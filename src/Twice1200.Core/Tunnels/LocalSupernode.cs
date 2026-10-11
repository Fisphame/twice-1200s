using Twice1200.Core.Runtime;

namespace Twice1200.Core.Tunnels;

/// <summary>
/// 本机超级节点。
/// <para>
/// <b>它解决的是一个很实际的问题:</b> 当远程超级节点不可用时(服务器没开、端口没放行、
/// 联系不上维护者),客户端开发与验证就完全停摆。而 <c>supernode.exe</c> 就在分发包里,
/// 因此可以在本机跑一个,把整条 n2n 链路验证完。
/// </para>
/// <para>
/// 实测确认:本机超级节点能够完成完整握手,edge 会打
/// <c>[OK] edge &lt;&lt;&lt; === &gt;&gt;&gt; supernode</c>。
/// 这条路径让"服务端不可用"从关键路径上被摘掉。
/// </para>
/// </summary>
public sealed class LocalSupernode : IDisposable
{
    /// <summary>默认监听端口,与 n2n 惯例一致。</summary>
    public const int DefaultPort = 7777;

    private readonly ProcessSupervisor _supervisor = new();

    /// <summary>当前状态。</summary>
    public TunnelState State => _supervisor.State;

    /// <summary>实际使用的端口。</summary>
    public int Port { get; private set; } = DefaultPort;

    /// <summary>已捕获的日志。</summary>
    public IReadOnlyList<string> LogLines => _supervisor.LogLines;

    /// <summary>新日志行通知。</summary>
    public event EventHandler<string>? OutputReceived
    {
        add => _supervisor.OutputReceived += value;
        remove => _supervisor.OutputReceived -= value;
    }

    /// <summary>
    /// 构造要传给 <c>supernode.exe</c> 的参数。
    /// <para>
    /// <c>-v</c> 不是可选的美化选项:<b>supernode 在没有参数时会只打印帮助然后退出。</b>
    /// 这一点在帮助文本里明确写着,踩过一次才知道。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(int port, bool verbose = true)
    {
        var arguments = new List<string> { "-p", port.ToString() };

        if (verbose)
        {
            arguments.Add("-v");
        }

        return arguments;
    }

    /// <summary>
    /// 在本机启动超级节点。
    /// </summary>
    /// <param name="executablePath">supernode.exe 的完整路径。</param>
    /// <param name="port">监听端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动成功返回 true。</returns>
    public async Task<bool> StartAsync(
        string executablePath,
        int port = DefaultPort,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), $"端口 {port} 超出 1–65535 范围。");
        }

        if (!File.Exists(executablePath))
        {
            return false;
        }

        Port = port;

        var request = new ProcessLaunchRequest
        {
            ExecutablePath = executablePath,
            Arguments = BuildArguments(port),
            // 就绪判据:超级节点会打印 "supernode is listening on UDP <port>"
            RunningDetector = N2nOutputParser.SupernodeIsListening,
            StartupWindow = TimeSpan.FromSeconds(10),
        };

        var result = await _supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);

        return result.Started;
    }

    /// <summary>
    /// 等待超级节点确认在监听。
    /// </summary>
    public Task<bool> WaitUntilListeningAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => _supervisor.WaitForOutputAsync(N2nOutputParser.SupernodeIsListening, timeout, cancellationToken);

    /// <summary>停止超级节点。</summary>
    public Task StopAsync(TimeSpan? gracePeriod = null) => _supervisor.StopAsync(gracePeriod);

    /// <inheritdoc />
    public void Dispose() => _supervisor.Dispose();
}
