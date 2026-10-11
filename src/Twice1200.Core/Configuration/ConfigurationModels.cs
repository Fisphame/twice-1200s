namespace Twice1200.Core.Configuration;

/// <summary>虚拟网卡的配置。</summary>
public sealed class TapConfiguration
{
    /// <summary>
    /// 期望的虚拟网卡名称。
    /// <para>
    /// 注意:n2n 的 edge.exe 通过 ANSI 转换命令行参数,
    /// 在非简体中文代码页的系统上中文网卡名会乱码。程序会在启动时检测并自动改用
    /// <see cref="FallbackAdapterName"/>。
    /// </para>
    /// </summary>
    public string AdapterName { get; set; } = "n2n-TAP";

    /// <summary>中文名不可用时的回退名称。</summary>
    public string FallbackAdapterName { get; set; } = "n2n-TAP";

    /// <summary>检测到代码页不是 936 时,是否自动回退到 ASCII 名称。</summary>
    public bool AutoFallbackOnNonChineseCodePage { get; set; } = true;
}

/// <summary>n2n 组网配置。</summary>
public sealed class N2nConfiguration
{
    /// <summary>超级节点地址,形如 <c>supernode.example.com:7777</c>。</summary>
    public string Supernode { get; set; } = "supernode.invalid:7777";

    /// <summary>
    /// 网络组名。同一组的人才能互相发现。
    /// <para>
    /// <b>注意:超级节点只使用前 16 个字符。</b> 实测中 <c>local-probe-community</c> 在
    /// 超级节点日志里显示为 <c>local-probe-communi</c>。
    /// 因此两个组名如果前 16 个字符相同,会被超级节点视为同一个组 ——
    /// 命名时请让前 16 个字符就具备区分度。
    /// </para>
    /// </summary>
    public string Community { get; set; } = string.Empty;

    /// <summary>本机在虚拟网络中的 IP。留空表示由程序自动挑选。</summary>
    public string? AssignedIp { get; set; }

    /// <summary>虚拟网段,形如 <c>10.42.0.0/24</c>。</summary>
    public string VirtualNetwork { get; set; } = "10.42.0.0/24";

    /// <summary>组密码的引用。</summary>
    public SecretReference Key { get; set; } = SecretReference.Unset;

    /// <summary>虚拟网卡设置。</summary>
    public TapConfiguration Tap { get; set; } = new();
}

/// <summary>一条 frp 端口映射规则。</summary>
public sealed class FrpProxy
{
    /// <summary>规则名称,需在服务端唯一。</summary>
    public string Name { get; set; } = "web";

    /// <summary>协议类型,目前只考虑 <c>tcp</c>。</summary>
    public string Type { get; set; } = "tcp";

    /// <summary>本机被转发的地址。</summary>
    public string LocalIp { get; set; } = "127.0.0.1";

    /// <summary>本机被转发的端口。</summary>
    public int LocalPort { get; set; } = 25565;

    /// <summary>服务端对公网暴露的端口。</summary>
    public int RemotePort { get; set; } = 25565;
}

/// <summary>frp 内网穿透配置。</summary>
public sealed class FrpConfiguration
{
    /// <summary>服务端地址。</summary>
    public string ServerAddress { get; set; } = string.Empty;

    /// <summary>服务端控制端口。</summary>
    public int ServerPort { get; set; } = 7000;

    /// <summary>鉴权 token 的引用。</summary>
    public SecretReference AuthToken { get; set; } = SecretReference.Unset;

    /// <summary>
    /// 是否要求 TLS。
    /// <para>默认开启。关闭会降低安全性,应当在校验中给出警告。</para>
    /// </summary>
    public bool RequireTls { get; set; } = true;

    /// <summary>端口映射规则。</summary>
    public List<FrpProxy> Proxies { get; set; } = [];
}
