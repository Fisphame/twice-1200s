namespace Twice1200.Core.Runtime;

/// <summary>
/// 一组已知的密钥值,用于把日志里的敏感内容替换掉。
/// <para>
/// <b>为什么需要它:</b> 密钥本不该出现在日志里 —— 但如果出现了(比如某个工具在
/// 详细模式下回显了输入参数),日志就成了泄露渠道。这是最后一道兜底。
/// </para>
/// <para>
/// 与直接扫描进程环境块相比,这个类只持有"确实是我们传出去的"那几个值,
/// 因此不会误伤 <c>PATH</c>、<c>TEMP</c> 这类普通变量,也不会每行都重新枚举环境。
/// 日志是高频路径,这一点很重要。
/// </para>
/// </summary>
public sealed class SecretSet
{
    private readonly List<string> _secrets = [];

    /// <summary>视为密钥的最短长度。太短的串替换起来会误伤正常文本。</summary>
    private const int MinimumLength = 5;

    /// <summary>当前登记了几个密钥。</summary>
    public int Count => _secrets.Count;

    /// <summary>已登记的全部密钥值(按长度降序)。</summary>
    public IReadOnlyList<string> Values => _secrets;

    /// <summary>登记一个密钥值。空值、过短的值会被忽略。</summary>
    public void Add(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < MinimumLength)
        {
            return;
        }

        if (!_secrets.Contains(secret, StringComparer.Ordinal))
        {
            _secrets.Add(secret);

            // 长的先替换,避免短值先命中后留下长值的残片
            _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
        }
    }

    /// <summary>
    /// 把一行文本里的所有已知密钥替换成占位符。
    /// </summary>
    public string Scrub(string line)
    {
        if (string.IsNullOrEmpty(line) || _secrets.Count == 0)
        {
            return line;
        }

        var result = line;
        foreach (var secret in _secrets)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, "[已脱敏]", StringComparison.Ordinal);
            }
        }

        return result;
    }

    /// <summary>批量脱敏。</summary>
    public IReadOnlyList<string> ScrubAll(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Select(Scrub).ToList();
    }

    /// <summary>
    /// 从当前进程环境块里收集"看起来像密钥"的值。
    /// <para>
    /// 只在显式调用时使用,不参与日志热路径。用于兜底捕捉"别人设进去的"密钥。
    /// </para>
    /// </summary>
    public static SecretSet FromEnvironment()
    {
        var set = new SecretSet();

        try
        {
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Value is not string value || value.Length < MinimumLength)
                {
                    continue;
                }

                // 含路径分隔符或空格的多半是路径、命令行,不是密钥
                if (value.Contains('\\') || value.Contains('/') || value.Contains(' '))
                {
                    continue;
                }

                // 至少要有大写字母或数字,否则更可能是普通配置值
                if (!value.Any(char.IsUpper) && !value.Any(char.IsDigit))
                {
                    continue;
                }

                set.Add(value);
            }
        }
        catch
        {
            // 读环境块失败就退化为"没有额外密钥",由调用方保证不输出密钥
        }

        return set;
    }
}
