using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace Twice1200.Core.Net;

/// <summary>本机一块网络接口的快照。</summary>
/// <param name="Id">接口 ID。</param>
/// <param name="Name">连接名,例如"以太网 2"。这是 edge.exe 的 <c>-d</c> 参数要用的值。</param>
/// <param name="Description">接口描述,例如"TAP-Windows Adapter V9"。</param>
/// <param name="Type">接口类型。</param>
/// <param name="Status">运行状态。</param>
/// <param name="MacAddress">MAC 地址,取不到时为 <c>null</c>。</param>
/// <param name="IPv4Addresses">该接口上的 IPv4 地址。</param>
public sealed record NetworkInterfaceSnapshot(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    string? MacAddress,
    IReadOnlyList<IPAddress> IPv4Addresses)
{
    /// <summary>接口当前是否处于工作状态。</summary>
    public bool IsUp => Status == OperationalStatus.Up;

    /// <summary>第一个 IPv4 地址,没有时为 <c>null</c>。</summary>
    public IPAddress? PrimaryIPv4 => IPv4Addresses.FirstOrDefault();
}

/// <summary>
/// 网络接口枚举。
/// <para>
/// 使用 .NET 自带的 <see cref="NetworkInterface"/> 而不是 WMI 的 <c>Win32_NetworkAdapter</c>。
/// 原因:前者不依赖 WMI 服务,启动更快、更不容易被策略限制,且不需要额外依赖包。
/// 代价是拿不到"驱动服务名"(如 <c>tap0901</c>)这类细节,因此 TAP 识别改为按描述匹配。
/// </para>
/// </summary>
public static class NetworkInterfaceInspector
{
    /// <summary>TAP 类网卡的描述特征。匹配时忽略大小写。</summary>
    /// <remarks>
    /// 刻意<b>不</b>包含 <c>"n2n"</c> 这类过宽的关键字:
    /// 它会把任何名字里带 n2n 的接口都算进来,造成误报。
    /// n2n 使用的虚拟网卡本身就是 TAP-Windows,靠驱动描述即可识别。
    /// </remarks>
    private static readonly string[] TapDescriptionMarkers =
    [
        "TAP-Windows",
        "TAP-Win32",
        "OpenVPN TAP",
        "TAP Adapter",
    ];

    /// <summary>
    /// Windows 在真实网卡之上叠加的过滤驱动子接口的特征。
    /// <para>
    /// 这些不是独立网卡 —— 它们与宿主网卡共用同一个 MAC,只是 NDIS 过滤器链上的节点。
    /// 若不过滤掉,一块物理网卡会变成四五条记录,把报告淹掉,也会让"网卡数量"这类结论失真。
    /// n2n 启动时 <c>-d</c> 参数也绝不应该指向它们。
    /// </para>
    /// </summary>
    private static readonly string[] FilterInterfaceMarkers =
    [
        "-WFP Native MAC Layer LightWeight Filter-",
        "-WFP 802.3 MAC Layer LightWeight Filter-",
        "-QoS Packet Scheduler-",
        "-LightWeight Filter-",
        "-Packet Scheduler-",
        "-Filter-",
    ];

    /// <summary>其它虚拟局域网工具的描述特征,仅用于提示,不参与判定。</summary>
    private static readonly string[] OtherVirtualAdapterMarkers =
    [
        "Radmin VPN",
        "ZeroTier",
        "Tailscale",
        "WireGuard",
        "Hamachi",
        "SoftEther",
    ];

    /// <summary>枚举全部网络接口。失败时返回空列表,不抛异常。</summary>
    public static IReadOnlyList<NetworkInterfaceSnapshot> Enumerate()
    {
        var result = new List<NetworkInterfaceSnapshot>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var properties = nic.GetIPProperties();
                    var ipv4 = properties.UnicastAddresses
                        .Select(u => u.Address)
                        .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .ToList();

                    string? mac = null;
                    try
                    {
                        var bytes = nic.GetPhysicalAddress()?.GetAddressBytes();
                        if (bytes is { Length: > 0 })
                        {
                            mac = string.Join(':', bytes.Select(b => b.ToString("X2")));
                        }
                    }
                    catch
                    {
                        // 某些虚拟接口取 MAC 会失败,忽略
                    }

                    result.Add(new NetworkInterfaceSnapshot(
                        nic.Id,
                        nic.Name,
                        nic.Description,
                        nic.NetworkInterfaceType,
                        nic.OperationalStatus,
                        mac,
                        ipv4));
                }
                catch
                {
                    // 单个接口读取失败不影响其余接口
                }
            }
        }
        catch
        {
            // 整体枚举失败时返回已收集部分
        }

        return result;
    }

    /// <summary>判断一个接口是否是 Windows 叠加在真实网卡上的过滤驱动子接口。</summary>
    public static bool IsFilterInterface(NetworkInterfaceSnapshot nic)
        => FilterInterfaceMarkers.Any(marker =>
            nic.Name.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
            nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>判断一个接口是否是 TAP 类虚拟网卡。</summary>
    public static bool IsTapLike(NetworkInterfaceSnapshot nic)
    {
        if (IsFilterInterface(nic))
        {
            return false;
        }

        var text = nic.Description + " " + nic.Name;
        return TapDescriptionMarkers.Any(marker =>
            text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>识别其它虚拟局域网工具,用于在报告里提示"你已经有同类工具了"。</summary>
    public static string? DetectOtherVirtualAdapter(NetworkInterfaceSnapshot nic)
        => OtherVirtualAdapterMarkers.FirstOrDefault(marker =>
            nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>找出全部 TAP 类接口。</summary>
    public static IReadOnlyList<NetworkInterfaceSnapshot> FindTapLike()
        => Enumerate().Where(IsTapLike).ToList();
}

/// <summary>
/// 系统 ANSI 代码页检测。
/// <para>
/// 这件事看起来无关紧要,但它决定了一个真实存在的故障:
/// <c>edge.exe</c> 在解析命令行参数时会按系统 ANSI 代码页做一次转换,
/// 于是"以太网 2"这种中文网卡名在非简体中文系统上会变成乱码,
/// edge 报"找不到网卡"而用户完全看不出原因。
/// </para>
/// </summary>
public static class SystemCodePage
{
    /// <summary>简体中文的 ANSI 代码页。</summary>
    public const int SimplifiedChinese = 936;

    /// <summary>
    /// 读取系统 ANSI 代码页。读不到时返回 <c>null</c>。
    /// </summary>
    public static int? Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            // 用 Registry.GetValue 而不是 Microsoft.Win32.Registry 的强类型 API:
            // 后者在 .NET 上需要额外的包引用,前者是 BCL 自带的。
            var raw = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Nls\CodePage",
                "ACP",
                null);

            return raw switch
            {
                int value => value,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给定的网卡名在当前系统代码页上是否会因编码转换而损坏。</summary>
    public static bool WouldCorruptNonAsciiName(string adapterName, out int? codePage)
    {
        codePage = Detect();

        if (string.IsNullOrEmpty(adapterName) || adapterName.All(char.IsAscii))
        {
            return false;
        }

        // 读不到代码页时保守处理:不认为会损坏,但调用方应把 codePage == null 作为"未知"上报
        return codePage is not null and not SimplifiedChinese;
    }
}
