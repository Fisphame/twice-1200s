namespace Twice1200.Core.Runtime;

/// <summary>
/// 把参数列表渲染成人可读的命令行文本。
/// <para>
/// <b>只用于展示、日志与排查,绝不用于启动进程。</b>
/// 真正启动时始终把参数以列表形式交给运行时的参数转义逻辑 ——
/// 自己拼字符串正是历史上出问题的原因。
/// </para>
/// <para>
/// 它存在的价值是让"参数到底怎么被分隔"变得可见:
/// 日志里出现 <c>-d "以太网 2"</c> 说明网卡名完整,
/// 而 <c>-d 以太网 2</c> 一眼就能看出网卡名被空格拆成了两个参数。
/// </para>
/// </summary>
public static class CommandLineFormatter
{
    /// <summary>渲染成可读文本。含空格或引号的值会显示引号。</summary>
    public static string Format(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var parts = new List<string> { Quote(executable) };
        parts.AddRange(arguments.Select(Quote));
        return string.Join(' ', parts);
    }

    /// <summary>只渲染参数部分,不含可执行文件。</summary>
    public static string FormatArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', arguments.Select(Quote));
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            // 空参数在命令行里是看不见的,显式标出来以免困惑
            return "\"\"";
        }

        if (value.Contains(' ') || value.Contains('"') || value.Contains('\t'))
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
