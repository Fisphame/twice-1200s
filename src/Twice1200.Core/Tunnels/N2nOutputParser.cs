namespace Twice1200.Core.Tunnels;

/// <summary>
/// 解读 n2n 工具的输出文本。
/// <para>
/// 把"什么样的一行日志代表什么状态"集中在这里,而不是散落在启动流程里。
/// 这些字符串是从实测日志里抄下来的,改动它们要同步更新 <c>docs</c> 与探测档案。
/// </para>
/// <para>
/// <b>一个实测教训:</b> 超级节点日志里<b>没有</b> "REGISTER" 这个字面词,
/// 它的措辞是 <c>new community</c> / <c>created edge</c>。
/// 早先按 "REGISTER" 匹配,结果在一次完全成功的运行上报了假阴性。
/// </para>
/// </summary>
public static class N2nOutputParser
{
    /// <summary>edge 已完成与超级节点的握手(收到 REGISTER_SUPER_ACK)。</summary>
    public static bool HasHandshake(string? text)
        => Contains(text, "REGISTER_SUPER_ACK") || Contains(text, "[OK] edge");

    /// <summary>edge 已收到超级节点的 PONG(连通性活跃)。</summary>
    public static bool HasPong(string? text)
        => Contains(text, "Rx PONG from supernode");

    /// <summary>edge 已完成本地初始化并进入运行状态。</summary>
    public static bool HasStarted(string? text)
        => Contains(text, "edge started") || Contains(text, "created local tap device");

    /// <summary>超级节点已完成启动并开始监听。</summary>
    public static bool SupernodeIsListening(string? text)
        => Contains(text, "supernode started") || Contains(text, "listening on UDP");

    /// <summary>文本里是否出现了"超级节点无响应"。</summary>
    public static bool SupernodeSilent(string? text)
        => Contains(text, "supernode not responding");

    /// <summary>超级节点是否接受了某个社区。</summary>
    public static bool SupernodeAcceptedCommunity(string? text)
        => Contains(text, "new community") || Contains(text, "assigned sub-network");

    /// <summary>是否报告加密被禁用(说明密钥没有生效)。</summary>
    public static bool EncryptionDisabled(string? text)
        => Contains(text, "encryption is disabled");

    /// <summary>
    /// 从日志里提取加密算法名,例如 <c>AES</c>、<c>null</c>。
    /// </summary>
    public static string? ExtractCipher(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"using\s+(?<cipher>\S+)\s+cipher",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups["cipher"].Value : null;
    }

    private static bool Contains(string? text, string needle)
        => !string.IsNullOrEmpty(text) && text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
