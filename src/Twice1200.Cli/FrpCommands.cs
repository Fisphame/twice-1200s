using System.Text;
using Twice1200.Core.Assets;
using Twice1200.Core.Configuration;
using Twice1200.Core.Runtime;
using Twice1200.Core.Security;
using Twice1200.Core.Tunnels;

namespace Twice1200.Cli;

/// <summary>
/// <c>frp</c> 子命令:启停与查看 frp 端口映射。
/// <para>
/// 与 n2n 那条链路共用 <see cref="ProcessSupervisor"/> / <see cref="TunnelState"/> /
/// <see cref="CredentialStore"/>,所以这一层很薄。
/// </para>
/// </summary>
internal static class FrpCommands
{
    /// <summary>处理 <c>frp</c> 子命令。</summary>
    public static async Task<int> RunAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var action = reader.Positional.Count > 1 ? reader.Positional[1].ToLowerInvariant() : "status";

        return action switch
        {
            "up" or "start" or "connect" => await RunUpAsync(reader, cancellationToken).ConfigureAwait(false),
            "status" or "show" => RunStatus(reader),
            "down" or "stop" or "disconnect" => RunDown(reader),
            "config" => RunConfig(reader),
            _ => throw new CliUsageException($"未知的 frp 子命令「{action}」。可用:up / status / down / config"),
        };
    }

    // ================================================================
    //  frp config —— 只展示将要生成的配置(密钥打码)
    // ================================================================

    private static int RunConfig(ArgumentReader reader)
    {
        var configuration = ConfigurationCommands.LoadOrTemplate(reader.GetString("config"));
        var options = BuildOptions(reader, configuration, out _, out var problems, out var tokenSource);

        Console.WriteLine("── 将生成的 frpc 配置(不会写入文件)──");
        Console.WriteLine();

        var toml = FrpcConfigGenerator.Generate(
            new FrpConfiguration
            {
                ServerAddress = options.ServerAddress,
                ServerPort = options.ServerPort,
                RequireTls = options.RequireTls,
                Proxies = options.Proxies.ToList(),
            },
            options.AuthToken);

        // 把 token 行打码:配置文件里它是明文,但屏幕上不必显示
        foreach (var line in toml.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("auth.token", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  auth.token = \"[已打码 — {Redaction.Describe(options.AuthToken)}]\"");
            }
            else
            {
                Console.WriteLine("  " + trimmed);
            }
        }

        Console.WriteLine();
        Console.WriteLine("── 关键检查 ──");
        Console.WriteLine($"  token 是否进命令行   : 否(只写进临时配置文件)");
        Console.WriteLine($"  token 来源           : {tokenSource}");
        Console.WriteLine($"  TLS                  : {(options.RequireTls ? "启用" : "未启用 ← 建议开启")}");
        Console.WriteLine($"  映射规则数           : {options.Proxies.Count}");
        Console.WriteLine($"  生成的配置文件位置   : 运行期临时目录,启动后立即删除");

        if (problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("── 配置问题 ──");
            foreach (var problem in problems)
            {
                Console.WriteLine("  · " + problem);
            }
        }

        return 0;
    }

    // ================================================================
    //  frp up
    // ================================================================

    private static async Task<int> RunUpAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var configuration = ConfigurationCommands.LoadOrTemplate(reader.GetString("config"));
        var options = BuildOptions(reader, configuration, out var assets, out _, out var tokenSource);

        if (reader.GetFlag("dry-run"))
        {
            var frpc = assets.Resolve(AssetPaths.Frpc) ?? @"<资源目录>\frp\frpc.exe";
            Console.WriteLine(CommandLineFormatter.Format(
                frpc,
                FrpcConfigGenerator.BuildArguments(@"<运行期临时目录>\frpc-<随机>.toml")));
            Console.WriteLine("（--dry-run:未生成配置,也未启动任何进程）");
            return 0;
        }

        Console.WriteLine($"── frp 连接 ──");
        Console.WriteLine($"  服务端   : {options.ServerAddress}:{options.ServerPort}");
        Console.WriteLine($"  TLS      : {(options.RequireTls ? "启用" : "未启用")}");
        Console.WriteLine($"  映射规则 : {options.Proxies.Count} 条");
        foreach (var proxy in options.Proxies)
        {
            Console.WriteLine($"      「{proxy.Name}」 {proxy.LocalIp}:{proxy.LocalPort}  →  服务端 {proxy.RemotePort}");
        }

        Console.WriteLine($"  token    : {tokenSource}");
        Console.WriteLine();

        using var tunnel = new FrpTunnel();
        tunnel.OutputReceived += (_, line) => Console.WriteLine("  [frpc] " + line);

        Console.WriteLine("[*] 启动 frpc…");
        Console.WriteLine();

        var result = await tunnel.StartAsync(options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine((result.IsSuccess ? "[+] " : "[×] ") + result.Message);

        if (result.Hints is { Count: > 0 })
        {
            Console.WriteLine();
            foreach (var hint in result.Hints)
            {
                Console.WriteLine("    · " + hint);
            }
        }

        if (!result.IsSuccess)
        {
            return ExitCodeFor(result.Status);
        }

        Console.WriteLine();
        Console.WriteLine("[*] 保持运行中 —— 按 Ctrl+C 断开。");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("[*] 收到中断,正在断开…");
        }

        await tunnel.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Console.WriteLine("[+] 已断开。");
        return 0;
    }

    // ================================================================
    //  frp status / down
    // ================================================================

    private static int RunStatus(ArgumentReader reader)
    {
        var configuration = ConfigurationCommands.LoadOrTemplate(reader.GetString("config"));
        var assets = AssetLocator.Resolve(reader.GetString("assets"));

        Console.WriteLine("── frp 状态 ──");
        Console.WriteLine();

        var processes = ProcessSnapshotter.FindByName("frpc", "frps");
        Console.WriteLine($"  相关进程 : {(processes.Count == 0 ? "无" : $"{processes.Count} 个")}");
        foreach (var process in processes)
        {
            Console.WriteLine($"      {process.Name}  PID {process.Id}");
        }

        Console.WriteLine();
        Console.WriteLine("── 配置 ──");
        Console.WriteLine($"  服务端     : {configuration.Frp.ServerAddress}:{configuration.Frp.ServerPort}");
        Console.WriteLine($"  TLS        : {(configuration.Frp.RequireTls ? "要求" : "未要求")}");
        Console.WriteLine($"  映射规则   : {configuration.Frp.Proxies.Count} 条");
        foreach (var proxy in configuration.Frp.Proxies)
        {
            Console.WriteLine($"      「{proxy.Name}」 {proxy.LocalIp}:{proxy.LocalPort}  →  {proxy.RemotePort}");
        }

        Console.WriteLine();
        Console.WriteLine("── 凭据 ──");
        var store = new CredentialStore();
        var handle = configuration.Frp.AuthToken.Handle;

        if (string.IsNullOrWhiteSpace(handle))
        {
            handle = CredentialHandles.FrpAuthToken;
        }

        if (store.TryGetSecret(handle, out var token, out var credentialResult))
        {
            // 只报告形状,不回显内容
            Console.WriteLine($"  token : 已保存(键「{handle}」,长度 {token!.Length},指纹 {Redaction.Fingerprint(token)})");
        }
        else
        {
            Console.WriteLine($"  token : 不可用 — {credentialResult.Message}");
            Console.WriteLine($"          请先保存:{SelfName} credential set {handle}");
        }

        Console.WriteLine();
        Console.WriteLine("── frpc.exe ──");
        var frpc = assets.Resolve(AssetPaths.Frpc);
        Console.WriteLine($"  路径 : {frpc ?? "(资源目录未解析)"}");

        return 0;
    }

    private static int RunDown(ArgumentReader reader)
    {
        var targets = ProcessSnapshotter.FindByName("frpc");

        if (targets.Count == 0)
        {
            Console.WriteLine("没有正在运行的 frpc 进程。");
            return 0;
        }

        Console.WriteLine($"发现 {targets.Count} 个 frpc 进程:");

        foreach (var process in targets)
        {
            Console.WriteLine($"  PID {process.Id}");
        }

        if (!reader.GetFlag("force"))
        {
            Console.Write("确认结束它们?(y/N) ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().StartsWith('y'))
            {
                Console.WriteLine("已取消。");
                return 0;
            }
        }

        var stopped = 0;
        foreach (var process in targets)
        {
            try
            {
                using var target = System.Diagnostics.Process.GetProcessById(process.Id);
                target.Kill(entireProcessTree: true);
                target.WaitForExit(5000);
                stopped++;
                Console.WriteLine($"  已结束 PID {process.Id}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  无法结束 PID {process.Id}:{ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[+] 已结束 {stopped} / {targets.Count} 个进程。");
        return stopped == targets.Count ? 0 : 1;
    }

    // ================================================================
    //  辅助
    // ================================================================

    private static FrpLaunchOptions BuildOptions(
        ArgumentReader reader,
        AppConfiguration? configuration,
        out AssetLocator assets,
        out IReadOnlyList<string> problems,
        out string tokenSource)
    {
        assets = AssetLocator.Resolve(reader.GetString("assets"));

        var frp = configuration?.Frp;

        // token 来源:命令行(仅测试) → 凭据库
        var token = reader.GetString("token");

        if (token is not null)
        {
            tokenSource = "命令行参数(--token,仅适合测试)";
        }
        else
        {
            var handle = frp?.AuthToken.Handle;
            if (string.IsNullOrWhiteSpace(handle))
            {
                handle = CredentialHandles.FrpAuthToken;
            }

            var store = new CredentialStore();
            if (store.TryGetSecret(handle, out var stored, out _))
            {
                token = stored;
                tokenSource = $"凭据库(键「{handle}」)";
            }
            else
            {
                token = string.Empty;
                tokenSource = $"未找到 —— 请先保存:{SelfName} credential set {handle}";
            }
        }

        var serverAddress = reader.GetString("server")
                            ?? frp?.ServerAddress
                            ?? string.Empty;

        var serverPort = reader.GetInt("port")
                         ?? frp?.ServerPort
                         ?? 7000;

        var requireTls = !reader.GetFlag("no-tls") && (frp?.RequireTls ?? true);

        // 映射规则:命令行可覆盖 local/remote 端口,其余沿用配置
        var proxies = frp?.Proxies is { Count: > 0 }
            ? frp.Proxies.ToList()
            : [];

        var localPort = reader.GetInt("local-port");
        var remotePort = reader.GetInt("remote-port");

        if (localPort is not null || remotePort is not null)
        {
            if (proxies.Count == 0)
            {
                proxies.Add(new FrpProxy
                {
                    Name = reader.GetString("name") ?? "web",
                    Type = "tcp",
                    LocalIp = "127.0.0.1",
                    LocalPort = localPort ?? 25565,
                    RemotePort = remotePort ?? localPort ?? 25565,
                });
            }
            else
            {
                var first = proxies[0];
                proxies[0] = new FrpProxy
                {
                    Name = first.Name,
                    Type = first.Type,
                    LocalIp = first.LocalIp,
                    LocalPort = localPort ?? first.LocalPort,
                    RemotePort = remotePort ?? first.RemotePort,
                };
            }
        }

        var options = new FrpLaunchOptions
        {
            FrpcPath = assets.Resolve(AssetPaths.Frpc) ?? "frpc.exe",
            ServerAddress = serverAddress,
            ServerPort = serverPort,
            AuthToken = token ?? string.Empty,
            RequireTls = requireTls,
            Proxies = proxies,
        };

        problems = FrpTunnel.Validate(options);
        return options;
    }

    private static int ExitCodeFor(FrpStartStatus status) => status switch
    {
        FrpStartStatus.InvalidConfiguration => 64,
        FrpStartStatus.ExecutableMissing => 72,
        FrpStartStatus.AuthFailed => 77,      // EX_NOPERM 的近似
        FrpStartStatus.ServerUnreachable => 69, // EX_UNAVAILABLE
        _ => 1,
    };

    private static string SelfName
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? "twice1200" : Path.GetFileName(path);
        }
    }
}
