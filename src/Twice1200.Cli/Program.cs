using System.Text;
using Twice1200.Core.Assets;
using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;

namespace Twice1200.Cli;

/// <summary>
/// 命令行入口。
/// <para>
/// 这个项目的定位不是"顺便做个命令行版",而是**开发期的主力工具**:
/// 调网络与权限问题时,不必反复启动 GUI、等 WPF 编译;
/// 也能让用户在出问题时跑一条命令并把输出发回来。
/// </para>
/// </summary>
public static class Program
{
    /// <summary>数据根目录开关,与界面一致。</summary>
    private const string DataRootOption = "--data-root";

    /// <summary>入口。</summary>
    public static async Task<int> Main(string[] args)
    {
        // 中文输出必须显式声明编码,否则在重定向或管道里会乱码
        Console.OutputEncoding = Encoding.UTF8;

        // 【必须在任何路径被读取之前执行】
        ApplyDataRootOverride(args);

        // 数据目录不可写时明确告知。刻意不自动换位置 ——
        // 早先的自动回退会把配置与凭据写进 bin\Debug\... 这类会被清理的目录,
        // 用户的密钥就这样无声消失。数据的去向必须由用户决定。
        if (!AppPaths.IsConfigurationDirectoryWritable())
        {
            Console.Error.WriteLine(AppPaths.DescribeUnwritableDataDirectory());
        }

        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }        catch (CliUsageException ex)
        {
            WriteError(ex.Message);
            Console.Error.WriteLine();
            PrintUsage(stream: Console.Error);
            return 64; // EX_USAGE
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            WriteError("已取消。");
            return 130; // 128 + SIGINT
        }
        catch (Exception ex)
        {
            WriteError($"未预期的错误:{ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine("这是一个缺陷。请把上面的信息与复现步骤一并反馈。");
            return 70; // EX_SOFTWARE
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var reader = new ArgumentReader(args);
        var command = reader.Positional.Count > 0 ? reader.Positional[0].ToLowerInvariant() : "help";

        using var cancellation = new CancellationTokenSource();

        // Ctrl+C 应当干净地退出,而不是抛一堆堆栈
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        return command switch
        {
            "doctor" or "check" => await RunDoctorAsync(reader, cancellation.Token).ConfigureAwait(false),
            "n2n" => await TunnelCommands.RunAsync(reader, cancellation.Token).ConfigureAwait(false),
            "tap" => await TapCommands.RunAsync(reader, cancellation.Token).ConfigureAwait(false),
            "frp" => await FrpCommands.RunAsync(reader, cancellation.Token).ConfigureAwait(false),
            "diagnose" => DiagnoseCommands.Run(reader),
            "credential" or "cred" => await CredentialCommands.RunAsync(reader, cancellation.Token).ConfigureAwait(false),
            "paths" => RunPaths(reader),
            "config" => RunConfig(reader),
            "version" or "--version" or "-v" => RunVersion(),
            "help" or "--help" or "-h" or "/?" => RunHelp(),
            _ => throw new CliUsageException($"未知的命令「{command}」。"),
        };
    }

    // ============================================================
    //  doctor —— 环境体检
    // ============================================================

    private static async Task<int> RunDoctorAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var verbose = reader.GetFlag("verbose");
        var assetsRoot = reader.GetString("assets");
        var configPath = reader.GetString("config");

        // 资源目录:显式指定 → 环境变量 → 自动向上查找
        var locator = AssetLocator.Resolve(assetsRoot);

        // 配置:显式指定 → 默认位置。不存在时不报错,由配置检查项给出结论。
        var configuration = LoadConfiguration(configPath, out var resolvedConfigPath);

        var options = new DoctorOptions
        {
            Assets = locator,
            SupernodeHost = reader.GetString("supernode-host", "supernode.invalid")!,
            SupernodePort = reader.GetInt("supernode-port", 7777)!.Value,
            FrpServerHost = reader.GetString("frp-host", "frp.invalid")!,
            FrpServerPort = reader.GetInt("frp-port", 7000)!.Value,
            ConnectTimeout = TimeSpan.FromSeconds(reader.GetInt("timeout", 5)!.Value),
            ScanVirtualNetwork = reader.GetFlag("scan-virtual-network"),
            ScanProbeTimeout = TimeSpan.FromMilliseconds(reader.GetInt("scan-timeout", 700)!.Value),
            ScanConcurrency = reader.GetInt("scan-concurrency", 32)!.Value,
            DesiredVirtualIp = reader.GetString("virtual-ip"),
            ProbeEdge = reader.GetFlag("probe-edge"),
            ProbeKey = reader.GetString("probe-key"),
            EdgeProbeTimeout = TimeSpan.FromSeconds(reader.GetInt("probe-timeout", 10)!.Value),
            Configuration = configuration,
            ConfigurationPath = resolvedConfigPath,
        };

        if (verbose)
        {
            Console.WriteLine($"命令行     : {reader.DescribeForLog()}");
            Console.WriteLine($"配置目录   : {AppPaths.ConfigurationDirectory}");
            Console.WriteLine($"资源目录   : {locator.Root ?? "(未找到)"}  [{locator.Origin}]");
            Console.WriteLine();
        }

        var runner = DoctorRunner.BuildDefault();
        var report = await runner.RunAsync(options, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(report.ToText(verbose));

        if (verbose)
        {
            PrintDiagnosticFooter(options);
        }

        return report.ExitCode;
    }

    /// <summary>附加的诊断信息,只在 --verbose 时输出。</summary>
    private static void PrintDiagnosticFooter(DoctorOptions options)
    {
        Console.WriteLine("── 附加诊断 ──");
        Console.WriteLine($"  网络接口总数 : {NetworkInterfaceInspector.Enumerate().Count}");
        Console.WriteLine($"  系统代码页   : {SystemCodePage.Detect()?.ToString() ?? "读取失败"}");
        Console.WriteLine($"  配置目录     : {AppPaths.ConfigurationDirectory}");
        Console.WriteLine($"  日志目录     : {AppPaths.LogDirectory}");
        Console.WriteLine($"  运行期目录   : {AppPaths.RuntimeDirectory}");
        Console.WriteLine();
    }

    private static AppConfiguration? LoadConfiguration(string? explicitPath, out string? resolvedPath)
    {
        resolvedPath = explicitPath ?? AppPaths.ConfigurationFile;

        try
        {
            if (!File.Exists(resolvedPath))
            {
                return null;
            }

            return AppConfiguration.Parse(File.ReadAllText(resolvedPath, Encoding.UTF8));
        }
        catch
        {
            // 解析失败时不抛异常:配置检查项会把它报成一条失败
            return null;
        }
    }

    // ============================================================
    //  paths —— 显示各类路径
    // ============================================================

    private static int RunPaths(ArgumentReader reader)
    {
        var locator = AssetLocator.Resolve(reader.GetString("assets"));
        var showHash = reader.GetFlag("hash");

        Console.WriteLine("── 目录 ──");
        Console.WriteLine($"  配置目录 : {AppPaths.ConfigurationDirectory}");
        Console.WriteLine($"  配置文件 : {AppPaths.ConfigurationFile}{(File.Exists(AppPaths.ConfigurationFile) ? "" : "  (尚不存在)")}");
        Console.WriteLine($"  凭据文件 : {AppPaths.SecretsFile}{(File.Exists(AppPaths.SecretsFile) ? "" : "  (尚不存在)")}");
        Console.WriteLine($"  日志目录 : {AppPaths.LogDirectory}");
        Console.WriteLine($"  运行目录 : {AppPaths.RuntimeDirectory}");
        Console.WriteLine($"  程序目录 : {AppContext.BaseDirectory}");
        Console.WriteLine($"  工作目录 : {Directory.GetCurrentDirectory()}");
        Console.WriteLine();

        Console.WriteLine("── 资源 ──");
        Console.WriteLine($"  资源目录 : {locator.Root ?? "(未找到)"}");
        Console.WriteLine($"  解析说明 : {locator.Origin}");
        Console.WriteLine();

        var targets = new (string Title, string Relative)[]
        {
            ("n2n edge.exe", AssetPaths.N2nEdge),
            ("n2n supernode.exe", AssetPaths.N2nSupernode),
            ("frp frpc.exe", AssetPaths.Frpc),
            ("frp frps.exe", AssetPaths.Frps),
            ("TAP 驱动安装包", AssetPaths.TapInstaller),
        };

        foreach (var (title, relative) in targets)
        {
            var path = locator.Resolve(relative);

            if (path is null || !File.Exists(path))
            {
                Console.WriteLine($"  [缺失] {title,-20} {relative}");
                continue;
            }

            var info = new FileInfo(path);
            var architecture = PeInspector.Describe(PeInspector.ReadArchitecture(path));
            Console.WriteLine($"  [存在] {title,-20} {info.Length / 1024.0,10:F1} KB  {architecture}");

            if (showHash)
            {
                try
                {
                    var hash = Hashing.Sha256FileAsync(path).GetAwaiter().GetResult();
                    Console.WriteLine($"         SHA256 {hash}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"         SHA256 计算失败: {ex.GetType().Name}");
                }
            }
        }

        return 0;
    }

    // ============================================================
    //  config —— 配置模板生成
    // ============================================================

    private static int RunConfig(ArgumentReader reader)
    {
        var target = reader.GetString("config") ?? AppPaths.ConfigurationFile;

        if (!reader.GetFlag("init"))
        {
            Console.WriteLine("用法: twice1200 config --init [--config <路径>] [--force]");
            Console.WriteLine();
            Console.WriteLine("生成一份不含任何个人信息的配置模板。密钥留空,需要之后单独填写。");
            return 0;
        }

        if (File.Exists(target) && !reader.GetFlag("force"))
        {
            WriteError($"目标文件已存在:{target}");
            Console.Error.WriteLine("如果确认要覆盖,请加上 --force。");
            return 73; // EX_CANTCREAT
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(target, AppConfiguration.CreateTemplate().ToJson(), new UTF8Encoding(false));

        Console.WriteLine($"已生成配置模板:{target}");
        Console.WriteLine();
        Console.WriteLine("请注意:模板里不包含任何密码或 token。");
        Console.WriteLine("这些密钥应当在程序界面里输入,并以 DPAPI 加密的形式单独保存 ——");
        Console.WriteLine("永远不要把明文密钥写进这个配置文件。");
        return 0;
    }

    // ============================================================
    //  其它
    // ============================================================

    private static int RunVersion()
    {
        Console.WriteLine($"Twice1200 CLI {typeof(Program).Assembly.GetName().Version}");
        Console.WriteLine($".NET {Environment.Version}");
        Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        return 0;
    }

    private static int RunHelp()
    {
        PrintUsage(stream: Console.Out);
        return 0;
    }

    private static void PrintUsage(TextWriter? stream = null)
    {
        stream ??= Console.Out;

        stream.WriteLine("""
            Twice1200 —— n2n 组网与 frp 穿透的 Windows 图形工具(命令行版)

            用法:
              twice1200 <命令> [选项]

            命令:
              doctor          环境体检:检查驱动、网卡、权限、资源、配置与连通性
              n2n             启停与查看 n2n 连接(见下)
              tap             虚拟网卡管理:检测、安装驱动、创建、改名、启用(见下)
              frp             端口映射:配置、启停、查看状态(见下)
              credential      管理本机凭据库:密钥以 DPAPI 加密保存,不落明文(见下)
              diagnose        故障定位:写入权限、进程身份、签名状态(见下)
              paths           显示配置目录、资源目录及各二进制文件的状态
              config --init   生成一份不含个人信息的配置模板
              version         显示版本信息
              help            显示本帮助

            doctor 的选项:
              --assets <目录>            显式指定资源目录(内含 n2n/ tap/ frp/)
              --config <路径>            指定配置文件,默认 %APPDATA%\Twice1200\config.json
              --supernode-host <主机>    超级节点地址(默认 supernode.invalid,仅占位)
              --supernode-port <端口>    超级节点端口,默认 7777
              --frp-host <主机>          frp 服务端地址
              --frp-port <端口>          frp 服务端控制端口
              --timeout <秒>             TCP 探测超时,默认 5
              --virtual-ip <地址>        指定本机虚拟 IP,用于冲突检测
              --scan-virtual-network     扫描虚拟网段寻找空闲 IP(会向网段发 ICMP,默认关闭)
              --scan-timeout <毫秒>      单个地址的探测超时,默认 700
              --scan-concurrency <数量>  扫描并发数,默认 32
              --probe-edge               实测:真实启动一次 edge.exe,从它的日志判断卡在哪一步
                                         (默认关闭;使用不存在的网卡名与社区名,不改动真实网络配置)
              --probe-timeout <秒>       实测的等待时长,默认 10
              --probe-key <密钥>         仅用于测试:直接指定实测用的密钥
                                         (正常情况应从凭据库读取,不要用这个参数写真实密钥)
              --verbose                  输出附加诊断信息与完整的检查细节

            paths 的选项:
              --assets <目录>            显式指定资源目录
              --hash                     额外计算各文件的 SHA256

            n2n 子命令:
              n2n args                   只渲染将要执行的命令,不启动任何进程
                                         (用来看含空格的网卡名有没有被正确保持成一项)
              n2n up                     启动 n2n 连接
              n2n status                 查看运行状态、权限与虚拟网卡
              n2n down [--force]         结束残留的 edge / supernode 进程

            n2n up / n2n args 的选项:
              --local-supernode          在本机启动一个超级节点并连它,
                                         用于在远程超级节点不可用时验证客户端
              --adapter <名称>           虚拟网卡名(含空格无需加引号)
              --community <组名>         网络组名
              --supernode <主机:端口>    超级节点地址
              --virtual-ip <地址>        本机虚拟 IP;不指定则向超级节点申请
              --key <密钥>               仅用于测试;真实使用应从凭据库读取
              --dry-run                  只打印命令,不启动进程
              --verbose-edge             给 edge 加上 -v 详细日志
              --allow-unelevated         跳过提权检查(仅用于诊断;
                                         实测确认不提权时 edge 打不开 TAP 网卡)
              --assets / --config        同上

            tap 子命令:
              tap status                 查看网卡、驱动与权限状态(不需要管理员权限)
              tap ensure --name <名称>   确保有一块可用的 TAP 网卡
                                         (自动请求提权;按 检测 → 装驱动 → 创建 → 改名 推进)
              tap create                 只创建一块网卡实例(需已提权)
              tap rename --from <旧> --to <新>   重命名(需已提权)
              tap enable --name <名称>   启用被禁用的网卡(需已提权)

            credential 子命令:
              credential list            列出已保存的键名与状态(不显示内容)
              credential set [键名]      保存一个密钥(掩码输入;默认键名 n2n/key)
              credential show [键名]     查看长度与指纹,用于核对,不显示内容
              credential delete <键名>   删除一项
              credential clear           清空凭据库
              credential diagnose        自检 DPAPI 加解密是否正常工作

              set 的选项:
                --stdin       从标准输入读取(用于脚本化)
                --force       跳过弱密码确认

            frp 子命令:
              frp config                 只展示将要生成的 frpc 配置(token 打码)
              frp up                     启动端口映射
              frp status                 查看配置、凭据与进程状态
              frp down [--force]         结束 frpc 进程

            frp up / frp config 的选项:
              --server <主机>            服务端地址
              --port <端口>              控制端口,默认 7000
              --local-port <端口>        覆盖第一条规则的本地端口
              --remote-port <端口>       覆盖第一条规则的服务端端口
              --name <规则名>            新建规则时的名称
              --no-tls                   关闭 TLS(不建议)
              --token <token>            仅用于测试;真实使用应从凭据库读取
              --dry-run                  只打印命令,不生成配置也不启动

            config 的选项:
              --init                     生成配置模板
              --config <路径>            输出路径,默认 %APPDATA%\Twice1200\config.json
              --force                    覆盖已存在的文件

            退出码:
              0   体检全部通过
              1   存在失败项
              2   无失败但有警告
              64  命令行用法错误
              70  程序内部错误
              130 用户中断
            """);
    }

    private static void WriteError(string message)
    {
        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("[错误] " + message);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    /// <summary>
    /// 解析并应用 <c>--data-root</c>。与界面共用同一套路径解析,
    /// 保证命令行与界面永远指向同一个数据目录。
    /// </summary>
    private static void ApplyDataRootOverride(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string? value = null;

            if (arg.StartsWith(DataRootOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(DataRootOption.Length + 1)..];
            }
            else if (arg.Equals(DataRootOption, StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length)
            {
                value = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                AppPaths.OverrideDataRoot(value);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[错误] 无法使用指定的数据目录:{ex.Message}");
            }

            return;
        }
    }
}
