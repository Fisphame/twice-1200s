using System.Security.Cryptography;
using System.Text;

namespace Twice1200.Core.Runtime;

/// <summary>文件哈希工具。</summary>
public static class Hashing
{
    /// <summary>计算文件的 SHA256,返回大写十六进制字符串。</summary>
    /// <exception cref="IOException">文件不可读时抛出。</exception>
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}

/// <summary>字符串脱敏工具。日志与报告的安全守门人。</summary>
public static class Redaction
{
    /// <summary>
    /// 把密钥变成不可还原的指纹,用于"确认两台机器上填的是同一个密码"而不泄露内容。
    /// </summary>
    public static string Fingerprint(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return "(空)";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// 显示密钥的形状(长度与首尾字符),用于用户核对。
    /// 只在必须让用户确认"填对了"的场合使用。
    /// </summary>
    public static string Describe(string? secret)
        => string.IsNullOrEmpty(secret) ? "(未设置)" : $"长度 {secret.Length}";

    /// <summary>
    /// 用当前进程环境块里"看起来像密钥"的值对文本做脱敏。
    /// <para>
    /// 这是兜底手段。正常路径应当显式登记密钥 —— 见 <see cref="SecretSet"/>。
    /// 三条保护线依次是:
    /// </para>
    /// <list type="number">
    ///   <item><description>不把密钥拼进命令行(首选)</description></item>
    ///   <item><description>用 <see cref="SecretSet"/> 显式登记并过滤日志</description></item>
    ///   <item><description>本方法:从环境块兜底推断(最弱,可能漏也可能误伤)</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ScrubSecrets(IEnumerable<string> lines, int minimumSecretLength = 8)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return SecretSet.FromEnvironment().ScrubAll(lines);
    }
}
