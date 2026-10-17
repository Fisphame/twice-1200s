using System.Net;
using Twice1200.Core.Net;

namespace Twice1200.Core.Configuration;

/// <summary>
/// 维护「虚拟网段」与「本机虚拟 IP」之间的一致性。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它:</b> 界面上没有单独的「虚拟网段」输入框 —— 用户只填本机虚拟 IP。
/// 而配置里这两项都必须存在,且<b>网段必须包含那个 IP</b>
/// (校验器会把它当成错误)。
/// </para>
/// <para>
/// <b>它修的是一个真实故障:</b> 早先保存配置时是从一份全新模板起手的,
/// 只写入了 IP、没有写网段,于是网段永远等于模板默认值。
/// 当默认值恰好和用户的 IP 同网段时看不出来 —— 默认值一改,配置就自相矛盾了。
/// 这类"靠巧合成立"的代码最难发现,所以把它抽成一个可以写测试的独立方法。
/// </para>
/// </remarks>
public static class VirtualNetworkResolver
{
    /// <summary>
    /// 当网段不包含给定 IP 时,按 /24 从该 IP 推导出正确网段。
    /// </summary>
    /// <param name="currentNetwork">当前配置里的网段,可能是空或不匹配。</param>
    /// <param name="assignedIp">本机虚拟 IP;为空时不做任何推导。</param>
    /// <param name="prefixLength">推导时使用的前缀长度,默认 /24。</param>
    /// <returns>
    /// 需要改变时返回新的网段文本(形如 <c>10.236.167.0/24</c>);
    /// 当前网段已经包含该 IP、或无法推导时返回 <c>null</c>。
    /// </returns>
    public static string? TryDerive(string? currentNetwork, string? assignedIp, int prefixLength = 24)
    {
        if (string.IsNullOrWhiteSpace(assignedIp) ||
            !IPAddress.TryParse(assignedIp.Trim(), out var address))
        {
            return null;
        }

        // 当前网段本来就包含这个 IP —— 尊重用户/原有配置,不动它。
        // 这一点很重要:有人可能刻意用 /16 之类的更大网段。
        if (IpSubnet.TryParse(currentNetwork ?? string.Empty, out var existing) &&
            existing.Contains(address))
        {
            return null;
        }

        if (prefixLength is < 0 or > 32)
        {
            return null;
        }

        // 交给 IpSubnet 做掩码对齐:10.236.167.114/24 → 10.236.167.0/24
        return IpSubnet.TryParse($"{address}/{prefixLength}", out var derived)
            ? derived.ToString()
            : null;
    }
}
