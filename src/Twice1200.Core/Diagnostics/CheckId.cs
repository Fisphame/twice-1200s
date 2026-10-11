namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 全部检查项的稳定编号。
/// <para>
/// 这些字符串是**对外契约**:脚本、文档、用户反馈里的"我 E-01 失败了"都指向它。
/// 不要随意改名;确需调整时同步更新文档与本类。
/// </para>
/// </summary>
public static class CheckId
{
    // ---------- 环境 ----------
    public const string Os = "E-01";
    public const string Elevation = "E-02";
    public const string DotNet = "E-03";

    // ---------- 虚拟网卡 ----------
    public const string TapPresence = "T-01";
    public const string TapReadiness = "T-02";
    public const string TapNamed = "T-03";
    public const string TapNameSafety = "T-04";

    // ---------- 外部二进制 ----------
    public const string AssetsRoot = "B-00";
    public const string BinN2nEdge = "B-01";
    public const string BinN2nSupernode = "B-02";
    public const string BinFrpc = "B-03";
    public const string BinFrps = "B-04";
    public const string BinTapDriver = "B-05";
    public const string BinArchitecture = "B-06";

    // ---------- 配置 ----------
    public const string ConfigN2n = "C-01";
    public const string ConfigFrp = "C-02";

    // ---------- 网络 ----------
    public const string NetSupernode = "N-01";
    public const string NetFrpServer = "N-02";
    public const string NetVirtualIpScan = "N-03";
    public const string NetEdgeProbe = "N-04";

    // ---------- 运行期 ----------
    public const string ProcN2n = "P-01";
    public const string ProcFrp = "P-02";
}
