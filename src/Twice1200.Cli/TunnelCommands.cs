using System.Text;
using Twice1200.Core.Assets;
using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics.Checks;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;
using Twice1200.Core.Security;
using Twice1200.Core.Tunnels;

namespace Twice1200.Cli;

/// <summary>配置文件的读取辅助。</summary>
internal static class ConfigurationCommands
{
    /// <summary>
    /// 读取配置文件;不存在或解析失败时退回一份模板。
    /// <para>
    /// 退回模板而不是报错,是为了让 <c>n2n args</c> 这类"只想看看命令长什么样"的
    /// 操作在尚未配置时也能用。
    /// </para>
    /// </summary>
    public static AppConfiguration LoadOrTemplate(string? explicitPath)
    {
        var path = explicitPath ?? AppPaths.ConfigurationFile;

        try
        {
            if (File.Exists(path))
            {
                return AppConfiguration.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
        }
        catch
        {
            // 解析失败就退回模板,由具体命令自行决定是否报错
        }

        return AppConfiguration.CreateTemplate();
    }
}

/// <summary>
/// <c>n2n</c> 子命令:启停与查看 n2n 连接。
/// <para>
/// 这一层刻意保持很薄 —— 所有逻辑都在 <see cref="N2nTunnel"/> 里。
/// 命令行的价值在于"能在没有界面的情况下验证核心功能",而不是把逻辑再写一遍。
/// </para>
/// </summary>
internal static class TunnelCommands
{
    /// <summary>
    /// 缺少配置时提示用的虚拟 IP。
    /// <para>取自原手工方案里的实际值,仅作为示例提示,不代表任何强制约定。</para>
    /// </summary>
    private const string FallbackVirtualIp = "10.42.0.2";

    /// <summary>处理 <c>n2n</c> 子命令。</summary>
    public static async Task<int> RunAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var action = reader.Positional.Count > 1 ? reader.Positional[1].ToLowerInvariant() : "status";

        return action switch
        {
            "up" or "start" or "connect" => await RunUpAsync(reader, cancellationToken).ConfigureAwait(false),
            "status" or "show" => RunStatus(reader),
            "down" or "stop" or "disconnect" => RunDown(reader),
            "args" => RunArgs(reader),
            _ => throw new CliUsageException($"未知的 n2n 子命令「{action}」。可用:up / status / down / args"),
        };
    }

    // ================================================================
    //  n2n args —— 只展示将要执行的命令,不启动任何东西
    // ================================================================

    /// <summary>
    /// 渲染将要执行的命令行,但不启动进程。
    /// <para>
    /// 这个子命令的存在是为了让参数拼装<b>可检查</b>。
    /// 含空格的网卡名到底有没有被正确保持成一项,看一眼输出就知道 ——
    /// 而这正是历史上出过错的地方。
    /// </para>
    /// </summary>
    private static int RunArgs(ArgumentReader reader)
    {
        var configuration = ConfigurationCommands.LoadOrTemplate(reader.GetString("config"));
        var options = BuildOptions(reader, configuration, out var assets, out var problems);

        Console.WriteLine("── 将要执行的命令(不会真正启动)──");
        Console.WriteLine();

        var edge = assets.Resolve(AssetPaths.N2nEdge) ?? @"<资源目录>\n2n\edge.exe";
        var arguments = N2nTunnel.BuildArguments(options);
        var environment = N2nTunnel.BuildEnvironment(options);

        Console.WriteLine("  " + CommandLineFormatter.Format(edge, arguments));
        Console.WriteLine();

        Console.WriteLine("── 参数逐项(共 " + arguments.Count + " 项)──");
        for (var i = 0; i < arguments.Count; i++)
        {
            var marker = arguments[i].Contains(' ') ? "   ← 含空格" : string.Empty;
            Console.WriteLine($"  [{i}] 「{arguments[i]}」{marker}");
        }

        Console.WriteLine();
        Console.WriteLine("── 环境变量(密钥走这里,不进命令行)──");
        if (environment.Count == 0)
        {
            Console.WriteLine("  (无)");
        }
        else
        {
            foreach (var (key, value) in environment)
            {
                // 只显示形状,不显示内容
                Console.WriteLine($"  {key} = ({Redaction.Describe(value)})");
            }
        }

        Console.WriteLine();
        Console.WriteLine("── 关键检查 ──");
        var adapterIndex = arguments.ToList().IndexOf("-d");
        var adapterOk = adapterIndex >= 0
                       && adapterIndex + 1 < arguments.Count
                       && string.Equals(arguments[adapterIndex + 1], options.AdapterName, StringComparison.Ordinal);

        Console.WriteLine("  -d 后面的网卡名是否完整 : " + (adapterOk ? "是" : "否 ← 有问题"));
        Console.WriteLine("  命令行里是否含密钥       : " +
                          (arguments.Any(a => !string.IsNullOrEmpty(options.Key) && a.Contains(options.Key, StringComparison.Ordinal))
                              ? "是 ← 泄露!"
                              : "否"));
        Console.WriteLine("  含空格的参数是否显示引号 : " +
                          (arguments.Any(a => a.Contains(' ')) ? "是(见上方渲染)" : "不适用"));

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
    //  n2n up
    // ================================================================

    private static async Task<int> RunUpAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var configuration = ConfigurationCommands.LoadOrTemplate(reader.GetString("config"));
        var options = BuildOptions(reader, configuration, out var assets, out _);

        // 密钥来源优先顺序:命令行(仅测试) → 凭据库
        var key = reader.GetString("key");
        if (key is null)
        {
            var handle = configuration?.N2n.Key.Handle;
            if (string.IsNullOrWhiteSpace(handle))
            {
                handle = CredentialHandles.N2nKey;
            }

            var store = new CredentialStore();
            if (store.TryGetSecret(handle, out var stored, out var credentialResult))
            {
                key = stored;
                Console.WriteLine($"[*] 已从凭据库取出密钥(键「{handle}」)。");
            }
            else
            {
                Console.Error.WriteLine("[错误] 没有可用的网络密码。");
                Console.Error.WriteLine($"       凭据库:{credentialResult.Message}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  请先保存密码(会用 DPAPI 加密,不落明文):");
                Console.Error.WriteLine($"      {SelfCommandName} credential set {handle}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  或者仅用于测试时临时指定:");
                Console.Error.WriteLine("      --key <密钥>");
                return 64;
            }
        }
        else
        {
            Console.WriteLine("[!] 使用了命令行传入的密钥(--key)。");
            Console.WriteLine("    注意:命令行对本机任何进程可见,仅适合测试。正常使用请保存到凭据库。");
        }

        var launchOptions = options with { Key = key };

        // 干跑:只渲染命令,不启动
        if (reader.GetFlag("dry-run"))
        {
            var edge = assets.Resolve(AssetPaths.N2nEdge) ?? @"<资源目录>\n2n\edge.exe";
            Console.WriteLine(CommandLineFormatter.Format(edge, N2nTunnel.BuildArguments(launchOptions)));
            Console.WriteLine("（--dry-run:未启动任何进程）");
            return 0;
        }

        var useLocal = reader.GetFlag("local-supernode");
        LocalSupernode? localSupernode = null;

        try
        {
            if (useLocal)
            {
                // 【重要】本地验证必须使用固定 IP。
                //
                // 不传 -a 时 edge 会接受超级节点分配的 IP,并把它写进虚拟网卡 ——
                // 实测中这会把网卡地址改成一个超级节点分配的随机地址,覆盖用户自己配的那个。
                // 一个诊断工具不该悄悄改用户的网络配置,所以这里要求显式指定 IP。
                if (string.IsNullOrWhiteSpace(launchOptions.AssignedIp))
                {
                    Console.Error.WriteLine("[错误] --local-supernode 需要配合 --virtual-ip 使用。");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  原因:不指定 IP 时,edge 会接受超级节点分配的地址并写入虚拟网卡,");
                    Console.Error.WriteLine("        这会改动你现有的网络配置。诊断工具不应有这样的副作用。");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"  请指定本机虚拟 IP,例如:--virtual-ip {FallbackVirtualIp}");
                    Console.Error.WriteLine("  或者把它写进配置文件的 n2n.assignedIp 字段。");
                    return 64;
                }

                var supernodePath = assets.Resolve(AssetPaths.N2nSupernode);
                if (supernodePath is null || !File.Exists(supernodePath))
                {
                    Console.Error.WriteLine("[错误] 找不到 supernode.exe,无法使用 --local-supernode。");
                    return 70;
                }

                var port = reader.GetInt("supernode-port", LocalSupernode.DefaultPort)!.Value;
                localSupernode = new LocalSupernode();

                Console.WriteLine($"[*] 在本机启动超级节点(UDP {port})…");

                var started = await localSupernode.StartAsync(supernodePath, port, cancellationToken)
                    .ConfigureAwait(false);

                if (!started)
                {
                    Console.Error.WriteLine("[错误] 本机超级节点启动失败。");
                    Console.Error.WriteLine($"      端口 {port} 可能已被占用。");
                    foreach (var line in localSupernode.LogLines.TakeLast(10))
                    {
                        Console.Error.WriteLine("      " + line);
                    }

                    return 70;
                }

                var listening = await localSupernode
                    .WaitUntilListeningAsync(TimeSpan.FromSeconds(8), cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine(listening
                    ? "[+] 超级节点已开始监听。"
                    : "[!] 未观察到明确的监听提示,继续尝试连接。");

                // 改写连接目标
                launchOptions = launchOptions with { Supernode = $"127.0.0.1:{port}" };
            }

            using var tunnel = new N2nTunnel();

            Console.WriteLine();
            Console.WriteLine($"[*] 启动 edge(超级节点 {launchOptions.Supernode})…");

            var result = await tunnel.StartAsync(launchOptions, cancellationToken).ConfigureAwait(false);

            ReportStart(result);

            if (!result.IsSuccess)
            {
                return ExitCodeFor(result.Status);
            }

            // 等握手 —— 这才是"真的连上了"
            Console.WriteLine("[*] 等待与超级节点完成握手(最多 20 秒)…");

            var handshake = await tunnel
                .WaitForHandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine();
            if (handshake)
            {
                Console.WriteLine("[+] 握手成功 —— 已与超级节点建立通信。");
            }
            else
            {
                Console.WriteLine("[×] 未完成握手 —— 超级节点没有回包。");
                Console.WriteLine();

                if (!useLocal)
                {
                    // 这里给的是"下一步该做什么",而不是又一句"失败了"。
                    // 本地回环是唯一能把责任分清的实验:本机能通说明服务端有问题。
                    Console.WriteLine("    下一步建议:用本机回环确认到底是客户端还是服务端的问题 ——");
                    Console.WriteLine($"        {SelfCommandName} n2n up --local-supernode \\");
                    Console.WriteLine($"            --community probe-{DateTime.Now:HHmmss} \\");
                    Console.WriteLine($"            --virtual-ip {launchOptions.AssignedIp ?? FallbackVirtualIp}");
                    Console.WriteLine();
                    Console.WriteLine("    本机能通、远端不通  →  服务端问题,继续在本机排查不会有结果。");
                    Console.WriteLine("    本机也不通          →  客户端问题,请先跑一次 doctor --probe-edge。");
                }
                else
                {
                    Console.WriteLine("    本机回环都不通,说明问题在客户端。请跑一次 doctor --probe-edge 看卡在哪一步。");
                }
            }

            if (useLocal && handshake)
            {
                Console.WriteLine();
                Console.WriteLine("[√] 本机回环验证通过:客户端功能正常。");
                Console.WriteLine("    这条结论可以直接用于判断将来的故障是在客户端还是服务端。");
            }

            Console.WriteLine();
            PrintLogTail(tunnel.LogLines);

            if (useLocal)
            {
                // 本地验证模式是目的性的:验证完就退出,不驻留
                return handshake ? 0 : 1;
            }

            // 连远程时需要保持运行,直到用户中断
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
            return handshake ? 0 : 1;
        }
        finally
        {
            if (localSupernode is not null)
            {
                await localSupernode.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                localSupernode.Dispose();
            }
        }
    }

    // ================================================================
    //  n2n status
    // ================================================================

    private static int RunStatus(ArgumentReader reader)
    {
        Console.WriteLine("── n2n 运行状态 ──");
        Console.WriteLine();

        var edge = ProcessSnapshotter.FindByName("edge");
        var supernode = ProcessSnapshotter.FindByName("supernode");

        Console.WriteLine($"  edge 进程      : {(edge.Count == 0 ? "无" : $"{edge.Count} 个")}");
        foreach (var process in edge)
        {
            Console.WriteLine($"      PID {process.Id}");
        }

        Console.WriteLine($"  supernode 进程 : {(supernode.Count == 0 ? "无" : $"{supernode.Count} 个")}");
        foreach (var process in supernode)
        {
            Console.WriteLine($"      PID {process.Id}");
        }

        Console.WriteLine();
        Console.WriteLine("  当前权限       : " + (N2nTunnel.IsElevated() ? "管理员" : "标准用户"));
        Console.WriteLine("      " + (N2nTunnel.IsElevated()
            ? "可以启动 edge。"
            : "无法启动 edge —— edge 需要管理员权限才能访问 TAP 网卡。请以管理员身份重新运行。"));

        Console.WriteLine();
        Console.WriteLine("── 虚拟网卡 ──");
        var taps = NetworkInterfaceInspector.FindTapLike();
        if (taps.Count == 0)
        {
            Console.WriteLine("  未检测到 TAP 类虚拟网卡。");
        }
        else
        {
            foreach (var nic in taps)
            {
                var addresses = nic.IPv4Addresses.Count > 0
                    ? string.Join(", ", nic.IPv4Addresses.Select(a => a.ToString()))
                    : "(无地址)";
                Console.WriteLine($"  {nic.Name}  [{nic.Description}]");
                Console.WriteLine($"      状态 {nic.Status}   地址 {addresses}");
            }
        }

        return 0;
    }

    // ================================================================
    //  n2n down
    // ================================================================

    private static int RunDown(ArgumentReader reader)
    {
        var targets = ProcessSnapshotter.FindByName("edge", "supernode");

        if (targets.Count == 0)
        {
            Console.WriteLine("没有正在运行的 edge 或 supernode 进程。");
            return 0;
        }

        Console.WriteLine($"发现 {targets.Count} 个相关进程:");

        foreach (var process in targets)
        {
            Console.WriteLine($"  {process.Name}  PID {process.Id}");
        }

        if (!reader.GetFlag("force"))
        {
            var answer = Prompt("确认结束它们?(y/N) ");
            if (!answer)
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
                Console.WriteLine($"  已结束 {process.Name} ( PID {process.Id} )");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  无法结束 PID {process.Id}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"[+] 已结束 {stopped} / {targets.Count} 个进程。");
        return stopped == targets.Count ? 0 : 1;
    }

    // ================================================================
    //  辅助
    // ================================================================

    private static N2nLaunchOptions BuildOptions(
        ArgumentReader reader,
        AppConfiguration? configuration,
        out AssetLocator assets,
        out IReadOnlyList<string> problems)
    {
        assets = AssetLocator.Resolve(reader.GetString("assets"));

        var n2n = configuration?.N2n;
        var issueList = new List<string>();

        var adapterName = reader.GetString("adapter")
                          ?? n2n?.Tap.AdapterName
                          ?? "n2n-TAP";

        var supernode = reader.GetString("supernode")
                        ?? n2n?.Supernode
                        ?? "supernode.invalid:7777";

        var community = reader.GetString("community")
                        ?? n2n?.Community
                        ?? string.Empty;

        var assignedIp = reader.GetString("virtual-ip") ?? n2n?.AssignedIp;

        if (string.IsNullOrWhiteSpace(community))
        {
            issueList.Add("未设置网络组名(可用 --community 指定,或在配置里填写)。");
        }

        // 网卡名的问题在这里提前暴露 —— 这正是 T-04 检查项覆盖的内容
        if (adapterName.Contains('"') || adapterName.Contains('\\'))
        {
            issueList.Add("网卡名含双引号或反斜杠,无法安全地作为参数传递。");
        }

        if (adapterName.Contains(' '))
        {
            issueList.Add($"网卡名「{adapterName}」含空格。参数以列表形式传递,运行时会自动加引号," +
                          "因此可以正常工作;但如果在别处手工拼接命令行,它会被拆成两个参数。");
        }

        problems = issueList;

        var edge = assets.Resolve(AssetPaths.N2nEdge) ?? @"<资源目录>\n2n\edge.exe";

        return new N2nLaunchOptions
        {
            EdgePath = edge,
            AdapterName = adapterName,
            Community = community,
            Supernode = supernode,
            AssignedIp = assignedIp,
            Key = null,
            ExtraArguments = reader.GetFlag("verbose-edge") ? ["-v"] : [],
            AllowUnelevated = reader.GetFlag("allow-unelevated"),
        };
    }

    private static void ReportStart(N2nStartResult result)
    {
        var prefix = result.IsSuccess ? "[+]" : "[×]";
        Console.WriteLine($"{prefix} {result.Message}");

        switch (result.Status)
        {
            case TunnelStartStatus.NotElevated:
                Console.WriteLine("    提示:这是设计如此,不是缺陷 —— edge.exe 在标准权限下无法打开 TAP 网卡。");
                Console.WriteLine("          原手工方案让你「以管理员身份打开 cmd」,做的正是同一件事。");
                break;

            case TunnelStartStatus.AdapterMissing:
                Console.WriteLine();
                Console.WriteLine("    下一步:让程序自己把环境准备好(会自动请求提权)——");
                Console.WriteLine($"        {SelfCommandName} tap ensure --name <目标网卡名>");
                Console.WriteLine("    它按 检测 → 装驱动 → 创建网卡 → 改名 的顺序推进,不用手工敲命令。");
                Console.WriteLine("    也可以先用 doctor 的 T-03 / T-04 检查项核对名称。");
                break;

            case TunnelStartStatus.NoTapDevices:
                Console.WriteLine();
                Console.WriteLine("    下一步:让程序自己把环境准备好(会自动请求提权)——");
                Console.WriteLine($"        {SelfCommandName} tap ensure --name <目标网卡名>");
                Console.WriteLine("    这通常意味着 TAP 驱动尚未安装,或装了但还没有网卡实例。");
                break;
        }
    }

    /// <summary>本程序在命令行里的名字,用于生成可复制的后续命令。</summary>
    private static string SelfCommandName
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? "twice1200" : Path.GetFileName(path);
        }
    }

    private static int ExitCodeFor(TunnelStartStatus status) => status switch
    {
        TunnelStartStatus.NotElevated => 74,      // EX_NOPERM
        TunnelStartStatus.InvalidConfiguration => 64,
        TunnelStartStatus.ExecutableMissing => 72,
        _ => 1,
    };

    private static void PrintLogTail(IReadOnlyList<string> lines, int count = 15)
    {
        if (lines.Count == 0)
        {
            return;
        }

        Console.WriteLine($"── edge 日志(共 {lines.Count} 行,显示最后 {Math.Min(count, lines.Count)} 行)──");

        var noise = new[] { "dropping Tx multicast" };
        var meaningful = lines.Where(l => !noise.Any(n => l.Contains(n, StringComparison.OrdinalIgnoreCase))).ToList();

        foreach (var line in meaningful.TakeLast(count))
        {
            Console.WriteLine("  " + line);
        }
    }

    private static bool Prompt(string message)
    {
        Console.Write(message);
        try
        {
            var answer = Console.ReadLine();
            return answer is not null && answer.Trim().StartsWith('y');
        }
        catch
        {
            return false;
        }
    }
}
