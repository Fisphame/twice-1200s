using System.Text.RegularExpressions;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>
/// 一次 edge 探测的运行结果。
/// </summary>
/// <param name="Started">进程是否成功创建。</param>
/// <param name="Exited">进程是否自行退出(与"被超时终止"区分)。</param>
/// <param name="ExitCode">退出码;未退出时为 <c>null</c>。</param>
/// <param name="Output">edge 的日志(标准输出与标准错误合并,已脱敏)。</param>
/// <param name="Failure">启动失败的原因;成功时为 <c>null</c>。</param>
/// <param name="CommandLineForDisplay">实际使用的命令行(仅供展示)。</param>
public sealed record EdgeProbeRun(
    bool Started,
    bool Exited,
    int? ExitCode,
    string Output,
    string? Failure,
    string CommandLineForDisplay);

/// <summary>
/// 从 edge 的日志里判断它走到了哪一步。
/// <para>
/// 这个分类器的价值在于:edge 的失败方式会说话。
/// "找不到任何 TAP 设备"和"找不到叫 X 的设备"指向完全不同的原因,
/// 而两者在用户眼里都是"连不上"。把日志翻成人能看懂的一步结论,是这个类的全部职责。
/// </para>
/// </summary>
public sealed class EdgeProbeDiagnosis
{
    /// <summary>日志里是否出现了"找不到任何 TAP 设备"。</summary>
    public bool NoTapDevicesAtAll { get; init; }

    /// <summary>被指定的网卡名在系统里不存在(但设备列表是能枚举的)。</summary>
    public bool NamedDeviceNotFound { get; init; }

    /// <summary>日志里用于报错的那个设备名。</summary>
    public string? ReportedDeviceName { get; init; }

    /// <summary>是否已成功打开 TAP 设备。</summary>
    public bool DeviceOpened { get; init; }

    /// <summary>
    /// 是否已向超级节点发出"请给我分配 IP"的请求。
    /// <para>
    /// 这条路径与 <see cref="DeviceOpened"/> 是<b>互斥</b>的:只有没指定 <c>-a</c> 时
    /// edge 才会走这里。实测时踩过一次 —— 探针没传 <c>-a</c>,edge 先去要 IP
    /// 而根本没走到打开网卡那一步,日志里完全不出现 "Cannot find tap device",
    /// 一度以为分类器坏了。
    /// </para>
    /// <para>
    /// 它的诊断价值很高:能走到这一步说明<b>设备层是好的,而且 edge 已经读到了 <c>-d</c> 参数</b>
    /// (参数是在打开设备之前就解析的),失败纯粹发生在与超级节点的交互上。
    /// </para>
    /// </summary>
    public bool RequestedIpAssignment { get; init; }

    /// <summary>是否已创建本地 TAP 设备并启动 edge。</summary>
    public bool EdgeStarted { get; init; }

    /// <summary>是否已发起向超级节点的注册。</summary>
    public bool AttemptedRegistration { get; init; }

    /// <summary>
    /// TAP 设备层是否正常。能打开设备、能启动、或能走到"向超级节点要 IP"任一条,
    /// 都说明本机的设备层没有问题,问题在更上面。
    /// </summary>
    public bool TapLayerHealthy => DeviceOpened || EdgeStarted || RequestedIpAssignment;

    /// <summary>超级节点是否给出了响应。</summary>
    public bool SupernodeResponded { get; init; }

    /// <summary>日志里的加密算法,例如 <c>AES</c> 或 <c>null</c>。</summary>
    public string? Cipher { get; init; }

    /// <summary>是否明确报告加密被禁用(说明密钥没传进去)。</summary>
    public bool EncryptionDisabled { get; init; }

    /// <summary>日志里是否出现了"超级节点无响应"的警告。</summary>
    public bool SupernodeSilent { get; init; }

    /// <summary>解析日志。</summary>
    public static EdgeProbeDiagnosis Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var cipherMatch = Regex.Match(output, @"using\s+(?<cipher>\S+)\s+cipher", RegexOptions.IgnoreCase);
        var namedMatch = Regex.Match(output, @"Cannot find tap device\s+""(?<name>[^""]*)""", RegexOptions.IgnoreCase);

        var cipher = cipherMatch.Success ? cipherMatch.Groups["cipher"].Value : null;

        return new EdgeProbeDiagnosis
        {
            NoTapDevicesAtAll = output.Contains("No Windows tap devices found", StringComparison.OrdinalIgnoreCase),
            NamedDeviceNotFound = namedMatch.Success,
            ReportedDeviceName = namedMatch.Success ? namedMatch.Groups["name"].Value : null,
            DeviceOpened = output.Contains("Open device", StringComparison.OrdinalIgnoreCase),
            RequestedIpAssignment =
                output.Contains("automatically assign IP address", StringComparison.OrdinalIgnoreCase) ||
                output.Contains("asking for IP address", StringComparison.OrdinalIgnoreCase),
            EdgeStarted = output.Contains("edge started", StringComparison.OrdinalIgnoreCase)
                          || output.Contains("created local tap device", StringComparison.OrdinalIgnoreCase),
            AttemptedRegistration = output.Contains("registering with supernode", StringComparison.OrdinalIgnoreCase)
                                    || output.Contains("REGISTER_SUPER", StringComparison.OrdinalIgnoreCase),
            SupernodeResponded = output.Contains("successfully joined", StringComparison.OrdinalIgnoreCase)
                                 || output.Contains("re-registered", StringComparison.OrdinalIgnoreCase)
                                 || output.Contains("REGISTER_ACK", StringComparison.OrdinalIgnoreCase),
            Cipher = cipher,
            EncryptionDisabled = output.Contains("encryption is disabled", StringComparison.OrdinalIgnoreCase),
            SupernodeSilent = output.Contains("supernode not responding", StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>
/// 启动一次 <c>edge.exe</c> 并收集它的日志。
/// <para>
/// 它<b>不再自己管理进程</b>,而是复用 <see cref="ProcessSupervisor"/>。
/// 这是刻意的:进程管理里有三个容易写错的点(参数引用、密钥泄露、管道写满死锁),
/// 如果探针和正式启动各写一份实现,就等于把同样的坑挖两遍。
/// </para>
/// </summary>
public static class EdgeProbeRunner
{
    /// <summary>用于替代真实网卡的探针名称。刻意不存在,以免改动任何真实网络配置。</summary>
    public const string NonExistentAdapterName = "Twice1200ProbeNoSuchAdapter";

    /// <summary>探测用的社区名。同样不存在,避免误入真实网络。</summary>
    public const string ProbeCommunity = "twice1200-probe-community";

    /// <summary><c>edge.exe</c> 读取密钥所用的环境变量名。</summary>
    public const string KeyEnvironmentVariable = "N2N_KEY";

    /// <summary>
    /// 构造探测用的参数列表。
    /// <para>
    /// 参数以列表形式返回,不拼接字符串 —— 含空格的网卡名因此不会分裂成两个参数。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string adapterName, string supernode)
        => ["-d", adapterName, "-c", ProbeCommunity, "-l", supernode, "-v"];

    /// <summary>
    /// 启动一次探测。
    /// </summary>
    /// <param name="edgePath">edge.exe 的完整路径。</param>
    /// <param name="adapterName">要传给 <c>-d</c> 的网卡名。</param>
    /// <param name="supernode">超级节点地址,格式 <c>主机:端口</c>。</param>
    /// <param name="key">用于测试的密钥;为 <c>null</c> 表示不提供。</param>
    /// <param name="timeout">等待时长。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static async Task<EdgeProbeRun> RunAsync(
        string edgePath,
        string adapterName,
        string supernode,
        string? key,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(edgePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);

        var arguments = BuildArguments(adapterName, supernode);
        var displayCommand = CommandLineFormatter.Format(edgePath, arguments);

        if (!File.Exists(edgePath))
        {
            return new EdgeProbeRun(false, false, null, string.Empty, $"找不到 edge.exe:{edgePath}", displayCommand);
        }

        var secrets = new SecretSet();
        secrets.Add(key);

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(key))
        {
            // 密钥只在环境变量里出现,不进命令行
            environment[KeyEnvironmentVariable] = key;
        }

        using var supervisor = new ProcessSupervisor();

        var request = new ProcessLaunchRequest
        {
            ExecutablePath = edgePath,
            Arguments = arguments,
            Environment = environment,
            Secrets = secrets,
            // 探测时不用就绪判据:我们恰恰想看它在"就绪之前"走到哪一步。
            RunningDetector = null,
        };

        var start = await supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);

        if (!start.Started)
        {
            return new EdgeProbeRun(
                false, false, null, supervisor.GetLogText(), start.Failure, displayCommand);
        }

        // 让它跑一会儿收集日志,然后主动终止 —— 探测不指望它连上。
        var completion = await WaitOrTimeoutAsync(supervisor, timeout, cancellationToken)
            .ConfigureAwait(false);

        var exitedNaturally = completion is not null;

        if (!exitedNaturally)
        {
            await supervisor.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        return new EdgeProbeRun(
            true,
            exitedNaturally,
            completion?.ExitCode,
            supervisor.GetLogText(),
            null,
            displayCommand);
    }

    /// <summary>
    /// 等待进程结束或超时。
    /// </summary>
    /// <returns>进程自行结束时返回其结果;超时返回 <c>null</c>。</returns>
    private static async Task<ProcessRunResult?> WaitOrTimeoutAsync(
        ProcessSupervisor supervisor,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var wait = supervisor.WaitForExitAsync(timeoutSource.Token);

        // 注意捕获 wait 的结果,而不是丢弃它 —— 早先这里丢了结果,
        // 导致"自行退出"的进程拿不到退出码。
        var waitWithResult = wait.ContinueWith(
            static t => (ProcessRunResult?)t.Result,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);

        var finished = await Task.WhenAny(
            waitWithResult,
            Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        return finished == waitWithResult ? await waitWithResult.ConfigureAwait(false) : null;
    }
}
