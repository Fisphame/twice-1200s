using System.Text;

namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 一次体检的完整报告。
/// </summary>
public sealed class CheckReport
{
    private readonly List<(CheckResult Result, TimeSpan Elapsed)> _entries = [];

    internal void Add(CheckResult result, TimeSpan elapsed) => _entries.Add((result, elapsed));

    /// <summary>全部结果,按执行顺序。</summary>
    public IReadOnlyList<CheckResult> Results => _entries.Select(e => e.Result).ToList();

    /// <summary>失败项数量。</summary>
    public int FailCount => _entries.Count(e => e.Result.Status == CheckStatus.Fail);

    /// <summary>警告项数量。</summary>
    public int WarnCount => _entries.Count(e => e.Result.Status == CheckStatus.Warn);

    /// <summary>通过项数量。</summary>
    public int OkCount => _entries.Count(e => e.Result.Status == CheckStatus.Ok);

    /// <summary>跳过项数量。</summary>
    public int SkipCount => _entries.Count(e => e.Result.Status == CheckStatus.Skip);

    /// <summary>全部执行耗时。</summary>
    public TimeSpan TotalElapsed => _entries.Aggregate(TimeSpan.Zero, (acc, e) => acc + e.Elapsed);

    /// <summary>
    /// 体检结论对应的进程退出码。
    /// <list type="bullet">
    ///   <item><description>0 — 全部通过</description></item>
    ///   <item><description>1 — 有失败项</description></item>
    ///   <item><description>2 — 无失败,但有警告</description></item>
    /// </list>
    /// </summary>
    public int ExitCode => FailCount > 0 ? 1 : WarnCount > 0 ? 2 : 0;

    /// <summary>非详细模式下,单项最多显示多少条细节。</summary>
    private const int SummaryDetailLimit = 4;

    /// <summary>非详细模式下,单项最多显示多少条建议。</summary>
    private const int SummaryHintLimit = 2;

    /// <summary>渲染为人可读的文本报告。</summary>
    /// <param name="verbose">
    /// 为 <c>false</c>(默认)时只显示每项检查的前几条细节,报告保持可读;
    /// 为 <c>true</c> 时输出全部细节。
    /// </param>
    public string ToText(bool verbose = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine("═══ Twice1200 环境体检报告 ═══");
        sb.AppendLine();

        foreach (var (result, elapsed) in _entries)
        {
            sb.Append("  ").Append(Marker(result.Status)).Append(' ');
            sb.Append(result.Id.PadRight(6)).Append(' ');
            sb.Append(PadDisplay(result.Title, 22)).Append("  ");
            sb.Append(result.Message);

            if (elapsed.TotalMilliseconds >= 1)
            {
                sb.Append("  (").Append(elapsed.TotalMilliseconds.ToString("F0")).Append(" ms)");
            }

            sb.AppendLine();

            AppendLines(sb, result.Details, "·", SummaryDetailLimit, verbose);
            AppendLines(sb, result.Hints, "→", SummaryHintLimit, verbose);
        }

        sb.AppendLine();
        sb.Append("  小计: 通过 ").Append(OkCount);
        sb.Append(" / 警告 ").Append(WarnCount);
        sb.Append(" / 失败 ").Append(FailCount);
        sb.Append(" / 跳过 ").Append(SkipCount);
        sb.Append("    总耗时 ").Append(TotalElapsed.TotalSeconds.ToString("F1")).AppendLine(" s");
        sb.AppendLine();

        var failures = _entries.Where(e => e.Result.Status == CheckStatus.Fail).ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine("  需要处理的问题:");
            foreach (var (result, _) in failures)
            {
                sb.Append("    × [").Append(result.Id).Append("] ").Append(result.Title)
                  .Append(" — ").AppendLine(result.Message);
            }

            sb.AppendLine();
        }

        sb.AppendLine(ExitCode switch
        {
            0 => "  结论: 环境可用。",
            1 => "  结论: 存在失败项,请先按上面的提示处理。",
            _ => "  结论: 环境可用,但有警告值得留意。",
        });

        return sb.ToString();
    }

    private static string Marker(CheckStatus status) => status switch
    {
        CheckStatus.Ok => "[√]",
        CheckStatus.Warn => "[!]",
        CheckStatus.Fail => "[×]",
        _ => "[-]",
    };

    /// <summary>
    /// 输出一组补充行。非详细模式下超出的部分折叠成一行计数提示。
    /// <para>
    /// 折叠是必要的:某些检查(例如 edge 实测)会带十几行日志,
    /// 全部展开会让报告失去"一眼看清问题"的价值。
    /// </para>
    /// </summary>
    private static void AppendLines(
        StringBuilder sb,
        IReadOnlyList<string>? lines,
        string bullet,
        int limit,
        bool verbose)
    {
        if (lines is not { Count: > 0 })
        {
            return;
        }

        var shown = verbose ? lines.Count : Math.Min(lines.Count, limit);

        for (var i = 0; i < shown; i++)
        {
            sb.AppendLine($"           {bullet} {lines[i]}");
        }

        if (shown < lines.Count)
        {
            sb.AppendLine($"           {bullet} ……另有 {lines.Count - shown} 行未显示(加 --verbose 查看全部)");
        }
    }

    /// <summary>
    /// 按终端显示宽度补齐(中日韩全角字符占两列)。
    /// 直接用 PadRight 会让含中文的列对不齐。
    /// </summary>
    private static string PadDisplay(string text, int width)
    {
        var displayWidth = 0;
        foreach (var ch in text)
        {
            displayWidth += IsFullWidth(ch) ? 2 : 1;
        }

        if (displayWidth >= width)
        {
            return text;
        }

        return text + new string(' ', width - displayWidth);
    }

    private static bool IsFullWidth(char ch) => ch switch
    {
        >= '\u1100' and <= '\u115F' => true,   // 谚文字母
        >= '\u2E80' and <= '\u303E' => true,   // 中日韩部首、标点
        >= '\u3041' and <= '\u33FF' => true,   // 假名、注音、中日韩兼容
        >= '\u3400' and <= '\u4DBF' => true,   // 中日韩扩展 A
        >= '\u4E00' and <= '\u9FFF' => true,   // 中日韩统一表意文字
        >= '\uA000' and <= '\uA4CF' => true,   // 彝文
        >= '\uAC00' and <= '\uD7A3' => true,   // 谚文音节
        >= '\uF900' and <= '\uFAFF' => true,   // 中日韩兼容表意文字
        >= '\uFE30' and <= '\uFE6F' => true,   // 中日韩兼容形式
        >= '\uFF00' and <= '\uFF60' => true,   // 全角 ASCII
        >= '\uFFE0' and <= '\uFFE6' => true,   // 全角符号
        _ => false,
    };
}
