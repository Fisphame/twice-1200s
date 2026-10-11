using Twice1200.Core.Runtime;
using Twice1200.Core.Tunnels;

namespace Twice1200.SelfTest;

/// <summary>
/// n2n 隧道层的参数拼装与进程管理。
/// <para>
/// 这些用例守着一个具体的、真实发生过的 bug:网卡名"以太网 2"含空格,
/// 手工拼接命令行时被拆成两个参数,现象是 edge 报"找不到 TAP 网卡"。
/// 所以最关键的断言是"参数列表里网卡名是完整的一项"。
/// </para>
/// </summary>
public static class TunnelTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        // ---------------------------------------------------------------- 参数拼装

        suite.Test("n2n 参数:含空格的网卡名保持为独立的一项", () =>
        {
            var arguments = N2nTunnel.BuildArguments(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "以太网 2",
                Community = "some-community",
                Supernode = "1200s.top:7777",
                AssignedIp = "10.236.167.114",
            });

            // 这是核心断言:-d 后面紧跟的就是完整的网卡名,没有被拆开
            var index = arguments.ToList().IndexOf("-d");
            Check.True(index >= 0, "应包含 -d 参数");
            Check.Equal("以太网 2", arguments[index + 1], "-d 后面的值必须是完整的网卡名");
            Check.False(
                arguments.Contains("2"),
                "不应出现独立的「2」—— 那意味着网卡名被空格拆开了");
        });

        suite.Test("n2n 参数:绝不自带引号", () =>
        {
            // 自行加引号会让引号成为名称的一部分,同样打不开网卡。
            // 转义由运行时的参数列表负责。
            var arguments = N2nTunnel.BuildArguments(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "以太网 2",
                Community = "c",
                Supernode = "s:1",
            });

            foreach (var argument in arguments)
            {
                Check.False(
                    argument.StartsWith('"') || argument.EndsWith('"'),
                    $"参数不应自带引号,但出现了「{argument}」");
            }
        });

        suite.Test("n2n 参数:指定了 IP 才传 -a", () =>
        {
            var withIp = N2nTunnel.BuildArguments(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "n2n-TAP",
                Community = "c",
                Supernode = "s:1",
                AssignedIp = "10.0.0.5",
            });
            Check.True(withIp.Contains("-a"), "指定 IP 时应包含 -a");
            Check.Equal("10.0.0.5", withIp[withIp.ToList().IndexOf("-a") + 1], "-a 的值");

            // 不传 -a 会让 edge 走"向超级节点要 IP"的路径(先去要 IP 而不打开网卡)
            var withoutIp = N2nTunnel.BuildArguments(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "n2n-TAP",
                Community = "c",
                Supernode = "s:1",
                AssignedIp = null,
            });
            Check.False(withoutIp.Contains("-a"), "未指定 IP 时不应包含 -a");
        });

        suite.Test("n2n 参数:社区名与超级节点正确传入", () =>
        {
            var arguments = N2nTunnel.BuildArguments(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "n2n-TAP",
                Community = "my-community",
                Supernode = "host.example:7777",
            });

            var list = arguments.ToList();
            Check.Equal("my-community", list[list.IndexOf("-c") + 1], "-c 的值");
            Check.Equal("host.example:7777", list[list.IndexOf("-l") + 1], "-l 的值");
        });

        // ---------------------------------------------------------------- 密钥处理

        suite.Test("n2n 密钥:绝不出现在参数列表里", () =>
        {
            const string secret = "MySecret-Key-9876543210";

            var options = new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "以太网 2",
                Community = "c",
                Supernode = "s:1",
                Key = secret,
            };

            var arguments = N2nTunnel.BuildArguments(options);

            foreach (var argument in arguments)
            {
                Check.DoesNotContain(argument, secret, "命令行里不允许出现密钥");
            }

            Check.False(arguments.Contains("-k"), "不应使用 -k 参数传递密钥");
        });

        suite.Test("n2n 密钥:通过 N2N_KEY 环境变量传递", () =>
        {
            const string secret = "MySecret-Key-9876543210";

            var environment = N2nTunnel.BuildEnvironment(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "n2n-TAP",
                Community = "c",
                Supernode = "s:1",
                Key = secret,
            });

            Check.True(
                environment.ContainsKey(N2nTunnel.KeyEnvironmentVariable),
                $"应设置 {N2nTunnel.KeyEnvironmentVariable}");
            Check.Equal(
                secret,
                environment[N2nTunnel.KeyEnvironmentVariable],
                "环境变量里应是原始密钥");
        });

        suite.Test("n2n 密钥:未设置时不注入空变量", () =>
        {
            var environment = N2nTunnel.BuildEnvironment(new N2nLaunchOptions
            {
                EdgePath = "edge.exe",
                AdapterName = "n2n-TAP",
                Community = "c",
                Supernode = "s:1",
                Key = null,
            });

            Check.Equal(0, environment.Count, "没有密钥就不应注入任何环境变量");
        });

        // ---------------------------------------------------------------- 命令行渲染

        suite.Test("命令行渲染:含空格的值显示引号", () =>
        {
            var rendered = CommandLineFormatter.Format(
                @"C:\path\edge.exe",
                ["-d", "以太网 2", "-c", "community"]);

            Check.Contains(rendered, "-d \"以太网 2\"", "含空格的值必须显示引号,否则日志看不出分隔方式");
            Check.Contains(rendered, "-c community", "不含空格的值不需要引号");
        });

        suite.Test("命令行渲染:能一眼看出被拆开的错误形态", () =>
        {
            // 正确形态与错误形态的渲染结果必须不同,否则这个工具就没有价值
            var correct = CommandLineFormatter.Format("edge.exe", ["-d", "以太网 2"]);
            var broken = CommandLineFormatter.Format("edge.exe", ["-d", "以太网", "2"]);

            Check.Equal("edge.exe -d \"以太网 2\"", correct, "正确形态");
            Check.Equal("edge.exe -d 以太网 2", broken, "错误形态应能看出多了一个参数");
            Check.True(correct != broken, "两者必须可区分");
        });

        suite.Test("命令行渲染:空参数被显式标出", () =>
        {
            var rendered = CommandLineFormatter.FormatArguments(["-d", ""]);
            Check.Contains(rendered, "\"\"", "空参数应显示为空引号,而不是消失");
        });

        suite.Test("命令行渲染:与实际生成的命令行逐字一致", () =>
        {
            // 这个用例是核心正确性保证:渲染工具的价值完全取决于
            // "它显示的就是子进程真实看到的"。若两者不一致,它会把人引向错误结论。
            //
            // 夹具说明:这里用 cmd.exe 的 %* 回显参数。cmd 按自己的控制台代码页
            // 输出,因此在中文系统上无法可靠回显非 ASCII 内容 ——
            // 这是**夹具的限制,不是被测代码的问题**,所以本用例只用 ASCII 值。
            // 含非 ASCII 的转义行为已在 .probe-edge/cmdline-check 里用
            // P/Invoke + UTF-8 的辅助程序逐字核对过(四种情况全部一致)。
            var cmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");

            var cases = new (string Label, string[] Values)[]
            {
                ("含空格的值", ["name=adapter 2", "newname=n2n-TAP"]),
                ("不含空格", ["name=n2n-TAP", "newname=abc"]),
                ("多个含空格参数", ["-d", "a b c", "-c", "x y"]),
                ("单个含空格参数", ["-d", "local area connection 2"]),
                ("值里含等号与空格", ["name=a=b c"]),
            };

            var compared = 0;

            foreach (var (label, values) in cases)
            {
                var echoed = CaptureCmdArguments(cmd, values);

                if (echoed is null)
                {
                    continue; // 环境不允许时跳过该项,而不是报假失败
                }

                var mine = CommandLineFormatter.FormatArguments(values);

                Check.Equal(
                    mine,
                    echoed,
                    $"[{label}] 渲染结果必须与子进程真实收到的命令行一致");

                compared++;
            }

            Check.True(compared > 0, "至少应有一项完成对比,否则本用例没有验证任何东西");
        });

        suite.Test("命令行渲染:含空格的键值对整体成为一项", () =>
        {
            // netsh 的 name= 形式最容易出问题:空格在值里,
            // 若不加引号会被拆成 name=以太网 和 2。
            var rendered = CommandLineFormatter.FormatArguments(["name=以太网 2"]);

            Check.Equal("\"name=以太网 2\"", rendered, "整个键值对应被引号包住");
        });

        // ---------------------------------------------------------------- 崩溃路径

        suite.Test("n2n 启动:网卡名为空时立即拒绝,不启动进程", () =>
        {
            var result = new N2nTunnel().StartAsync(new N2nLaunchOptions
            {
                EdgePath = "does-not-matter.exe",
                AdapterName = "",
                Community = "c",
                Supernode = "s:1",
            }).GetAwaiter().GetResult();

            Check.Equal(TunnelStartStatus.InvalidConfiguration, result.Status, "应判为配置非法");
            Check.Contains(result.Message, "网卡名", "提示应指出是网卡名的问题");
        });

        suite.Test("n2n 启动:网卡名含引号时拒绝", () =>
        {
            var result = new N2nTunnel().StartAsync(new N2nLaunchOptions
            {
                EdgePath = "does-not-matter.exe",
                AdapterName = "bad\"name",
                Community = "c",
                Supernode = "s:1",
            }).GetAwaiter().GetResult();

            Check.Equal(TunnelStartStatus.InvalidConfiguration, result.Status, "含引号应被拒绝");
        });

        suite.Test("n2n 启动:edge.exe 不存在时明确报告", () =>
        {
            var missing = Path.Combine(Path.GetTempPath(), $"no-such-edge-{Guid.NewGuid():N}.exe");

            var result = new N2nTunnel().StartAsync(new N2nLaunchOptions
            {
                EdgePath = missing,
                AdapterName = "n2n-TAP",
                Community = "c",
                Supernode = "s:1",
            }).GetAwaiter().GetResult();

            Check.Equal(TunnelStartStatus.ExecutableMissing, result.Status, "应报告可执行文件缺失");
        });

        suite.Test("n2n 启动:标准权限下提前拦住并说明原因", () =>
        {
            // 本自检程序通常以标准权限运行。若当前恰好是管理员,则跳过此用例。
            if (N2nTunnel.IsElevated())
            {
                return;
            }

            var edge = FindEdgeExe();
            if (edge is null)
            {
                return; // 没有 edge.exe 就无法验证
            }

            var result = new N2nTunnel().StartAsync(new N2nLaunchOptions
            {
                EdgePath = edge,
                AdapterName = "Twice1200SelfTestNoSuchAdapter",
                Community = "c",
                Supernode = "127.0.0.1:1",
            }).GetAwaiter().GetResult();

            Check.Equal(
                TunnelStartStatus.NotElevated,
                result.Status,
                "标准权限下应在启动前就拦住,而不是让它跑起来再报含糊的错误");
            Check.Contains(result.Message, "管理员", "提示应点明需要管理员权限");
        });

        // ---------------------------------------------------------------- 进程管理

        suite.Test("进程管理:能启动、捕获输出、正常结束", () =>
        {
            var supervisor = new ProcessSupervisor();

            var request = new ProcessLaunchRequest
            {
                ExecutablePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                Arguments = ["/c", "echo", "hello-from-selftest"],
                RunningDetector = null,
                StartupWindow = TimeSpan.FromSeconds(5),
            };

            var startResult = supervisor.StartAsync(request).GetAwaiter().GetResult();
            Check.True(startResult.Started, $"进程应能启动:{startResult.Failure}");

            var final = supervisor.WaitForExitAsync().GetAwaiter().GetResult();

            Check.Contains(final.Output, "hello-from-selftest", "应捕获到子进程的输出");

            supervisor.Dispose();
        });

        suite.Test("进程管理:输出里的密钥被脱敏", () =>
        {
            const string secret = "SuperSecretValueToRedact123";

            var supervisor = new ProcessSupervisor();

            var secrets = new SecretSet();
            secrets.Add(secret);

            // 让 cmd 把"密钥"回显出来,模拟某个工具在详细模式下泄露参数
            var request = new ProcessLaunchRequest
            {
                ExecutablePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                Arguments = ["/c", "echo", $"leaked={secret}"],
                Secrets = secrets,
                RunningDetector = null,
                StartupWindow = TimeSpan.FromSeconds(5),
            };

            var startResult = supervisor.StartAsync(request).GetAwaiter().GetResult();
            Check.True(startResult.Started, "进程应能启动");

            var final = supervisor.WaitForExitAsync().GetAwaiter().GetResult();

            Check.DoesNotContain(final.Output, secret, "日志里绝不能残留密钥明文");
            Check.Contains(final.Output, "已脱敏", "应留下脱敏标记,便于确认过滤生效");

            supervisor.Dispose();
        });

        suite.Test("进程管理:可执行文件不存在时安全失败", () =>
        {
            var supervisor = new ProcessSupervisor();

            var result = supervisor.StartAsync(new ProcessLaunchRequest
            {
                ExecutablePath = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.exe"),
            }).GetAwaiter().GetResult();

            Check.False(result.Started, "不应报告启动成功");
            Check.Contains(result.Failure ?? string.Empty, "找不到", "应说明原因");
            Check.Equal(TunnelState.Failed, supervisor.State, "状态应为失败");

            supervisor.Dispose();
        });

        suite.Test("进程管理:重复启动被拒绝", () =>
        {
            var supervisor = new ProcessSupervisor();

            var request = new ProcessLaunchRequest
            {
                // 用 ping 保持存活几秒,以便测"重复启动"
                ExecutablePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "ping.exe"),
                Arguments = ["-n", "5", "127.0.0.1"],
                RunningDetector = null,
                StartupWindow = TimeSpan.FromSeconds(5),
            };

            var first = supervisor.StartAsync(request).GetAwaiter().GetResult();
            if (!first.Started)
            {
                supervisor.Dispose();
                return; // 环境不允许,跳过
            }

            var second = supervisor.StartAsync(request).GetAwaiter().GetResult();
            Check.False(second.Started, "第二次启动应被拒绝");
            Check.Contains(second.Failure ?? string.Empty, "已经", "应说明已有进程在运行");

            supervisor.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            supervisor.Dispose();
        });

        suite.Test("脱敏:未声明为密钥的值不会被抹掉", () =>
        {
            // 这是一个真实回归的守卫。
            //
            // 早先 ProcessSupervisor 会把 request.Environment 里的**所有**值都登记为密钥,
            // 于是 "以太网 2"、"10.236.167.114" 这类正常内容(含数字,恰好通过
            // SecretSet 的"看起来像密钥"启发式)被抹成了 [已脱敏] ——
            // 生产日志里的网卡名和 IP 因此全部消失,比不脱敏更糟。
            //
            // 夹具说明:这里用 ASCII 值,因为 cmd.exe 按控制台代码页输出,
            // 在本环境下无法可靠回显非 ASCII 内容 —— 那是夹具的限制,不是被测代码的问题。
            // 非 ASCII 参数能否完整送达,已由 .probe-edge/encoding-order 用
            // P/Invoke + 十六进制核对逐字确认过(中文与空格均完好)。
            const string adapterName = "adapter 2";
            const string ipAddress = "10.236.167.114";

            var supervisor = new ProcessSupervisor();

            var request = new ProcessLaunchRequest
            {
                ExecutablePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                Arguments = ["/c", "echo", $"{adapterName} {ipAddress}"],
                // 刻意放进 Environment,但不登记为密钥
                Environment = new Dictionary<string, string> { ["SOME_VALUE"] = adapterName },
                Secrets = new SecretSet(),
                RunningDetector = null,
                StartupWindow = TimeSpan.FromSeconds(5),
            };

            var start = supervisor.StartAsync(request).GetAwaiter().GetResult();
            Check.True(start.Started, "进程应能启动");

            var final = supervisor.WaitForExitAsync().GetAwaiter().GetResult();

            Check.Contains(final.Output, adapterName, "网卡名不应被脱敏");
            Check.Contains(final.Output, ipAddress, "IP 地址不应被脱敏");
            Check.DoesNotContain(final.Output, "[已脱敏]", "没有声明密钥就不该出现脱敏标记");

            supervisor.Dispose();
        });

        suite.Test("脱敏:显式声明的密钥仍然会被抹掉", () =>
        {
            // 与上一条配对:放宽启发式不能把真正的保护也一起丢掉。
            const string secret = "ExplicitSecretValue-556677889900";

            var supervisor = new ProcessSupervisor();
            var secrets = new SecretSet();
            secrets.Add(secret);

            var request = new ProcessLaunchRequest
            {
                ExecutablePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "cmd.exe"),
                Arguments = ["/c", "echo", $"adapter=adapter 2 key={secret}"],
                Secrets = secrets,
                RunningDetector = null,
                StartupWindow = TimeSpan.FromSeconds(5),
            };

            var start = supervisor.StartAsync(request).GetAwaiter().GetResult();
            Check.True(start.Started, "进程应能启动");

            var final = supervisor.WaitForExitAsync().GetAwaiter().GetResult();

            Check.DoesNotContain(final.Output, secret, "显式声明的密钥必须被抹掉");
            Check.Contains(final.Output, "已脱敏", "应留下脱敏标记");
            Check.Contains(final.Output, "adapter 2", "同时不应误伤普通内容");

            supervisor.Dispose();
        });

        suite.Test("SecretSet:登记、脱敏与去重", () =>
        {
            var set = new SecretSet();

            Check.Equal(0, set.Count, "初始为空");
            Check.Equal("nothing", set.Scrub("nothing"), "没有密钥时不改动文本");

            set.Add("ShortSecret1");
            set.Add("ShortSecret1");
            Check.Equal(1, set.Count, "重复登记应被忽略");

            set.Add("ab");
            Check.Equal(1, set.Count, "过短的值不应被登记");
            set.Add(null);
            set.Add("   ");
            Check.Equal(1, set.Count, "空值不应被登记");

            Check.DoesNotContain(set.Scrub("x ShortSecret1 y"), "ShortSecret1", "应被替换");
        });
    }

    /// <summary>
    /// 让 cmd.exe 回显它收到的参数,返回去掉可执行文件之后的命令行文本。
    /// <para>
    /// 用 <c>%*</c> 是因为它会原样保留引号,能真实反映命令行文本。
    /// 环境不允许时返回 <c>null</c>。
    /// </para>
    /// </summary>
    private static string? CaptureCmdArguments(string cmdPath, string[] values)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = cmdPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // 与 ProcessSupervisor 保持一致:不显式指定就会按 GBK 解码,中文变乱码
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            // /c echo 会把 %* 展开成原始参数文本
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("echo");
            foreach (var value in values)
            {
                startInfo.ArgumentList.Add(value);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            var trimmed = output.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            // cmd /c echo 在回显时会把引号去掉一层?实测不会 —— 它原样打印。
            // 但如果输出里出现了额外的转义,这里不做猜测,直接原样返回由断言暴露差异。
            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>尝试定位随包分发的 edge.exe;找不到时返回 null。</summary>
    private static string? FindEdgeExe()
    {
        var assets = Twice1200.Core.Assets.AssetLocator.Resolve();
        var edge = assets.Resolve(Twice1200.Core.Assets.AssetPaths.N2nEdge);

        return edge is not null && File.Exists(edge) ? edge : null;
    }
}
