using Twice1200.Core.Configuration;
using Twice1200.Core.Tunnels;

namespace Twice1200.SelfTest;

/// <summary>
/// frp 配置生成与启动校验。
/// <para>
/// 最要紧的两条:
/// 一是 <b>token 绝不能出现在命令行里</b>(命令行对本机任何进程可读);
/// 二是 <b>TOML 转义必须正确</b>——配置文件里任何未转义的引号或反斜杠
/// 都会让 frpc 直接报语法错误,而那种错误信息不会指出是我们的生成器写错了。
/// </para>
/// </summary>
public static class FrpTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        // ---------------------------------------------------------------- TOML 转义

        suite.Test("TOML 转义:普通字符串加引号", () =>
        {
            Check.Equal("\"hello\"", FrpcConfigGenerator.Quote("hello"), "普通字符串");
            Check.Equal("\"\"", FrpcConfigGenerator.Quote(""), "空字符串");
            Check.Equal("\"\"", FrpcConfigGenerator.Quote(null), "null 视为空字符串");
        });

        suite.Test("TOML 转义:双引号与反斜杠", () =>
        {
            // 这两类字符如果不转义,配置文件会直接语法错误
            Check.Equal("\"a\\\"b\"", FrpcConfigGenerator.Quote("a\"b"), "双引号");
            Check.Equal("\"a\\\\b\"", FrpcConfigGenerator.Quote("a\\b"), "反斜杠");
            Check.Equal("\"C:\\\\path\\\\x\"", FrpcConfigGenerator.Quote(@"C:\path\x"), "Windows 路径");
        });

        suite.Test("TOML 转义:控制字符用 \\uXXXX 形式", () =>
        {
            Check.Equal("\"a\\nb\"", FrpcConfigGenerator.Quote("a\nb"), "换行");
            Check.Equal("\"a\\tb\"", FrpcConfigGenerator.Quote("a\tb"), "制表符");
            Check.Equal("\"a\\rb\"", FrpcConfigGenerator.Quote("a\rb"), "回车");

            // 其它控制字符走 \uXXXX,这是唯一在所有情况下都安全的形式
            var quoted = FrpcConfigGenerator.Quote("a\u0001b");
            Check.Contains(quoted, "\\u0001", "不可打印控制字符应转义为 \\uXXXX");
            Check.False(quoted.Contains('\u0001'), "转义后不应残留原始控制字符");
        });

        suite.Test("TOML 转义:中文原样保留", () =>
        {
            // TOML 是 UTF-8 文本,中文不需要转义
            Check.Equal("\"以太网\"", FrpcConfigGenerator.Quote("以太网"), "中文应原样保留");
        });

        // ---------------------------------------------------------------- 配置生成

        suite.Test("frp 配置:包含全部必需字段", () =>
        {
            var toml = FrpcConfigGenerator.Generate(BuildValid(), "PROBE-TOKEN-NOT-REAL");

            Check.Contains(toml, "serverAddr =", "应有 serverAddr");
            Check.Contains(toml, "serverPort = 7000", "应有 serverPort");
            Check.Contains(toml, "auth.token =", "应有 auth.token");
            Check.Contains(toml, "transport.tls.enable = true", "应启用 TLS");
            Check.Contains(toml, "[[proxies]]", "应有 proxies 数组");
            Check.Contains(toml, "name = \"web\"", "应有规则名");
            Check.Contains(toml, "type = \"tcp\"", "应有类型");
            Check.Contains(toml, "localPort = 25565", "应有本地端口");
            Check.Contains(toml, "remotePort = 25565", "应有服务端端口");
        });

        suite.Test("frp 配置:TLS 可关闭", () =>
        {
            var configuration = BuildValid();
            configuration.RequireTls = false;

            var toml = FrpcConfigGenerator.Generate(configuration, "PROBE-TOKEN-NOT-REAL");
            Check.Contains(toml, "transport.tls.enable = false", "应反映关闭状态");
        });

        suite.Test("frp 配置:多条规则会生成多段", () =>
        {
            var configuration = BuildValid();
            configuration.Proxies.Add(new FrpProxy
            {
                Name = "second",
                Type = "tcp",
                LocalIp = "127.0.0.1",
                LocalPort = 25566,
                RemotePort = 25566,
            });

            var toml = FrpcConfigGenerator.Generate(configuration, "PROBE-TOKEN-NOT-REAL");

            var count = toml.Split("[[proxies]]").Length - 1;
            Check.Equal(2, count, "应有两段 [[proxies]]");
            Check.Contains(toml, "localPort = 25566", "第二条规则应存在");
        });

        suite.Test("frp 配置:token 里的特殊字符被正确转义", () =>
        {
            // 这是最容易被忽略的地方:token 是用户输入,可能含引号或反斜杠。
            const string tricky = "tok\"en\\with'quotes";

            var toml = FrpcConfigGenerator.Generate(BuildValid(), tricky);

            var tokenLine = toml.Split('\n')
                .First(l => l.StartsWith("auth.token", StringComparison.Ordinal))
                .TrimEnd('\r');

            Check.Equal("auth.token = \"tok\\\"en\\\\with'quotes\"", tokenLine, "整行应与预期完全一致");

            // 关键性质:首尾之外的双引号必须都处于转义状态。
            // 早先这里用"数引号个数"来断言 —— 那是错的:
            // 被转义的引号在字面上仍是一个 " 字符,数量本来就是 3。
            // 真正要验证的是"每个内部引号前面都有奇数个反斜杠"。
            Check.Equal('"', tokenLine[^1], "行尾应是闭合引号");

            var body = tokenLine[("auth.token = ".Length + 1)..^1];

            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] != '"')
                {
                    continue;
                }

                // 往前数连续反斜杠;奇数表示这个引号是被转义的
                var backslashes = 0;
                for (var j = i - 1; j >= 0 && body[j] == '\\'; j--)
                {
                    backslashes++;
                }

                Check.True(
                    backslashes % 2 == 1,
                    $"位置 {i} 的双引号没有被转义 —— 会破坏 TOML 结构");
            }
        });

        // ---------------------------------------------------------------- 参数构造

        suite.Test("frp 参数:token 绝不进入命令行", () =>
        {
            // 这是整个 frp 实现里最重要的安全约束。
            const string token = "SuperSecretToken-5566778899";

            var arguments = FrpcConfigGenerator.BuildArguments(@"C:\temp\frpc-abc.toml");

            foreach (var argument in arguments)
            {
                Check.DoesNotContain(argument, token, "命令行里不允许出现 token");
            }

            Check.False(arguments.Contains("-t"), "不应使用 -t 传 token");
            Check.False(arguments.Contains("--token"), "不应使用 --token 传 token");
            Check.Equal("-c", arguments[0], "应当只用 -c 指定配置文件");
        });

        // ---------------------------------------------------------------- 启动校验

        suite.Test("frp 校验:合法配置没有问题", () =>
        {
            var problems = FrpTunnel.Validate(BuildValidOptions());
            Check.Equal(0, problems.Count, "不应有问题:" + string.Join(" / ", problems));
        });

        suite.Test("frp 校验:缺少服务端地址", () =>
        {
            var options = BuildValidOptions() with { ServerAddress = "" };
            var problems = FrpTunnel.Validate(options);

            Check.True(problems.Any(p => p.Contains("服务端地址", StringComparison.Ordinal)), "应报缺失");
        });

        suite.Test("frp 校验:服务端地址带协议前缀", () =>
        {
            var options = BuildValidOptions() with { ServerAddress = "http://example.com" };
            var problems = FrpTunnel.Validate(options);

            Check.True(
                problems.Any(p => p.Contains("协议前缀", StringComparison.Ordinal)),
                "应指出协议前缀问题");
        });

        suite.Test("frp 校验:缺少 token", () =>
        {
            var options = BuildValidOptions() with { AuthToken = "" };
            var problems = FrpTunnel.Validate(options);

            Check.True(problems.Any(p => p.Contains("token", StringComparison.Ordinal)), "应报缺少 token");
        });

        suite.Test("frp 校验:没有映射规则", () =>
        {
            var options = BuildValidOptions() with { Proxies = [] };
            var problems = FrpTunnel.Validate(options);

            Check.True(problems.Any(p => p.Contains("映射规则", StringComparison.Ordinal)), "应报没有规则");
        });

        suite.Test("frp 校验:tcp 与 udp 都接受", () =>
        {
            // udp 对游戏联机是刚需(大多数游戏走 UDP),
            // 因此它属于要支持的常规类型,而不是"不支持的类型"。
            foreach (var type in new[] { "tcp", "udp", "TCP", "UDP" })
            {
                var options = BuildValidOptions() with
                {
                    Proxies = [new FrpProxy { Name = "p", Type = type, LocalPort = 25565, RemotePort = 25565 }],
                };

                var problems = FrpTunnel.Validate(options);
                Check.Equal(0, problems.Count, $"类型「{type}」应被接受:" + string.Join(" / ", problems));
            }
        });

        suite.Test("frp 校验:tcp 与 udp 可以用同一个端口号", () =>
        {
            // 实测用 frpc verify 确认过:同一端口号上 tcp 与 udp 共存是合法的。
            // 因此端口冲突只在**同一协议内**才算冲突。
            var options = BuildValidOptions() with
            {
                Proxies =
                [
                    new FrpProxy { Name = "game-tcp", Type = "tcp", LocalPort = 25565, RemotePort = 25565 },
                    new FrpProxy { Name = "game-udp", Type = "udp", LocalPort = 25565, RemotePort = 25565 },
                ],
            };

            var problems = FrpTunnel.Validate(options);
            Check.Equal(0, problems.Count, "tcp 与 udp 同端口不应报冲突:" + string.Join(" / ", problems));
        });

        suite.Test("frp 校验:同一协议内端口冲突仍被拒绝", () =>
        {
            var options = BuildValidOptions() with
            {
                Proxies =
                [
                    new FrpProxy { Name = "a", Type = "tcp", LocalPort = 1001, RemotePort = 2001 },
                    new FrpProxy { Name = "b", Type = "tcp", LocalPort = 1002, RemotePort = 2001 },
                ],
            };

            var problems = FrpTunnel.Validate(options);
            Check.True(problems.Any(p => p.Contains("2001", StringComparison.Ordinal)), "应报端口冲突");
        });

        suite.Test("frp 校验:不支持的类型被明确拒绝", () =>
        {
            // 这些类型需要额外的域名或访问者配置,结构上与"本地端口→服务端端口"不同,
            // 因此明确拒绝,而不是让 frpc 在启动后才失败。
            foreach (var type in new[] { "http", "https", "stcp", "xtcp" })
            {
                var options = BuildValidOptions() with
                {
                    Proxies = [new FrpProxy { Name = "x", Type = type, LocalPort = 1001, RemotePort = 2001 }],
                };

                var problems = FrpTunnel.Validate(options);
                Check.True(
                    problems.Any(p => p.Contains(type, StringComparison.Ordinal)),
                    $"类型「{type}」应被拒绝并指明类型名");
            }
        });

        suite.Test("frp 校验:端口越界", () =>
        {
            var options = BuildValidOptions() with { ServerPort = 99999 };
            var problems = FrpTunnel.Validate(options);

            Check.True(problems.Any(p => p.Contains("99999", StringComparison.Ordinal)), "应报端口越界");
        });

        // ---------------------------------------------------------------- 日志判据

        suite.Test("frp 日志:识别成功登录与代理启动", () =>
        {
            // 这两条字符串来自对 frpc 0.68.0 的实测输出,不是猜的
            const string log = """
                2026-10-12 01:56:57.625 [I] [client/service.go:370] [476230c29ba108a1] login to server success, get run id [476230c29ba108a1]
                2026-10-12 01:56:57.681 [I] [client/control.go:176] [476230c29ba108a1] [probe] start proxy success
                """;

            Check.True(FrpTunnel.HasLoginSuccess(log), "应识别登录成功");
            Check.True(FrpTunnel.HasProxyStarted(log), "应识别代理启动");
            Check.False(FrpTunnel.HasAuthFailure(log), "不应误报鉴权失败");
            Check.False(FrpTunnel.HasConnectionFailure(log), "不应误报连接失败");
        });

        suite.Test("frp 日志:识别 token 不匹配", () =>
        {
            const string log = """
                2026-10-12 01:57:06.565 [E] [client/service.go:363] token in login doesn't match token from configuration
                login to the server failed: token in login doesn't match token from configuration.
                """;

            Check.True(FrpTunnel.HasAuthFailure(log), "应识别鉴权失败");
            Check.True(FrpTunnel.HasConnectionFailure(log), "也含整体失败标志");
            Check.False(FrpTunnel.HasProxyStarted(log), "不应误报成功");
        });

        suite.Test("frp 日志:识别服务端不可达", () =>
        {
            const string log = """
                2026-10-12 01:57:13.709 [W] [client/service.go:381] connect to server error: dial tcp 127.0.0.1:9: connectex: No connection could be made because the target machine actively refused it.
                login to the server failed: dial tcp 127.0.0.1:9 ...
                """;

            Check.True(FrpTunnel.HasConnectionFailure(log), "应识别连接失败");
            Check.False(FrpTunnel.HasAuthFailure(log), "不应误报鉴权失败");
        });

        // ---------------------------------------------------------------- ANSI 清理

        suite.Test("ANSI:颜色转义被剥掉", () =>
        {
            // 实测确认 frpc 即使输出被重定向也会带颜色序列,
            // 它们会污染界面显示并让字符串匹配不可靠。
            const string colored = "\u001b[1;34m2026-10-12 hello\u001b[0m";
            var plain = FrpTunnel.StripAnsi(colored);

            Check.Equal("2026-10-12 hello", plain, "应只剩下正文");
            Check.False(plain.Contains('\u001b'), "不应残留 ESC");
        });

        suite.Test("ANSI:普通文本不被改动", () =>
        {
            const string plain = "login to server success";
            Check.Equal(plain, FrpTunnel.StripAnsi(plain), "无转义时不应改动");
        });

        suite.Test("ANSI:剥掉后仍能匹配判据", () =>
        {
            // 这条守着一个真实风险:如果只在某一处清理转义,
            // 就会出现"界面看不到但匹配不到"或反过来的不一致。
            const string colored = "\u001b[1;34m[client/service.go:370] login to server success\u001b[0m";
            var plain = FrpTunnel.StripAnsi(colored);

            Check.True(FrpTunnel.HasLoginSuccess(plain), "剥掉 ANSI 后应能匹配");
        });

        suite.Test("ANSI:空输入不崩溃", () =>
        {
            Check.Equal(string.Empty, FrpTunnel.StripAnsi(null), "null 应返回空串");
            Check.Equal(string.Empty, FrpTunnel.StripAnsi(""), "空串应返回空串");
        });
    }

    /// <summary>构造一份合法的 frp 配置(仅用于测试,不连任何真实服务端)。</summary>
    private static FrpConfiguration BuildValid() => new()
    {
        ServerAddress = "127.0.0.1",
        ServerPort = 7000,
        RequireTls = true,
        Proxies =
        [
            new FrpProxy
            {
                Name = "web",
                Type = "tcp",
                LocalIp = "127.0.0.1",
                LocalPort = 25565,
                RemotePort = 25565,
            },
        ],
    };

    /// <summary>构造一份合法的启动参数。token 是明显的假值 —— 测试里不允许出现真实凭据。</summary>
    private static FrpLaunchOptions BuildValidOptions() => new()
    {
        FrpcPath = "frpc.exe",
        ServerAddress = "127.0.0.1",
        ServerPort = 7000,
        AuthToken = "PROBE-TOKEN-NOT-REAL",
        RequireTls = true,
        Proxies =
        [
            new FrpProxy
            {
                Name = "web",
                Type = "tcp",
                LocalIp = "127.0.0.1",
                LocalPort = 25565,
                RemotePort = 25565,
            },
        ],
    };
}
