using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Twice1200.Core.Runtime;

/// <summary>
/// 启动一个外部可执行文件的请求。
/// </summary>
public sealed class ProcessLaunchRequest
{
    /// <summary>可执行文件的完整路径。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>
    /// 参数列表。
    /// <para>
    /// <b>用列表而不是一整串字符串,这是刻意的。</b>
    /// 运行时会负责正确的引用转义,含空格的值(例如网卡名"以太网 2")不会分裂成两个参数。
    /// 实测中恰恰是"自己拼字符串"导致了这个问题,现象是 edge 报"找不到 TAP 网卡",
    /// 看起来像驱动故障。列表形式让这个错误<b>在结构上不可能发生</b>。
    /// </para>
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// 要注入到子进程环境块的变量。
    /// <para>
    /// 密钥走这里,不走命令行 —— 命令行对本机任何进程可见。
    /// 注意这是<b>每个进程独立的设置</b>,不会改动本进程的环境块,
    /// 因此不存在"忘了恢复导致密钥扩散到整个进程树"的风险。
    /// </para>
    /// <para>
    /// <b>这里的值不会被自动脱敏。</b> 如果某个值是密钥,必须同时登记到
    /// <see cref="Secrets"/> 里。这不是麻烦,而是刻意的:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     早先的做法是"把环境变量里的所有值都当密钥脱敏",结果把
    ///     <c>以太网 2</c>、<c>10.236.167.114</c> 这类正常内容也抹成了
    ///     <c>[已脱敏]</c> —— <b>日志因此失去排查价值,比不脱敏更糟。</b>
    ///   </description></item>
    ///   <item><description>
    ///     改成显式声明之后,脱敏是确定的:该抹的一定抹掉,不该抹的绝不动。
    ///   </description></item>
    /// </list>
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; }
        = new Dictionary<string, string>();

    /// <summary>工作目录。为 <c>null</c> 时用可执行文件所在目录。</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>用于日志脱敏的密钥集合。</summary>
    /// <remarks>
    /// <b>这里登记什么,日志里就抹什么;没登记的一律不动。</b>
    /// 环境变量里的值<b>不会</b>被自动登记 —— 见 <see cref="Environment"/> 的说明。
    /// </remarks>
    public SecretSet Secrets { get; init; } = new();

    /// <summary>
    /// 对每一行输出做的额外处理,在脱敏<b>之前</b>应用,结果进入日志缓冲与事件。
    /// <para>
    /// 典型用途是剥掉 ANSI 颜色转义序列。实测确认 frpc 即使被重定向到管道
    /// 也会输出 <c>\u001b[1;34m</c> 这类序列 —— 它们既污染界面显示,
    /// 也会让日志的字符串匹配变得不可靠。
    /// </para>
    /// <para>
    /// 放在这里而不是调用方侧,是为了保证<b>缓冲、事件、界面三处看到的完全一致</b>:
    /// 只在某一处清理,会导致"日志里没有但匹配到了"这类难查的不一致。
    /// </para>
    /// </summary>
    public Func<string, string>? LineTransform { get; init; }

    /// <summary>保留多少行历史输出供界面显示。</summary>
    public int LogCapacity { get; init; } = 2000;

    /// <summary>
    /// 判断"启动成功"的依据。
    /// <para>
    /// 隧道工具不会在就绪时退出,所以"进程还活着"并不等于"连上了"。
    /// 这里给出的是一个**基于输出内容**的判据;为 <c>null</c> 表示
    /// 只要进程活着就算运行中。
    /// </para>
    /// </summary>
    public Func<string, bool>? RunningDetector { get; init; }

    /// <summary>启动阶段的观察窗口。超过此时长仍未匹配 <see cref="RunningDetector"/>,则视为启动失败。</summary>
    public TimeSpan StartupWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 是否把子进程的输出按 UTF-8 解码。
    /// <para>
    /// <b>默认开启,但这件事的代价比看上去大。</b> 在 Windows 上设置
    /// <c>StandardOutputEncoding</c> 会让运行时调整<b>控制台</b>的输出代码页 ——
    /// 那是<b>进程级共享状态</b>,不是每个子进程私有的。
    /// 因此它会影响<b>之后</b>启动的每一个子进程,
    /// 包括那些按 ANSI 代码页解析自己命令行的原生程序。
    /// </para>
    /// <para>
    /// 实测后果:先启动 supernode(开启此项)之后再启动 edge,
    /// edge 收到的中文网卡名会损坏成乱码,于是报 <c>Cannot find tap device</c>。
    /// 所以调用方要为<b>整条进程链</b>统一这个设置,不能时开时关。
    /// </para>
    /// </summary>
    public bool SetUtf8OutputEncoding { get; init; } = true;
}

/// <summary>
/// 管理一个外部进程的完整生命周期。
/// <para>
/// <b>这个类存在的意义是让三类错误在结构上不可能发生:</b>
/// </para>
/// <list type="number">
///   <item><description>
///     <b>参数引用错误</b> —— 只接受参数列表,不接受拼接好的字符串。
///   </description></item>
///   <item><description>
///     <b>密钥泄露到命令行</b> —— 密钥只能通过 <see cref="ProcessLaunchRequest.Environment"/> 传递,
///     且会在日志输出前被 <see cref="SecretSet"/> 过滤。
///   </description></item>
///   <item><description>
///     <b>管道写满导致子进程卡死</b> —— stdout 与 stderr 一律同时读取。
///   </description></item>
/// </list>
/// <para>
/// 一个实例管理一个进程。它不是线程安全的:请在界面线程上调用,或者自行加锁。
/// </para>
/// </summary>
public sealed class ProcessSupervisor : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly SecretSet _secrets = new();

    private Process? _process;
    private Task? _waitTask;
    private CancellationTokenSource? _lifetime;
    private bool _disposed;
    private int _logCapacity = 2000;

    /// <summary>当前状态。</summary>
    public TunnelState State { get; private set; } = TunnelState.Stopped;

    /// <summary>状态变化时触发。会在后台线程上调用,界面侧需要自己切回 UI 线程。</summary>
    public event EventHandler<TunnelStateChange>? StateChanged;

    /// <summary>每产生一行新输出时触发(内容已脱敏)。</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>进程已退出时触发,附带本次运行的汇总结果。</summary>
    public event EventHandler<ProcessRunResult>? Exited;

    /// <summary>是否正在运行(包含启动中)。</summary>
    public bool IsActive => _process is { HasExited: false };

    /// <summary>当前进程 ID;未运行时为 <c>null</c>。</summary>
    public int? ProcessId
    {
        get
        {
            var process = _process;
            if (process is null)
            {
                return null;
            }

            try
            {
                return process.HasExited ? null : process.Id;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>已捕获的历史输出(已脱敏)。</summary>
    public IReadOnlyList<string> LogLines => _log.ToArray();

    /// <summary>把历史输出拼成一段文本,便于整体展示或复制反馈。</summary>
    public string GetLogText() => string.Join(Environment.NewLine, _log);

    /// <summary>
    /// 启动进程。
    /// </summary>
    /// <returns>启动过程的结果。注意"启动成功"不等于"已连接"。</returns>
    public async Task<ProcessRunResult> StartAsync(
        ProcessLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (IsActive)
            {
                return ProcessRunResult.Failed("已经有一个进程在运行,请先停止它。");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
        {
            return Fail("未指定可执行文件路径。");
        }

        if (!File.Exists(request.ExecutablePath))
        {
            return Fail($"找不到可执行文件:{request.ExecutablePath}");
        }

        Clear();
        _logCapacity = request.LogCapacity;

        // 只登记调用方**显式声明**为密钥的值。
        //
        // 早先这里还会把 request.Environment 里的所有值一并登记,
        // 结果是灾难性的:SecretSet 的"看起来像密钥"启发式会把
        // "以太网 2"、"10.236.167.114" 这类正常内容也当成密钥,
        // 于是生产日志里的网卡名和 IP 全被抹成 [已脱敏] ——
        // 日志因此失去排查价值,比不脱敏更糟。
        //
        // 现在语义是确定的:声明什么就抹什么,没声明的一律不动。
        foreach (var secret in request.Secrets.Values)
        {
            _secrets.Add(secret);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory
                              ?? Path.GetDirectoryName(request.ExecutablePath)
                              ?? Environment.CurrentDirectory,
        };

        // 输出编码。最初是为了修中文乱码而加的,但它的副作用比预想的大得多 ——
        // 详见 ProcessLaunchRequest.SetUtf8OutputEncoding 的说明:
        // 它会调整控制台的输出代码页,而那会影响**之后**启动的每一个子进程。
        if (request.SetUtf8OutputEncoding)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        // 参数逐个加入,由运行时处理引用。绝不拼字符串。
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // 注入环境变量(密钥走这里)。这是进程私有副本,不影响本进程环境块。
        foreach (var (key, value) in request.Environment)
        {
            startInfo.Environment[key] = value;
        }

        Process process;
        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                return Fail("创建进程失败:未返回进程对象。");
            }

            process = started;
        }
        catch (Exception ex)
        {
            return Fail($"启动失败:{ex.GetType().Name}: {ex.Message}");
        }

        _process = process;
        _lifetime = new CancellationTokenSource();

        SetState(TunnelState.Starting, $"已启动进程( PID {SafeId(process)} ),等待进入工作状态…");

        // 用一个"已退出"信号,让等待就绪的过程能同时响应"检测到就绪"和"进程结束"两件事。
        // 必须在启动 WatchAsync 之前挂上,否则可能错过一个极短命进程的 Exited 事件。
        var exitedSignal = new TaskCompletionSource<ProcessRunResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExited(object? sender, ProcessRunResult result) => exitedSignal.TrySetResult(result);

        Exited += OnExited;

        // stdout 与 stderr 都要读,否则其中一个管道写满会让子进程阻塞
        var stdout = PumpAsync(process.StandardOutput, request.LineTransform);
        var stderr = PumpAsync(process.StandardError, request.LineTransform);

        _waitTask = WatchAsync(process, stdout, stderr, _lifetime.Token);

        try
        {
            // 等待进入"运行中",或者进程提前退出
            return await WaitForReadyAsync(request, exitedSignal.Task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Exited -= OnExited;
        }
    }

    /// <summary>请求停止进程。会先尝试正常结束,超时后强制终止。</summary>
    public async Task StopAsync(TimeSpan? gracePeriod = null)
    {
        var process = _process;
        if (process is null)
        {
            SetState(TunnelState.Stopped, "当前没有运行的进程。");
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }
        }
        catch
        {
            return;
        }

        var grace = gracePeriod ?? TimeSpan.FromSeconds(5);
        SetState(TunnelState.Starting, "正在停止…");

        try
        {
            // edge/frpc 对 CloseMainWindow 无响应(它们没有窗口),
            // 因此直接走 Kill,但先给一点时间让读取任务把残留输出收干净。
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(new CancellationTokenSource(grace).Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 超时后不再重试:Kill 已经发出,继续等也没有意义
        }
        catch (Exception ex)
        {
            SetState(TunnelState.Failed, $"停止进程时出错:{ex.Message}");
        }
    }

    /// <summary>
    /// 等待当前进程结束,并返回本次运行的汇总结果。
    /// <para>
    /// 供需要"跑一个命令、看它完整输出"的场景使用(命令行工具、自检)。
    /// 界面场景不要用它 —— 界面应当订阅 <see cref="Exited"/> 事件而不是阻塞等待。
    /// </para>
    /// </summary>
    public async Task<ProcessRunResult> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
        {
            return ProcessRunResult.Failed("当前没有运行的进程。");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消等待,进程仍在运行
        }

        // 给读取任务一点时间把残留输出收完
        var waitTask = _waitTask;
        if (waitTask is not null)
        {
            await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None))
                .ConfigureAwait(false);
        }

        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch
        {
            // 进程对象已回收
        }

        return new ProcessRunResult(true, false, exitCode, GetLogText(), null);
    }

    /// <summary>清空历史输出。</summary>
    public void Clear()
    {
        while (_log.TryDequeue(out _))
        {
            // 逐条出队清空
        }
    }

    /// <summary>
    /// 等待输出里出现满足条件的行。
    /// <para>
    /// 这是"验证连接是否建立"的正确姿势:不能靠"进程还活着"判断 ——
    /// 隧道工具会带着一个失败的连接一直活着,并反复重试。
    /// 只有对端真的回了包,输出里才会出现相应的行。
    /// </para>
    /// </summary>
    /// <param name="predicate">对整个输出文本求值;返回 true 表示已满足。</param>
    /// <param name="timeout">最长等待时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已满足条件时返回 true;超时或进程结束时返回 false。</returns>
    public async Task<bool> WaitForOutputAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnOutput(object? sender, string line)
        {
            // 每来一行就整体求值。日志行数不多,这样比维护增量状态更不容易出错。
            if (predicate(GetLogText()))
            {
                signal.TrySetResult(true);
            }
        }

        OutputReceived += OnOutput;

        try
        {
            // 挂上事件之后再检查一次,避免"条件在此之前就已满足"导致的错过
            if (predicate(GetLogText()))
            {
                return true;
            }

            var completed = await Task.WhenAny(
                signal.Task,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

            return completed == signal.Task && signal.Task.IsCompletedSuccessfully;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            OutputReceived -= OnOutput;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _lifetime?.Cancel();
        }
        catch
        {
            // 忽略
        }

        try
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略
        }

        _lifetime?.Dispose();
        _process?.Dispose();
        _process = null;
    }

    // ================================================================
    //  内部实现
    // ================================================================

    /// <summary>把密钥登记进日志过滤器。供 Tunnel 层在需要时补充登记。</summary>
    public void RegisterSecret(string? secret) => _secrets.Add(secret);

    private ProcessRunResult Fail(string reason)
    {
        SetState(TunnelState.Failed, reason);
        return ProcessRunResult.Failed(reason);
    }

    private async Task<ProcessRunResult> WaitForReadyAsync(
        ProcessLaunchRequest request,
        Task<ProcessRunResult> exitSignal,
        CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return ProcessRunResult.Failed("进程引用丢失。");
        }

        try
        {
            if (request.RunningDetector is null)
            {
                // 没有内容判据:那就等一小段时间,看它是活着还是已经跑完了。
                var delay = Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var first = await Task.WhenAny(delay, exitSignal).ConfigureAwait(false);

                if (first == exitSignal)
                {
                    // 进程已经结束。这里**不能一律当成失败** ——
                    // cmd /c、frpc 这类命令式进程正常完成时会立即退出。
                    // 判据应当是退出码:0 说明它成功跑完了。
                    return await FinishShortLivedAsync(process, exitSignal).ConfigureAwait(false);
                }

                // 到时间了还活着,视为运行中
                SetState(TunnelState.Running, $"运行中( PID {SafeId(process)} )。");
                return new ProcessRunResult(true, false, null, string.Empty, null);
            }

            // 有内容判据:轮询日志,直到匹配、超时,或者进程退出
            var detector = request.RunningDetector;
            var deadline = DateTime.UtcNow + request.StartupWindow;

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (detector(GetLogText()))
                {
                    SetState(TunnelState.Running, $"运行中( PID {SafeId(process)} ),已确认进入工作状态。");
                    return new ProcessRunResult(true, false, null, string.Empty, null);
                }

                if (exitSignal.IsCompleted)
                {
                    return await FinishShortLivedAsync(process, exitSignal).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }

            // 超时:进程还活着但没能确认进入工作状态。
            // 这不算失败 —— 判据本身可能因为版本差异而不出现,如实上报即可,
            // 由上层结合日志内容做更精确的判断。
            SetState(
                TunnelState.Running,
                $"进程仍在运行( PID {SafeId(process)} ),但在 {request.StartupWindow.TotalSeconds:F0} 秒内" +
                "没有观察到明确的就绪标志。");
            return new ProcessRunResult(true, false, null, string.Empty, null);
        }
        catch (OperationCanceledException)
        {
            return ProcessRunResult.Failed("启动过程被取消。");
        }
    }

    /// <summary>
    /// 处理"进程在确认就绪之前就结束了"的情况。
    /// <para>
    /// 关键区分:<b>退出码 0 表示它正常跑完了,不是启动失败。</b>
    /// 早先这里一律返回失败,结果 <c>cmd /c echo</c> 这种命令式进程会被误判 ——
    /// 集成测试把这个缺陷暴露了出来。
    /// </para>
    /// </summary>
    private async Task<ProcessRunResult> FinishShortLivedAsync(Process process, Task<ProcessRunResult> exitSignal)
    {
        var completion = await exitSignal.ConfigureAwait(false);

        if (completion.ExitCode == 0)
        {
            SetState(TunnelState.Exited, $"进程已正常结束(退出码 0 )。");
            return new ProcessRunResult(true, false, 0, completion.Output, null);
        }

        var reason = completion.ExitCode is null
            ? "进程在就绪之前就退出了,请查看日志。"
            : $"进程在就绪之前退出,退出码 {completion.ExitCode}。";

        SetState(TunnelState.Failed, reason, completion.ExitCode);
        return new ProcessRunResult(true, false, completion.ExitCode, completion.Output, reason);
    }

    /// <summary>后台任务:等待退出、等输出收完、更新状态。</summary>
    private async Task WatchAsync(
        Process process,
        Task stdout,
        Task stderr,
        CancellationToken cancellationToken)
    {
        var killedByUs = false;

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            killedByUs = true;
        }

        // 等读取任务收尾,但不要无限期拖住
        await Task.WhenAny(
            Task.WhenAll(stdout, stderr),
            Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)).ConfigureAwait(false);

        int? exitCode = null;
        try
        {
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        catch
        {
            // 进程对象已回收时取退出码会抛异常
        }

        var result = new ProcessRunResult(true, killedByUs, exitCode, GetLogText(), null);

        if (!killedByUs)
        {
            SetState(
                exitCode == 0 ? TunnelState.Exited : TunnelState.Failed,
                exitCode is null
                    ? "进程已退出。"
                    : $"进程已退出,退出码 {exitCode}。",
                exitCode);
        }

        Exited?.Invoke(this, result);
    }

    /// <summary>逐行读取一路输出,清理后同时投递给事件与历史缓冲。</summary>
    private async Task PumpAsync(StreamReader reader, Func<string, string>? lineTransform)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                // 顺序是先变换、再脱敏:
                // 变换可能引入或移除字符,脱敏必须作用在最终文本上才可靠。
                var safe = lineTransform is null ? line : lineTransform(line);
                safe = _secrets.Scrub(safe);

                _log.Enqueue(safe);

                // 环形裁剪:超出容量就丢最早的
                while (_log.Count > _logCapacity && _log.TryDequeue(out _))
                {
                    // 逐条丢弃
                }

                OutputReceived?.Invoke(this, safe);
            }
        }
        catch
        {
            // 进程终止时管道断开,属预期情况
        }
    }

    private void SetState(TunnelState state, string detail, int? exitCode = null)
    {
        State = state;
        StateChanged?.Invoke(this, new TunnelStateChange(state, detail, exitCode));
    }

    private static string SafeId(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch
        {
            return "?";
        }
    }
}
