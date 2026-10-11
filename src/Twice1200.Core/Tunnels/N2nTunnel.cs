using Twice1200.Core.Diagnostics.Checks;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Tunnels;

/// <summary>
/// 启动结果的分类。
/// <para>
/// 比"成功/失败"细得多是有意的:用户看到的错误提示必须能指向**下一步该做什么**。
/// "连接失败"这四个字对排查毫无帮助,而"超级节点没有响应,请检查服务器"能直接指导行动。
/// </para>
/// </summary>
public enum TunnelStartStatus
{
    /// <summary>已启动并确认进入工作状态。</summary>
    Started,

    /// <summary>进程启动了,但在观察窗口内没有看到明确就绪标志(可能仍在连接)。</summary>
    StartedUnconfirmed,

    /// <summary>缺少管理员权限。</summary>
    NotElevated,

    /// <summary>指定的虚拟网卡不存在。</summary>
    AdapterMissing,

    /// <summary>完全没有 TAP 设备可用。</summary>
    NoTapDevices,

    /// <summary>可执行文件缺失。</summary>
    ExecutableMissing,

    /// <summary>配置不完整或非法。</summary>
    InvalidConfiguration,

    /// <summary>启动过程抛出异常。</summary>
    LaunchFailed,

    /// <summary>进程启动了但立即退出,原因无法归类。</summary>
    ExitedEarly,
}

/// <summary>
/// n2n 连接的一次启动结果。
/// </summary>
/// <param name="Status">结果分类。</param>
/// <param name="Message">人可读的一句话结论。</param>
/// <param name="LogLines">进程输出(已脱敏)。</param>
/// <param name="CommandLineForDisplay">实际使用的命令行(仅用于展示,含引号渲染)。</param>
/// <param name="Hints">
/// 失败时的"下一步该做什么"。做成独立字段而不是拼进 <see cref="Message"/>,
/// 是为了让界面能逐行显示 —— 塞进一个多行字符串会破坏日志的对齐。
/// </param>
public sealed record N2nStartResult(
    TunnelStartStatus Status,
    string Message,
    IReadOnlyList<string> LogLines,
    string CommandLineForDisplay,
    IReadOnlyList<string>? Hints = null)
{
    /// <summary>是否算作"成功启动"。</summary>
    public bool IsSuccess => Status is TunnelStartStatus.Started or TunnelStartStatus.StartedUnconfirmed;
}

/// <summary>
/// n2n 连接的启动参数。
/// <para>
/// 用 record 而不是 class,是为了让调用方能用 <c>with</c> 表达式派生变体
/// (例如"在已验证的基础上把超级节点改成 127.0.0.1"),而不必把每个字段重抄一遍。
/// </para>
/// </summary>
public sealed record N2nLaunchOptions
{
    /// <summary>edge.exe 的完整路径。</summary>
    public required string EdgePath { get; init; }

    /// <summary>虚拟网卡名。<b>可以含空格 —— 参数传递由列表负责,不需要调用方加引号。</b></summary>
    public required string AdapterName { get; init; }

    /// <summary>网络组名。</summary>
    public required string Community { get; init; }

    /// <summary>超级节点地址,格式 <c>主机:端口</c>。</summary>
    public required string Supernode { get; init; }

    /// <summary>
    /// 本机在虚拟网络中的 IP。
    /// <para>
    /// 为 <c>null</c> 时**不要**传 <c>-a</c>。这会让 edge 走"由超级节点分配 IP"的路径 ——
    /// 实测确认这条路径先去要 IP 而不打开网卡,因此不适合用来验证设备层。
    /// </para>
    /// </summary>
    public string? AssignedIp { get; init; }

    /// <summary>
    /// 网络密码。<b>它只会作为环境变量传给子进程,绝不进入命令行。</b>
    /// </summary>
    public string? Key { get; init; }

    /// <summary>额外的 edge 参数(例如 <c>-v</c>)。</summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// 允许非管理员运行。
    /// <para>
    /// 默认 <c>false</c>:实测确认 edge 需要管理员权限才能打开 TAP 网卡,
    /// 因此默认在启动前就拦住,并给出明确提示,而不是让它跑起来再报一句含糊的错误。
    /// </para>
    /// </summary>
    public bool AllowUnelevated { get; init; }
}

/// <summary>
/// n2n 隧道的启停。
/// <para>
/// <b>这个类的职责是把三个易错点在结构上消灭掉:</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>网卡名的引号问题。</b> 参数以列表形式交给 <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>,
///     由运行时转义。调用方<b>不需要</b>也不会去加引号 ——
///     实测中正是自己拼字符串让"以太网 2"被拆成了两个参数。
///   </description></item>
///   <item><description>
///     <b>密钥泄露到命令行。</b> 密钥只写进子进程的环境变量 <c>N2N_KEY</c>,
///     <see cref="BuildArguments"/> 里永远不会出现它。
///   </description></item>
///   <item><description>
///     <b>权限问题被含糊化。</b> 启动前先判权限,并在日志里比对 edge 的实际输出,
///     把"打不开网卡"翻译成"权限不足"。
///   </description></item>
/// </list>
/// </summary>
public sealed class N2nTunnel : IDisposable
{
    /// <summary>edge.exe 读取密钥所用的环境变量名。实测确认。</summary>
    public const string KeyEnvironmentVariable = "N2N_KEY";

    private readonly ProcessSupervisor _supervisor = new();

    /// <summary>当前状态。</summary>
    public TunnelState State => _supervisor.State;

    /// <summary>已捕获的日志(已脱敏)。</summary>
    public IReadOnlyList<string> LogLines => _supervisor.LogLines;

    /// <summary>状态变化通知。</summary>
    public event EventHandler<TunnelStateChange>? StateChanged
    {
        add => _supervisor.StateChanged += value;
        remove => _supervisor.StateChanged -= value;
    }

    /// <summary>新日志行通知。</summary>
    public event EventHandler<string>? OutputReceived
    {
        add => _supervisor.OutputReceived += value;
        remove => _supervisor.OutputReceived -= value;
    }

    /// <summary>
    /// 构造要传给 edge.exe 的参数列表。
    /// <para>
    /// <b>这个方法是公开的,而且被单元测试盯着</b> —— 参数拼装是历史上出过问题的地方,
    /// 值得让它可被独立验证,而不是藏在启动流程里。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(N2nLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            // 网卡名原样传入,不加引号。ArgumentList 会负责转义。
            "-d", options.AdapterName,
            "-c", options.Community,
            "-l", options.Supernode,
        };

        if (!string.IsNullOrWhiteSpace(options.AssignedIp))
        {
            arguments.Add("-a");
            arguments.Add(options.AssignedIp);
        }

        arguments.AddRange(options.ExtraArguments);

        return arguments;
    }

    /// <summary>
    /// 构造要注入子进程的环境变量。
    /// <para>密钥在这里,不在命令行里。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvironment(N2nLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(options.Key))
        {
            environment[KeyEnvironmentVariable] = options.Key;
        }

        return environment;
    }

    /// <summary>
    /// 启动连接。
    /// </summary>
    public async Task<N2nStartResult> StartAsync(
        N2nLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var arguments = BuildArguments(options);
        var environment = BuildEnvironment(options);

        var displayCommand = CommandLineFormatter.Format(options.EdgePath, arguments);

        // ---- 前置校验:把能提前发现的问题挡住,而不是让它跑起来再报错 ----
        if (string.IsNullOrWhiteSpace(options.AdapterName))
        {
            return Failure(TunnelStartStatus.InvalidConfiguration, "未指定虚拟网卡名。", displayCommand);
        }

        if (options.AdapterName.Contains('"') || options.AdapterName.Contains('\\'))
        {
            return Failure(
                TunnelStartStatus.InvalidConfiguration,
                "网卡名含双引号或反斜杠,无法安全地作为参数传递。",
                displayCommand);
        }

        if (string.IsNullOrWhiteSpace(options.Community))
        {
            return Failure(TunnelStartStatus.InvalidConfiguration, "未指定网络组名。", displayCommand);
        }

        if (string.IsNullOrWhiteSpace(options.Supernode))
        {
            return Failure(TunnelStartStatus.InvalidConfiguration, "未指定超级节点地址。", displayCommand);
        }

        if (!File.Exists(options.EdgePath))
        {
            return Failure(
                TunnelStartStatus.ExecutableMissing,
                $"找不到 edge.exe:{options.EdgePath}",
                displayCommand);
        }

        // 实测确认:标准权限下 edge 枚举不到 TAP 网卡。
        // 提前拦住并说明原因,比让它跑一遍再报一句含糊的错要好。
        if (!options.AllowUnelevated && !IsElevated())
        {
            return Failure(
                TunnelStartStatus.NotElevated,
                "启动 n2n 需要管理员权限 —— edge.exe 在标准权限下无法访问 TAP 虚拟网卡。",
                displayCommand,
                ["请以管理员身份重试。程序会通过提权辅助进程来完成这一步。"]);
        }

        var secrets = new SecretSet();
        secrets.Add(options.Key);

        var request = new ProcessLaunchRequest
        {
            ExecutablePath = options.EdgePath,
            Arguments = arguments,
            Environment = environment,
            Secrets = secrets,
            // 就绪判据:edge 打出 "edge started" 才算真正起来了。
            // 只判断"进程活着"会误判 —— 它可能在等超级节点分配 IP。
            RunningDetector = N2nOutputParser.HasStarted,
            StartupWindow = TimeSpan.FromSeconds(25),
        };

        var run = await _supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);

        if (!run.Started)
        {
            var lines = LogLines;
            var status = ClassifyFailure(lines, run.Failure);
            return new N2nStartResult(status, run.Failure ?? "启动失败。", lines, displayCommand);
        }

        // 进程起来了,再看日志判断它到底走到哪一步
        var log = _supervisor.GetLogText();
        var diagnosis = EdgeProbeDiagnosis.Parse(log);

        if (diagnosis.NoTapDevicesAtAll)
        {
            return new N2nStartResult(
                TunnelStartStatus.NoTapDevices,
                "edge 枚举不到任何 TAP 设备 —— 通常意味着权限不足。",
                LogLines,
                displayCommand);
        }

        if (diagnosis.NamedDeviceNotFound)
        {
            var reported = diagnosis.ReportedDeviceName ?? "(未知)";
            var truncated = !string.Equals(reported, options.AdapterName, StringComparison.Ordinal);

            // 名称不一致可能来自三种原因,提示里要覆盖到,而不是只猜一种。
            // 早先这里写死为"分隔符问题",在真正的原因(日志脱敏、编码转换)出现时
            // 反而会把人引向错误方向。
            var message = truncated
                ? $"edge 收到的网卡名是「{reported}」,与传入的「{options.AdapterName}」不一致。"
                : $"系统里没有名为「{reported}」的虚拟网卡。";

            var hints = truncated
                ? new[]
                {
                    "可能原因一:参数里的空格没有被正确引用(本程序用参数列表传递,通常不会是这个原因)。",
                    "可能原因二:日志脱敏把名称里的数字当成了密钥,使显示出的名称不完整。",
                    "可能原因三:网卡名含非 ASCII 字符,在编码转换中被破坏。",
                    "建议:把虚拟网卡改名为纯 ASCII(例如 n2n-TAP),可一次规避后两类原因。",
                }
                : null;

            return new N2nStartResult(
                TunnelStartStatus.AdapterMissing,
                message,
                LogLines,
                displayCommand,
                hints);
        }

        if (diagnosis.EdgeStarted)
        {
            return new N2nStartResult(
                TunnelStartStatus.Started,
                $"n2n 已启动(网卡「{options.AdapterName}」)。",
                LogLines,
                displayCommand);
        }

        return new N2nStartResult(
            TunnelStartStatus.StartedUnconfirmed,
            "进程已启动,但还没有观察到明确的就绪标志。",
            LogLines,
            displayCommand);
    }

    /// <summary>请求断开。</summary>
    public Task StopAsync(TimeSpan? gracePeriod = null) => _supervisor.StopAsync(gracePeriod);

    /// <summary>
    /// 等待与超级节点完成握手。
    /// <para>
    /// 这是"是否真的连上了"的可靠判据 —— 进程还活着并不代表连上了,
    /// 隧道工具会带着一个失败的连接一直重试。只有对端回了 ACK 才算通。
    /// </para>
    /// </summary>
    public Task<bool> WaitForHandshakeAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _supervisor.WaitForOutputAsync(N2nOutputParser.HasHandshake, timeout, cancellationToken);

    /// <summary>清空日志缓冲。</summary>
    public void ClearLog() => _supervisor.Clear();

    /// <inheritdoc />
    public void Dispose() => _supervisor.Dispose();

    // ================================================================

    /// <summary>当前进程是否以管理员身份运行。</summary>
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

    private N2nStartResult Failure(
        TunnelStartStatus status,
        string message,
        string commandLine,
        IReadOnlyList<string>? hints = null)
    {
        var lines = new List<string> { "[前置校验未通过] " + message };
        if (hints is not null)
        {
            lines.AddRange(hints.Select(h => "  → " + h));
        }

        return new N2nStartResult(status, message, lines, commandLine);
    }

    private static TunnelStartStatus ClassifyFailure(IReadOnlyList<string> lines, string? failure)
    {
        var text = string.Join('\n', lines);

        if (text.Contains("拒绝访问", StringComparison.Ordinal) ||
            text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelStartStatus.NotElevated;
        }

        if (text.Contains("No Windows tap devices found", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelStartStatus.NoTapDevices;
        }

        if (text.Contains("Cannot find tap device", StringComparison.OrdinalIgnoreCase))
        {
            return TunnelStartStatus.AdapterMissing;
        }

        if (!string.IsNullOrEmpty(failure))
        {
            return TunnelStartStatus.LaunchFailed;
        }

        return TunnelStartStatus.ExitedEarly;
    }
}
