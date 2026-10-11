namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>扫描虚拟网段,找出当前已在线的地址。</summary>
public static class VirtualIpScanner
{
    /// <summary>一次扫描的结果。</summary>
    /// <param name="Subnet">被扫描的网段。</param>
    /// <param name="Reachable">有响应的地址。</param>
    /// <param name="Probed">实际探测过的地址数量。</param>
    /// <param name="Candidate">推荐的可用地址;未找到合适候选时为 <c>null</c>。</param>
    /// <param name="Elapsed">扫描耗时。</param>
    public sealed record ScanResult(
        Net.IpSubnet Subnet,
        IReadOnlyList<System.Net.IPAddress> Reachable,
        int Probed,
        System.Net.IPAddress? Candidate,
        TimeSpan Elapsed);

    /// <summary>
    /// 扫描网段内的主机地址。
    /// <para>
    /// <b>这个方法会向网段内所有地址发送 ICMP 请求。</b>这是对网络的主动行为,
    /// 必须由用户明确同意后才可调用 —— 因此体检里默认关闭。
    /// </para>
    /// <para>
    /// <b>结果有固有局限,不能当作权威结论:</b>
    /// 不响应 ping 的机器(开启了防火墙)会被误判为"空闲";
    /// 而未启动的 n2n 同伴本来就不在线。因此挑选出的地址在首次连接后
    /// 仍必须确认没有冲突。
    /// </para>
    /// </summary>
    public static async Task<ScanResult> ScanAsync(
        Net.IpSubnet subnet,
        TimeSpan probeTimeout,
        int concurrency,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var hosts = subnet.EnumerateHosts(limit: 4096).ToList();
        var reachable = new List<System.Net.IPAddress>();
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var bag = new System.Collections.Concurrent.ConcurrentBag<System.Net.IPAddress>();

        var tasks = hosts.Select(async address =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await Net.NetworkProbes.PingAsync(address, probeTimeout, cancellationToken).ConfigureAwait(false))
                {
                    bag.Add(address);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        reachable.AddRange(bag.OrderBy(a => a.GetAddressBytes(), ByteArrayComparer.Instance));

        // 推荐一个可用的候选地址:取网段内第一个没有响应的地址
        var reachableSet = reachable.Select(a => a.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = hosts.FirstOrDefault(h => !reachableSet.Contains(h.ToString()));

        stopwatch.Stop();
        return new ScanResult(subnet, reachable, hosts.Count, candidate, stopwatch.Elapsed);
    }

    /// <summary>按字节序比较 IP 地址,用于稳定排序。</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            if (x is null || y is null)
            {
                return 0;
            }

            var length = Math.Min(x.Length, y.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = x[i].CompareTo(y[i]);
                if (diff != 0)
                {
                    return diff;
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }
}
