using Twice1200.Core.Assets;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Tap;

/// <summary>TAP 管理操作的结果分类。</summary>
public enum TapOperationStatus
{
    /// <summary>操作成功。</summary>
    Succeeded,

    /// <summary>已经处于目标状态,无需改动。</summary>
    NoChangeNeeded,

    /// <summary>需要管理员权限。</summary>
    NotElevated,

    /// <summary>找不到所需的文件。</summary>
    FileMissing,

    /// <summary>命令执行失败。</summary>
    CommandFailed,

    /// <summary>执行成功但结果未达到预期。</summary>
    VerificationFailed,

    /// <summary>参数不合法。</summary>
    InvalidArgument,
}

/// <summary>
/// 一次 TAP 管理操作的结果。
/// </summary>
/// <param name="Status">结果分类。</param>
/// <param name="Message">人可读的一句话结论。</param>
/// <param name="CommandLine">实际执行的命令(仅用于展示)。</param>
/// <param name="Output">命令输出。</param>
public sealed record TapOperationResult(
    TapOperationStatus Status,
    string Message,
    string? CommandLine = null,
    IReadOnlyList<string>? Output = null)
{
    /// <summary>是否算成功。</summary>
    public bool IsSuccess => Status is TapOperationStatus.Succeeded or TapOperationStatus.NoChangeNeeded;

    /// <summary>成功结果的快捷构造。</summary>
    public static TapOperationResult Ok(string message, string? commandLine = null, IReadOnlyList<string>? output = null)
        => new(TapOperationStatus.Succeeded, message, commandLine, output);

    /// <summary>无需改动。</summary>
    public static TapOperationResult NoChange(string message)
        => new(TapOperationStatus.NoChangeNeeded, message);

    /// <summary>失败结果的快捷构造。</summary>
    public static TapOperationResult Fail(TapOperationStatus status, string message, string? commandLine = null, IReadOnlyList<string>? output = null)
        => new(status, message, commandLine, output);
}

/// <summary>
/// TAP 虚拟网卡的管理:检测、安装驱动、创建实例、重命名、启用。
/// <para>
/// <b>只读操作不需要管理员权限,写操作需要。</b> 这个类不去自动提权 ——
/// 它把"需要提权"如实报出来,由调用方决定怎么处理
/// (命令行会自我提权重启,界面会通过辅助进程完成)。
/// 把提权策略留在调用方,是因为"要不要弹 UAC"是产品决策,不是能力问题。
/// </para>
/// <para>
/// 所有外部命令都通过 <see cref="ProcessSupervisor"/> 执行,因此天然获得
/// 参数正确转义、输出捕获与超时控制 —— 以及"网卡名含空格不会出问题"这个保证。
/// </para>
/// </summary>
public sealed class TapAdapterManager
{
    /// <summary>TAP 驱动服务名。注册表与 tapinstall 都用这个名字。</summary>
    public const string DriverServiceName = "tap0901";

    /// <summary>TAP-Windows 的默认安装目录。</summary>
    public const string DefaultInstallDirectory = @"C:\Program Files\TAP-Windows";

    /// <summary>
    /// 当前进程是否具备管理员权限。
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ================================================================
    //  只读探测(不需要权限)
    // ================================================================

    /// <summary>枚举系统里全部 TAP 类虚拟网卡。</summary>
    public static IReadOnlyList<NetworkInterfaceSnapshot> ListAdapters()
        => NetworkInterfaceInspector.FindTapLike();

    /// <summary>按名称查找 TAP 网卡。大小写不敏感。</summary>
    public static NetworkInterfaceSnapshot? FindAdapter(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return ListAdapters()
            .FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>判断 TAP 驱动是否已注册到系统。</summary>
    public static bool IsDriverRegistered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\" + DriverServiceName);

            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>定位驱动安装包(随包分发的那个 exe)。</summary>
    public static string? FindDriverInstaller(AssetLocator assets)
        => assets.Resolve(AssetPaths.TapInstaller);

    /// <summary>
    /// 定位 <c>tapinstall.exe</c>(驱动安装后由它创建/管理网卡实例)。
    /// <para>它会随驱动一起装进系统目录,因此不在 <c>assets/</c> 里。</para>
    /// </summary>
    public static string? FindTapInstall(string? installDirectory = null)
    {
        var bases = new List<string>();

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            bases.Add(installDirectory);
        }

        bases.Add(DefaultInstallDirectory);
        bases.Add(@"C:\Program Files (x86)\TAP-Windows");

        foreach (var root in bases)
        {
            var candidate = Path.Combine(root, "bin", "tapinstall.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>定位驱动 INF 文件。创建网卡实例时需要它。</summary>
    public static string? FindDriverInf(string? installDirectory = null)
    {
        var bases = new List<string>();

        if (!string.IsNullOrWhiteSpace(installDirectory))
        {
            bases.Add(installDirectory);
        }

        bases.Add(DefaultInstallDirectory);
        bases.Add(@"C:\Program Files (x86)\TAP-Windows");

        foreach (var root in bases)
        {
            var candidate = Path.Combine(root, "driver", "OemVista.inf");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    // ================================================================
    //  命令构造(纯函数,便于测试)
    // ================================================================

    /// <summary>
    /// 构造"安装驱动包"的命令。这是首次使用时的第一步。
    /// <para>驱动包自身支持 <c>/S</c> 静默安装。</para>
    /// </summary>
    public static IReadOnlyList<string> BuildDriverInstallArguments()
        => ["/S"];

    /// <summary>
    /// 构造"创建一个 TAP 网卡实例"的命令。
    /// <para>
    /// 驱动装好之后未必会自动出现网卡实例(实测中遇到过驱动在、网卡不在的情况),
    /// 因此需要这一步作为兜底。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildCreateAdapterArguments(string infPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        return ["install", infPath, DriverServiceName];
    }

    /// <summary>
    /// 构造"列出 TAP 网卡实例"的命令。用于核对驱动侧视角。
    /// </summary>
    public static IReadOnlyList<string> BuildListArguments()
        => ["status", DriverServiceName];

    /// <summary>
    /// 构造"重命名网卡"的命令。
    /// <para>
    /// 用 <c>netsh</c> 而不是 <c>Rename-NetAdapter</c>:后者需要先加载 NetAdapter 模块,
    /// 在提权子进程里启动更慢,而且失败时的错误信息不如 netsh 直观。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildRenameArguments(string oldName, string newName)
        => ["interface", "set", "interface", $"name={oldName}", $"newname={newName}"];

    /// <summary>构造"启用网卡"的命令。</summary>
    public static IReadOnlyList<string> BuildEnableArguments(string name)
        => ["interface", "set", "interface", $"name={name}", "admin=enable"];

    // ================================================================
    //  写操作(需要管理员权限)
    // ================================================================

    /// <summary>
    /// 安装 TAP 驱动(运行随包分发的安装包)。
    /// </summary>
    public async Task<TapOperationResult> InstallDriverAsync(
        AssetLocator assets,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
        {
            return TapOperationResult.Fail(
                TapOperationStatus.NotElevated,
                "安装驱动需要管理员权限。");
        }

        var installer = FindDriverInstaller(assets);
        if (installer is null || !File.Exists(installer))
        {
            return TapOperationResult.Fail(
                TapOperationStatus.FileMissing,
                "找不到 TAP 驱动安装包。",
                output: [$"期望位置:{installer ?? "(资源目录未解析)"}"]);
        }

        var arguments = BuildDriverInstallArguments();
        var display = CommandLineFormatter.Format(installer, arguments);

        var (exitCode, output) = await RunAsync(installer, arguments, TimeSpan.FromMinutes(3), cancellationToken)
            .ConfigureAwait(false);

        // 驱动安装程序即使成功也可能返回非 0(某些版本返回 3010 表示"需重启")
        if (exitCode is 0)
        {
            return TapOperationResult.Ok("驱动安装程序已执行完成。", display, output);
        }

        return TapOperationResult.Fail(
            TapOperationStatus.CommandFailed,
            exitCode is null
                ? "驱动安装程序超时未结束。"
                : $"驱动安装程序返回退出码 {exitCode}。",
            display,
            output);
    }

    /// <summary>
    /// 创建一个 TAP 网卡实例。
    /// </summary>
    public async Task<TapOperationResult> CreateAdapterAsync(
        string? installDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsElevated())
        {
            return TapOperationResult.Fail(
                TapOperationStatus.NotElevated,
                "创建虚拟网卡需要管理员权限。");
        }

        var tapinstall = FindTapInstall(installDirectory);
        var inf = FindDriverInf(installDirectory);

        if (tapinstall is null)
        {
            return TapOperationResult.Fail(
                TapOperationStatus.FileMissing,
                "找不到 tapinstall.exe,说明 TAP 驱动尚未安装。",
                output: ["请先安装驱动。"]);
        }

        if (inf is null)
        {
            return TapOperationResult.Fail(
                TapOperationStatus.FileMissing,
                "找不到驱动 INF 文件。",
                output: [@"期望位置:<TAP-Windows>\driver\OemVista.inf"]);
        }

        var arguments = BuildCreateAdapterArguments(inf);
        var display = CommandLineFormatter.Format(tapinstall, arguments);

        var (exitCode, output) = await RunAsync(tapinstall, arguments, TimeSpan.FromMinutes(2), cancellationToken)
            .ConfigureAwait(false);

        if (exitCode is 0)
        {
            return TapOperationResult.Ok("已尝试创建虚拟网卡实例。", display, output);
        }

        return TapOperationResult.Fail(
            TapOperationStatus.CommandFailed,
            exitCode is null
                ? "tapinstall 超时未结束。"
                : $"tapinstall 返回退出码 {exitCode}。注意:该工具在成功时也可能返回非 0。",
            display,
            output);
    }

    /// <summary>
    /// 把一块 TAP 网卡重命名为指定名称。
    /// </summary>
    /// <param name="currentName">当前名称。</param>
    /// <param name="targetName">目标名称。</param>
    public async Task<TapOperationResult> RenameAdapterAsync(
        string currentName,
        string targetName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentName) || string.IsNullOrWhiteSpace(targetName))
        {
            return TapOperationResult.Fail(TapOperationStatus.InvalidArgument, "网卡名不能为空。");
        }

        if (targetName.Contains('"') || targetName.Contains('\\'))
        {
            return TapOperationResult.Fail(
                TapOperationStatus.InvalidArgument,
                "目标名称含双引号或反斜杠,无法安全地作为参数传递。");
        }

        if (string.Equals(currentName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return TapOperationResult.NoChange($"网卡已经叫「{targetName}」,无需重命名。");
        }

        if (!IsElevated())
        {
            return TapOperationResult.Fail(
                TapOperationStatus.NotElevated,
                "重命名虚拟网卡需要管理员权限。");
        }

        // 目标名称被别的网卡占用时不要硬来 —— 那会把两块网卡搞成同名,更难排查
        var occupied = NetworkInterfaceInspector.Enumerate()
            .FirstOrDefault(n => string.Equals(n.Name, targetName, StringComparison.OrdinalIgnoreCase));

        if (occupied is not null)
        {
            return TapOperationResult.Fail(
                TapOperationStatus.VerificationFailed,
                $"名称「{targetName}」已被另一块网卡占用,不能使用。",
                output: [$"占用者:{occupied.Description}"]);
        }

        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        var arguments = BuildRenameArguments(currentName, targetName);
        var display = CommandLineFormatter.Format(netsh, arguments);

        var (exitCode, output) = await RunAsync(netsh, arguments, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        // 命令返回码不可靠:某些本地化版本的 netsh 在成功时也返回非 0。
        // 因此以"实际状态是否改变"为准。
        var renamed = FindAdapter(targetName) is not null;

        if (renamed)
        {
            return TapOperationResult.Ok($"已把「{currentName}」重命名为「{targetName}」。", display, output);
        }

        return TapOperationResult.Fail(
            TapOperationStatus.VerificationFailed,
            $"重命名命令已执行,但未检测到名为「{targetName}」的网卡。",
            display,
            output);
    }

    /// <summary>
    /// 启用一块被禁用的网卡。
    /// </summary>
    public async Task<TapOperationResult> EnableAdapterAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return TapOperationResult.Fail(TapOperationStatus.InvalidArgument, "网卡名不能为空。");
        }

        if (!IsElevated())
        {
            return TapOperationResult.Fail(
                TapOperationStatus.NotElevated,
                "启用虚拟网卡需要管理员权限。");
        }

        var netsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");
        var arguments = BuildEnableArguments(name);
        var display = CommandLineFormatter.Format(netsh, arguments);

        var (exitCode, output) = await RunAsync(netsh, arguments, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        // 同样以实际状态为准:能查到这块网卡就算成功(禁用的网卡在 .NET 视角下也存在,
        // 因此这里只能确认"命令没把它弄没",真正的启用效果需要后续连接来验证)
        if (FindAdapter(name) is not null)
        {
            return TapOperationResult.Ok($"已发出启用「{name}」的指令。", display, output);
        }

        return TapOperationResult.Fail(
            TapOperationStatus.VerificationFailed,
            $"执行后找不到名为「{name}」的网卡。",
            display,
            output);
    }

    /// <summary>
    /// 确保有一块可用的 TAP 网卡,并按需要重命名。
    /// <para>
    /// 这是界面上"一键准备环境"背后的逻辑:按 检测 → 装驱动 → 建实例 → 改名 的顺序推进,
    /// 每一步失败都带着明确原因返回。
    /// </para>
    /// </summary>
    public async Task<TapOperationResult> EnsureAdapterAsync(
        string desiredName,
        AssetLocator assets,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredName);

        void Report(string message) => progress?.Report(message);

        // 1. 已经有同名网卡:直接完成
        if (FindAdapter(desiredName) is not null)
        {
            return TapOperationResult.NoChange($"虚拟网卡「{desiredName}」已存在。");
        }

        Report("未找到同名网卡,开始检测环境…");

        // 2. 驱动没装:先装驱动
        if (!IsDriverRegistered())
        {
            if (!IsElevated())
            {
                return TapOperationResult.Fail(
                    TapOperationStatus.NotElevated,
                    "需要安装 TAP 驱动,这需要管理员权限。");
            }

            Report("未检测到 TAP 驱动,正在安装…");

            var install = await InstallDriverAsync(assets, cancellationToken).ConfigureAwait(false);
            if (!install.IsSuccess)
            {
                return install;
            }

            // 驱动装完网卡实例可能还没出现,给系统一点时间
            Report("等待驱动注册完成…");

            for (var i = 0; i < 15 && !IsDriverRegistered(); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        // 3. 有驱动但没有网卡实例:创建一块
        var adapters = ListAdapters();
        if (adapters.Count == 0)
        {
            Report("驱动已就绪但没有网卡实例,正在创建…");

            var create = await CreateAdapterAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            for (var i = 0; i < 20 && ListAdapters().Count == 0; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            adapters = ListAdapters();

            if (adapters.Count == 0)
            {
                return TapOperationResult.Fail(
                    TapOperationStatus.VerificationFailed,
                    "创建命令已执行,但仍未检测到虚拟网卡。可能需要重启系统。",
                    create.CommandLine,
                    create.Output);
            }

            Report($"已创建网卡「{adapters[0].Name}」。");
        }

        // 4. 有网卡但名字不对:改名
        var current = adapters[0];
        if (!string.Equals(current.Name, desiredName, StringComparison.OrdinalIgnoreCase))
        {
            Report($"正在把「{current.Name}」重命名为「{desiredName}」…");

            var rename = await RenameAdapterAsync(current.Name, desiredName, cancellationToken)
                .ConfigureAwait(false);

            if (!rename.IsSuccess)
            {
                // 改名失败不是致命错误:网卡是好的,只是名字不同
                return TapOperationResult.Fail(
                    rename.Status,
                    $"网卡可用,但重命名为「{desiredName}」失败:{rename.Message}",
                    rename.CommandLine,
                    rename.Output);
            }
        }

        // 5. 启用(禁用状态会让后续连接失败)
        var finalAdapter = FindAdapter(desiredName) ?? ListAdapters().FirstOrDefault();
        if (finalAdapter is not null)
        {
            var enable = await EnableAdapterAsync(finalAdapter.Name, cancellationToken).ConfigureAwait(false);
            if (!enable.IsSuccess)
            {
                Report($"启用网卡时出现问题:{enable.Message}");
            }
        }

        return TapOperationResult.Ok($"虚拟网卡「{desiredName}」已就绪。");
    }

    // ================================================================
    //  内部
    // ================================================================

    /// <summary>执行一个外部命令并收集结果。</summary>
    private static async Task<(int? ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var supervisor = new ProcessSupervisor();

        var request = new ProcessLaunchRequest
        {
            ExecutablePath = executable,
            Arguments = arguments,
            RunningDetector = null,
        };

        var start = await supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);

        if (!start.Started)
        {
            return (null, [start.Failure ?? "启动失败。"]);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var completion = await supervisor.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

        if (completion.ExitCode is null)
        {
            await supervisor.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        return (completion.ExitCode, supervisor.LogLines);
    }
}
