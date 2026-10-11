using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Twice1200.Core.Net;

/// <summary>探测结果。</summary>
public enum ProbeStatus
{
    /// <summary>探测成功,对端有响应。</summary>
    Reachable,

    /// <summary>能连上网络栈,但对端没有响应(端口关闭或服务没跑)。</summary>
    Refused,

    /// <summary>连不上,通常是网络不通、DNS 解析失败或被防火墙丢弃。</summary>
    Unreachable,

    /// <summary>超时,对方或中间设备静默丢弃了数据包。</summary>
    Timeout,

    /// <summary>未执行探测。</summary>
    Skipped,
}

/// <summary>一次探测的结论。</summary>
/// <param name="Status">结论分类。</param>
/// <param name="Elapsed">耗时。</param>
/// <param name="Message">人可读说明。</param>
/// <param name="ResolvedAddress">域名解析到的地址,未解析时为 <c>null</c>。</param>
public sealed record ProbeResult(
    ProbeStatus Status,
    TimeSpan Elapsed,
    string Message,
    string? ResolvedAddress = null);

/// <summary>
/// 网络连通性探测。
/// </summary>
public static class NetworkProbes
{
    /// <summary>
    /// DNS 解析。失败时返回 <c>null</c>,不抛异常。
    /// </summary>
    public static async Task<IPAddress[]?> TryResolveAsync(string host, CancellationToken cancellationToken = default)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        try
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// TCP 连接探测。
    /// <para>
    /// 注意区分"被拒绝"和"超时":
    /// 前者说明网络通、对端可达,只是那个端口没有服务在听(信息量很大);
    /// 后者通常意味着被防火墙静默丢弃,或者地址根本不通。
    /// 把这两种情况混为一谈是网络排查里最常见的误判来源。
    /// </para>
    /// </summary>
    public static async Task<ProbeResult> TcpAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var addresses = await TryResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses is null || addresses.Length == 0)
        {
            stopwatch.Stop();
            return new ProbeResult(
                ProbeStatus.Unreachable,
                stopwatch.Elapsed,
                $"无法解析主机名「{host}」。请检查网络连接与域名拼写。");
        }

        var resolved = addresses[0].ToString();
        Exception? lastError = null;

        foreach (var address in addresses)
        {
            using var client = new TcpClient(address.AddressFamily);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            try
            {
                await client.ConnectAsync(address, port, timeoutSource.Token).ConfigureAwait(false);
                stopwatch.Stop();
                return new ProbeResult(
                    ProbeStatus.Reachable,
                    stopwatch.Elapsed,
                    $"TCP {address}:{port} 连接成功。",
                    address.ToString());
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return new ProbeResult(
                    ProbeStatus.Timeout,
                    stopwatch.Elapsed,
                    $"连接 {address}:{port} 超时({timeout.TotalSeconds:F0} 秒内无响应)。" +
                    "通常意味着被防火墙静默丢弃,或目标地址不可达。",
                    address.ToString());
            }
            catch (SocketException ex)
            {
                lastError = ex;
                if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    stopwatch.Stop();
                    return new ProbeResult(
                        ProbeStatus.Refused,
                        stopwatch.Elapsed,
                        $"连接被拒绝:{address}:{port} 可达,但该端口没有服务在监听。",
                        address.ToString());
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        stopwatch.Stop();
        return new ProbeResult(
            ProbeStatus.Unreachable,
            stopwatch.Elapsed,
            $"无法连接 {host}:{port} — {Describe(lastError)}",
            resolved);
    }

    /// <summary>
    /// UDP 探测。
    /// <para>
    /// **UDP 没有"连接"的概念**,所以结果天然不可靠:
    /// 对端没有响应既可能是"服务没跑",也可能是"服务正常运行但按协议不理会这个包"。
    /// 因此这里的 <see cref="ProbeStatus.Reachable"/> 只能由收到真实回包产生,
    /// 而拿不到回包时返回 <see cref="ProbeStatus.Timeout"/> 并明确说明其局限 —— 不要据此下结论。
    /// </para>
    /// </summary>
    public static async Task<ProbeResult> UdpAsync(
        string host,
        int port,
        TimeSpan timeout,
        byte[] payload,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var addresses = await TryResolveAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses is null || addresses.Length == 0)
        {
            stopwatch.Stop();
            return new ProbeResult(ProbeStatus.Unreachable, stopwatch.Elapsed, $"无法解析主机名「{host}」。");
        }

        var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? addresses[0];

        var resolved = target.ToString();

        try
        {
            using var client = new UdpClient(target.AddressFamily);
            client.Connect(target, port);

            await client.SendAsync(payload, cancellationToken).ConfigureAwait(false);

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);

            var receive = client.ReceiveAsync(timeoutSource.Token).AsTask();
            var completed = await Task.WhenAny(
                receive,
                Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

            stopwatch.Stop();

            if (completed == receive && receive.IsCompletedSuccessfully)
            {
                var datagram = receive.Result;
                return new ProbeResult(
                    ProbeStatus.Reachable,
                    stopwatch.Elapsed,
                    $"收到来自 {datagram.RemoteEndPoint} 的 UDP 回包,长度 {datagram.Buffer.Length} 字节。",
                    resolved);
            }

            return new ProbeResult(
                ProbeStatus.Timeout,
                stopwatch.Elapsed,
                $"已向 {resolved}:{port} 发送探测包,{timeout.TotalSeconds:F1} 秒内无回包。" +
                "UDP 对端不回包是常态,此结果不能证明服务不可用。",
                resolved);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProbeResult(
                ProbeStatus.Unreachable,
                stopwatch.Elapsed,
                $"UDP 探测 {resolved}:{port} 失败 — {Describe(ex)}",
                resolved);
        }
    }

    /// <summary>ICMP ping。用于虚拟网段扫描。</summary>
    public static async Task<bool> PingAsync(IPAddress address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, (int)timeout.TotalMilliseconds).ConfigureAwait(false);
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把异常翻译成人能看懂的一句话。</summary>
    private static string Describe(Exception? ex) => ex switch
    {
        null => "未知原因",
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.HostNotFound => "找不到该主机",
            SocketError.HostUnreachable => "主机不可达",
            SocketError.NetworkUnreachable => "网络不可达",
            SocketError.TimedOut => "超时",
            _ => $"{se.SocketErrorCode}",
        },
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
