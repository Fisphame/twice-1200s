using System.Net;
using System.Net.Sockets;

namespace Twice1200.Core.Net;

/// <summary>
/// IPv4 网段的轻量表示。
/// <para>
/// 没有用 <c>System.Net.IPNetwork</c> —— 它在不同 .NET 版本里命名空间与语义有变化,
/// 而这里需要的功能很简单:判断包含关系、枚举主机地址、生成 CIDR 文本。
/// 自己实现反而更可控、更容易测试。
/// </para>
/// </summary>
public readonly record struct IpSubnet
{
    private IpSubnet(uint network, int prefixLength)
    {
        Network = network;
        PrefixLength = prefixLength;
    }

    /// <summary>网络地址(已按掩码对齐),以主机字节序的 uint 表示。</summary>
    public uint Network { get; }

    /// <summary>前缀长度,0–32。</summary>
    public int PrefixLength { get; }

    /// <summary>掩码。</summary>
    public uint Mask => PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);

    /// <summary>广播地址。</summary>
    public uint Broadcast => Network | ~Mask;

    /// <summary>
    /// 解析 CIDR 文本,例如 <c>10.42.0.0/24</c>。
    /// 也接受省略前缀的地址,此时按 /32 处理。
    /// </summary>
    public static bool TryParse(string? text, out IpSubnet subnet)
    {
        subnet = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        var slash = span.IndexOf('/');
        var addressPart = slash >= 0 ? span[..slash] : span;
        var prefixPart = slash >= 0 ? span[(slash + 1)..] : "32";

        if (!IPAddress.TryParse(addressPart, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!int.TryParse(prefixPart, out var prefixLength) || prefixLength is < 0 or > 32)
        {
            return false;
        }

        var raw = ToUInt32(address);
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        subnet = new IpSubnet(raw & mask, prefixLength);
        return true;
    }

    /// <summary>该地址是否落在本网段内。</summary>
    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        return (ToUInt32(address) & Mask) == Network;
    }

    /// <summary>枚举可用主机地址(排除网络地址与广播地址)。</summary>
    /// <param name="limit">最多返回多少个,防止 /8 这种网段把内存吃光。</param>
    public IEnumerable<IPAddress> EnumerateHosts(int limit = 4096)
    {
        var first = Network;
        var last = Broadcast;

        // /31 与 /32 没有"网络/广播地址"的概念,全部地址都可用
        if (PrefixLength < 31)
        {
            first += 1;
            last -= 1;
        }

        var count = 0L;
        for (var value = (long)first; value <= last && count < limit; value++, count++)
        {
            yield return FromUInt32((uint)value);
        }
    }

    /// <summary>主机地址总数。</summary>
    public long HostCount
    {
        get
        {
            var total = (long)Broadcast - Network + 1;
            if (PrefixLength >= 31)
            {
                return total;
            }

            return total >= 2 ? total - 2 : 0;
        }
    }

    /// <summary>渲染为 CIDR 文本。</summary>
    public override string ToString() => $"{FromUInt32(Network)}/{PrefixLength}";

    private static uint ToUInt32(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        // GetAddressBytes 返回网络字节序(大端),手动组装避免 BitConverter 的端序歧义
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
        => new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
