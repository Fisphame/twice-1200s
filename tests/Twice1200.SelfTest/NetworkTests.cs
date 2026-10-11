using System.Net;
using Twice1200.Core.Net;

namespace Twice1200.SelfTest;

/// <summary>IpSubnet 的解析与包含判定。</summary>
public static class IpSubnetTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("IpSubnet:/24 解析后网络地址按掩码对齐", () =>
        {
            Check.True(IpSubnet.TryParse("10.236.167.0/24", out var subnet), "应能解析标准 CIDR");
            Check.Equal(24, subnet.PrefixLength, "前缀长度");
            Check.Equal("10.236.167.0/24", subnet.ToString(), "渲染回来的 CIDR");
            Check.Equal(254L, subnet.HostCount, "/24 的可用主机数");
        });

        suite.Test("IpSubnet:主机位非零时按掩码归零", () =>
        {
            // 10.236.167.130/24 的网络地址应当是 10.236.167.0
            Check.True(IpSubnet.TryParse("10.236.167.130/24", out var subnet), "应能解析");
            Check.Equal("10.236.167.0/24", subnet.ToString(), "网络地址应被掩码对齐");
        });

        suite.Test("IpSubnet:Contains 判定", () =>
        {
            Check.True(IpSubnet.TryParse("10.236.167.0/24", out var subnet), "应能解析");

            Check.True(subnet.Contains(IPAddress.Parse("10.236.167.1")), "网段首地址应在内");
            Check.True(subnet.Contains(IPAddress.Parse("10.236.167.254")), "网段末地址应在内");
            Check.True(subnet.Contains(IPAddress.Parse("10.236.167.114")), "中间地址应在内");

            Check.False(subnet.Contains(IPAddress.Parse("10.236.168.1")), "相邻网段不应在内");
            Check.False(subnet.Contains(IPAddress.Parse("10.236.166.254")), "前一个网段不应在内");
            Check.False(subnet.Contains(IPAddress.Parse("192.168.1.1")), "其它网段不应在内");
        });

        suite.Test("IpSubnet:非 IPv4 地址不匹配", () =>
        {
            Check.True(IpSubnet.TryParse("10.0.0.0/8", out var subnet), "应能解析");
            Check.False(subnet.Contains(IPAddress.Parse("::1")), "IPv6 地址不应被判为在 IPv4 网段内");
        });

        suite.Test("IpSubnet:非法输入被拒绝", () =>
        {
            Check.False(IpSubnet.TryParse(null, out _), "null 应被拒绝");
            Check.False(IpSubnet.TryParse("", out _), "空串应被拒绝");
            Check.False(IpSubnet.TryParse("10.236.167.0/33", out _), "前缀 33 应被拒绝");
            Check.False(IpSubnet.TryParse("10.236.167.0/-1", out _), "负前缀应被拒绝");
            Check.False(IpSubnet.TryParse("not-an-ip/24", out _), "非 IP 应被拒绝");
            Check.False(IpSubnet.TryParse("10.236.167.0/abc", out _), "非数字前缀应被拒绝");
        });

        suite.Test("IpSubnet:省略前缀按 /32 处理", () =>
        {
            Check.True(IpSubnet.TryParse("10.0.0.7", out var subnet), "应能解析无前缀地址");
            Check.Equal(32, subnet.PrefixLength, "默认前缀长度");
            Check.Equal(1L, subnet.HostCount, "/32 只有自身一个地址");
            Check.True(subnet.Contains(IPAddress.Parse("10.0.0.7")), "应包含自身");
            Check.False(subnet.Contains(IPAddress.Parse("10.0.0.8")), "不应包含邻居");
        });

        suite.Test("IpSubnet:枚举主机地址排除网络与广播地址", () =>
        {
            Check.True(IpSubnet.TryParse("192.168.5.0/29", out var subnet), "应能解析 /29");

            var hosts = subnet.EnumerateHosts().ToList();

            // /29 = 8 个地址,去掉网络与广播后剩 6 个
            Check.Equal(6, hosts.Count, "/29 的可用主机数");
            Check.Equal("192.168.5.1", hosts[0].ToString(), "第一个可用地址");
            Check.Equal("192.168.5.6", hosts[^1].ToString(), "最后一个可用地址");
            Check.False(hosts.Any(h => h.ToString() == "192.168.5.0"), "不应包含网络地址");
            Check.False(hosts.Any(h => h.ToString() == "192.168.5.7"), "不应包含广播地址");
        });

        suite.Test("IpSubnet:枚举数量受 limit 限制", () =>
        {
            Check.True(IpSubnet.TryParse("10.0.0.0/8", out var subnet), "应能解析 /8");
            var hosts = subnet.EnumerateHosts(limit: 100).ToList();
            Check.Equal(100, hosts.Count, "应被 limit 截断,避免大网段吃光内存");
        });

        suite.Test("IpSubnet:/31 与 /32 不排除任何地址", () =>
        {
            Check.True(IpSubnet.TryParse("10.0.0.0/31", out var slash31), "应能解析 /31");
            Check.Equal(2, slash31.EnumerateHosts().Count(), "/31 两个地址都可用");
        });
    }
}

/// <summary>Endpoint 的解析。</summary>
public static class EndpointTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("Endpoint:主机加端口", () =>
        {
            Check.True(Endpoint.TryParse("1200s.top:7777", 0, out var endpoint, out var error), $"应能解析:{error}");
            Check.Equal("1200s.top", endpoint.Host, "主机名");
            Check.Equal(7777, endpoint.Port, "端口");
        });

        suite.Test("Endpoint:省略端口时使用默认值", () =>
        {
            Check.True(Endpoint.TryParse("1200s.top", 7777, out var endpoint, out _), "应能解析");
            Check.Equal(7777, endpoint.Port, "应回落到默认端口");
        });

        suite.Test("Endpoint:IPv4 字面量", () =>
        {
            Check.True(Endpoint.TryParse("39.105.139.45:7000", 0, out var endpoint, out _), "应能解析");
            Check.Equal("39.105.139.45", endpoint.Host, "主机");
            Check.Equal(7000, endpoint.Port, "端口");
        });

        suite.Test("Endpoint:方括号包裹的 IPv6", () =>
        {
            Check.True(Endpoint.TryParse("[::1]:7000", 0, out var endpoint, out var error), $"应能解析:{error}");
            Check.Equal("::1", endpoint.Host, "应去掉方括号");
            Check.Equal(7000, endpoint.Port, "端口");
        });

        suite.Test("Endpoint:拒绝协议前缀", () =>
        {
            Check.False(Endpoint.TryParse("http://example.com:80", 0, out _, out var error), "带协议前缀应被拒绝");
            Check.NotNull(error, "应给出原因");
            Check.Contains(error!, "协议前缀", "错误信息应说明原因");
        });

        suite.Test("Endpoint:拒绝越界端口", () =>
        {
            Check.False(Endpoint.TryParse("example.com:0", 0, out _, out _), "端口 0 应被拒绝");
            Check.False(Endpoint.TryParse("example.com:70000", 0, out _, out _), "端口 70000 应被拒绝");
        });

        suite.Test("Endpoint:拒绝非数字端口与空主机", () =>
        {
            Check.False(Endpoint.TryParse("example.com:abc", 0, out _, out _), "非数字端口应被拒绝");
            Check.False(Endpoint.TryParse(":7000", 0, out _, out _), "空主机应被拒绝");
            Check.False(Endpoint.TryParse("   ", 0, out _, out _), "空白应被拒绝");
        });

        suite.Test("Endpoint:拒绝含非法字符的主机名", () =>
        {
            Check.False(Endpoint.TryParse("bad host:7000", 0, out _, out _), "含空格的主机名应被拒绝");
            Check.False(Endpoint.TryParse("-bad.com:7000", 0, out _, out _), "以连字符开头应被拒绝");
        });

        suite.Test("Endpoint:ToString 还原", () =>
        {
            Check.True(Endpoint.TryParse("1200s.top:7777", 0, out var endpoint, out _), "应能解析");
            Check.Equal("1200s.top:7777", endpoint.ToString(), "ToString 应还原为 主机:端口");
        });
    }
}
