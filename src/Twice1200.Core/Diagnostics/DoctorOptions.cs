using Twice1200.Core.Assets;
using Twice1200.Core.Configuration;

namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 体检的可配置项。全部字段都有安全的默认值。
/// </summary>
public sealed class DoctorOptions
{
    /// <summary>
    /// 已解析的资源定位器。
    /// <para>由调用方构造一次并传入,避免每项检查各自重复遍历目录、得出不一致的结论。</para>
    /// </summary>
    public AssetLocator Assets { get; init; } = AssetLocator.Resolve();

    /// <summary>n2n 超级节点地址。</summary>
    public string SupernodeHost { get; init; } = "supernode.invalid";

    /// <summary>n2n 超级节点端口。</summary>
    public int SupernodePort { get; init; } = 7777;

    /// <summary>frp 服务端地址。</summary>
    public string FrpServerHost { get; init; } = "frp.invalid";

    /// <summary>frp 服务端控制端口。</summary>
    public int FrpServerPort { get; init; } = 7000;

    /// <summary>单个 TCP 连通性测试的超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 是否做虚拟网段扫描以寻找空闲 IP。
    /// <para>默认关闭 —— 扫描会向网段内所有地址发包,是对他人网络的实际动作,必须由用户明确同意。</para>
    /// </summary>
    public bool ScanVirtualNetwork { get; init; }

    /// <summary>
    /// 是否真实启动一次 <c>edge.exe</c> 做连通性实测。
    /// <para>
    /// 默认关闭。探测使用不存在的网卡名与社区名,因此不会改动真实网络配置,
    /// 但"启动一个外部进程"本身是真实动作,应当由用户明确同意。
    /// </para>
    /// </summary>
    public bool ProbeEdge { get; init; }

    /// <summary>
    /// 实测时使用的密钥。
    /// <para>
    /// 由调用方从凭据库取出后传入。为 <c>null</c> 表示不提供密钥,
    /// 此时 edge 会报告"加密被禁用"—— 这本身也是一条有用的诊断信息。
    /// </para>
    /// </summary>
    public string? ProbeKey { get; init; }

    /// <summary>实测 edge 的等待时长。</summary>
    public TimeSpan EdgeProbeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>扫描时的单个地址探测超时。</summary>
    public TimeSpan ScanProbeTimeout { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>扫描时的最大并发数。</summary>
    public int ScanConcurrency { get; init; } = 32;

    /// <summary>本机在虚拟网络中期望使用的 IP(用于"是否已被占用"的对照)。</summary>
    public string? DesiredVirtualIp { get; init; }

    /// <summary>本次体检使用的配置。为 <c>null</c> 表示尚未配置。</summary>
    public AppConfiguration? Configuration { get; init; }

    /// <summary>载入磁盘上配置文件的路径(仅用于报告)。</summary>
    public string? ConfigurationPath { get; init; }

    /// <summary>从配置里推导出要扫描的虚拟网段。配置不可用时返回 <c>null</c>。</summary>
    public Net.IpSubnet? ResolveVirtualSubnet()
    {
        if (Configuration is not null &&
            Net.IpSubnet.TryParse(Configuration.N2n.VirtualNetwork, out var fromConfig))
        {
            return fromConfig;
        }

        return null;
    }
}
