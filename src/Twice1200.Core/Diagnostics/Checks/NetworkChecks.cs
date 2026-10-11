using System.Net;
using Twice1200.Core.Net;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>
/// 检查 n2n 超级节点是否可达。
/// <para>
/// 这里刻意**不把 TCP 失败判为错误**:n2n 的 edge 与超级节点之间使用 UDP,
/// 端口不开放 TCP 是完全正常的。能给出的可靠结论只有 DNS 解析是否成功,
/// 真正的结论必须等 edge 实际连一次才能得到。因此本项以"提示"为主。
/// </para>
/// </summary>
public sealed class SupernodeCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.NetSupernode;

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var raw = options.Configuration?.N2n.Supernode ?? $"{options.SupernodeHost}:{options.SupernodePort}";

        if (!Endpoint.TryParse(raw, options.SupernodePort, out var endpoint, out var parseError))
        {
            return CheckResult.Fail(
                CheckId.NetSupernode,
                "超级节点",
                $"地址无法解析:{parseError}",
                details: [$"原始值: {raw}"]);
        }

        var details = new List<string>
        {
            $"目标地址 : {endpoint}",
            "协议     : n2n 使用 UDP,因此无法用 TCP 连接来判定服务是否正常。",
        };

        var addresses = await NetworkProbes.TryResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);

        if (addresses is null || addresses.Length == 0)
        {
            return CheckResult.Fail(
                CheckId.NetSupernode,
                "超级节点",
                $"无法解析主机名「{endpoint.Host}」。",
                details,
                [
                    "检查拼写是否正确。",
                    "检查本机 DNS 与网络连接是否正常。",
                ]);
        }

        details.Add($"解析结果 : {string.Join(", ", addresses.Select(a => a.ToString()))}");

        // UDP 探测:发一个零长度包。
        // 必须说明它几乎不可能有回包 —— 因为这不是 n2n 的握手包,超级节点会直接忽略。
        var udp = await NetworkProbes.UdpAsync(
            endpoint.Host,
            endpoint.Port,
            TimeSpan.FromSeconds(2),
            [],
            cancellationToken).ConfigureAwait(false);

        details.Add($"UDP 探测 : {udp.Message}");

        return CheckResult.Ok(
            CheckId.NetSupernode,
            "超级节点",
            $"DNS 解析成功({string.Join(", ", addresses.Select(a => a.ToString()))})。",
            details);
    }
}

/// <summary>检查 frp 服务端控制端口是否可达。</summary>
public sealed class FrpServerCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.NetFrpServer;

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var host = options.Configuration?.Frp.ServerAddress;
        var port = options.Configuration?.Frp.ServerPort ?? options.FrpServerPort;

        if (string.IsNullOrWhiteSpace(host))
        {
            host = options.FrpServerHost;
        }

        var details = new List<string>
        {
            $"目标地址 : {host}:{port}",
            "这是 frpc 与 frps 之间的控制连接,与对外暴露的转发端口是两个不同的端口。",
        };

        var probe = await NetworkProbes.TcpAsync(host, port, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
        details.Add($"耗时     : {probe.Elapsed.TotalMilliseconds:F0} ms");
        if (probe.ResolvedAddress is not null)
        {
            details.Add($"解析结果 : {probe.ResolvedAddress}");
        }

        return probe.Status switch
        {
            ProbeStatus.Reachable => CheckResult.Ok(
                CheckId.NetFrpServer,
                "frp 服务端",
                $"控制端口 {host}:{port} 可达,frps 正在运行。",
                details),

            ProbeStatus.Refused => CheckResult.Fail(
                CheckId.NetFrpServer,
                "frp 服务端",
                $"主机可达,但 {port} 端口没有服务在监听 —— frps 可能没有运行。",
                details,
                ["需要先在服务器上启动 frps。"]),

            ProbeStatus.Timeout => CheckResult.Fail(
                CheckId.NetFrpServer,
                "frp 服务端",
                $"连接 {host}:{port} 超时,数据包被静默丢弃。",
                details,
                [
                    "检查服务器安全组 / 防火墙是否放行了该端口。",
                    "确认服务器地址是否正确。",
                ]),

            _ => CheckResult.Fail(
                CheckId.NetFrpServer,
                "frp 服务端",
                probe.Message,
                details),
        };
    }
}

/// <summary>扫描虚拟网段,找出已占用的地址并推荐一个可用的。</summary>
public sealed class VirtualNetworkScanCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.NetVirtualIpScan;

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.ScanVirtualNetwork)
        {
            return CheckResult.Skip(
                CheckId.NetVirtualIpScan,
                "虚拟网段扫描",
                "未启用扫描(扫描会向网段内所有地址发送 ICMP 请求,需要显式开启)。",
                details: ["要执行扫描,请加上 --scan-virtual-network。"]);
        }

        var subnet = options.ResolveVirtualSubnet();
        if (subnet is null)
        {
            return CheckResult.Skip(
                CheckId.NetVirtualIpScan,
                "虚拟网段扫描",
                "没有可用的网段配置,无法扫描。");
        }

        // 显式取出可空值类型的实际值,后续直接使用非空的 struct
        var target = subnet.Value;

        if (target.HostCount <= 0 || target.HostCount > 4096)
        {
            return CheckResult.Skip(
                CheckId.NetVirtualIpScan,
                "虚拟网段扫描",
                $"网段 {target} 的可用地址数为 {target.HostCount},超出安全扫描范围。" +
                "请使用 /22 或更小的网段(不超过 4096 个地址)。");
        }

        var result = await VirtualIpScanner.ScanAsync(
            target,
            options.ScanProbeTimeout,
            options.ScanConcurrency,
            cancellationToken).ConfigureAwait(false);

        var details = new List<string>
        {
            $"网段     : {result.Subnet}",
            $"探测总数 : {result.Probed} 个地址",
            $"耗时     : {result.Elapsed.TotalSeconds:F1} s",
            $"有响应   : {result.Reachable.Count} 个",
        };

        foreach (var address in result.Reachable)
        {
            details.Add($"  · {address}");
        }

        if (result.Reachable.Count == 0)
        {
            details.Add("没有任何地址响应。若同伴本应在线,说明他们可能开着防火墙或不在线。");
        }

        details.Add("重要提醒:不响应 ping 的机器会被误判为空闲,离线的同伴也扫不到。");
        details.Add("因此扫描结果只能作为参考,首次连接后必须确认没有 IP 冲突。");

        // 配置里已经指定了 IP:检查是否已被占用
        if (!string.IsNullOrWhiteSpace(options.DesiredVirtualIp) &&
            IPAddress.TryParse(options.DesiredVirtualIp, out var desired))
        {
            var conflict = result.Reachable.Any(a => a.Equals(desired));

            return conflict
                ? CheckResult.Fail(
                    CheckId.NetVirtualIpScan,
                    "虚拟网段扫描",
                    $"配置的虚拟 IP {desired} 已被占用,与同伴冲突。",
                    details,
                    ["请修改为其它地址。IP 冲突会导致两台机器都时通时断。"])
                : CheckResult.Ok(
                    CheckId.NetVirtualIpScan,
                    "虚拟网段扫描",
                    $"配置的虚拟 IP {desired} 未检测到冲突。",
                    details);
        }

        // 未指定 IP:给出建议
        if (result.Candidate is not null)
        {
            details.Add($"推荐地址 : {result.Candidate}(网段内第一个无响应的地址)");

            return CheckResult.Ok(
                CheckId.NetVirtualIpScan,
                "虚拟网段扫描",
                $"网段内 {result.Reachable.Count} 个地址有响应,推荐使用 {result.Candidate}。",
                details);
        }

        return CheckResult.Warn(
            CheckId.NetVirtualIpScan,
            "虚拟网段扫描",
            "网段内没有找到可用的候选地址。",
            details,
            ["请扩大网段范围,或换一个网段。"]);
    }
}
