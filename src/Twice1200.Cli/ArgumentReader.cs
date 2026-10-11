namespace Twice1200.Cli;

/// <summary>
/// 命令行参数解析。
/// <para>
/// 刻意不使用 System.CommandLine 等第三方库:一是它们在预览期 API 变动频繁,
/// 二是本项目只需要"读取若干具名开关"这么简单的能力,
/// 自己实现反而更稳定、更好测,也不会给分发包增加依赖。
/// </para>
/// </summary>
public sealed class ArgumentReader
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];

    /// <summary>解析命令行。</summary>
    public ArgumentReader(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];

            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (!token.StartsWith('-'))
            {
                _positional.Add(token);
                continue;
            }

            // 允许 --name=value 与 --name value 两种写法
            var body = token.TrimStart('-');
            var equals = body.IndexOf('=');

            if (equals >= 0)
            {
                _options[body[..equals]] = body[(equals + 1)..];
                continue;
            }

            // 下一个 token 若不以 '-' 开头,则视为本选项的值
            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                _options[body] = args[i + 1];
                i++;
            }
            else
            {
                _options[body] = null;
            }
        }
    }

    /// <summary>位置参数(未带前缀的 token)。</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>是否存在某个开关(不带值也算存在)。</summary>
    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>读取字符串选项,未提供时返回 <paramref name="fallback"/>。</summary>
    public string? GetString(string name, string? fallback = null)
    {
        if (!_options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value;
    }

    /// <summary>读取整数选项。</summary>
    /// <exception cref="CliUsageException">值不是合法整数时抛出。</exception>
    public int? GetInt(string name, int? fallback = null)
    {
        var raw = GetString(name);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value))
        {
            throw new CliUsageException($"选项 --{name} 需要一个整数,收到的是「{raw}」。");
        }

        return value;
    }

    /// <summary>读取布尔开关。支持 <c>--flag</c> 与 <c>--flag=false</c> 两种形式。</summary>
    public bool GetFlag(string name, bool fallback = false)
    {
        if (!_options.TryGetValue(name, out var value))
        {
            return fallback;
        }

        // 只写了 --flag 没给值时视为 true
        if (value is null)
        {
            return true;
        }

        return value.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "y" or "on" => true,
            "0" or "false" or "no" or "n" or "off" => false,
            _ => throw new CliUsageException($"选项 --{name} 需要对错值(true/false),收到的是「{value}」。"),
        };
    }

    /// <summary>把所有已解析的选项渲染成一行,用于日志(值会被截断,避免意外打印长文本)。</summary>
    public string DescribeForLog()
    {
        if (_options.Count == 0 && _positional.Count == 0)
        {
            return "(无参数)";
        }

        var parts = _options
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv =>
            {
                var value = kv.Value;
                if (value is { Length: > 60 })
                {
                    value = value[..60] + "…";
                }

                return value is null ? $"--{kv.Key}" : $"--{kv.Key}={value}";
            })
            .ToList();

        if (_positional.Count > 0)
        {
            parts.Insert(0, string.Join(' ', _positional));
        }

        return string.Join(' ', parts);
    }
}

/// <summary>命令行用法错误。会导致退出码 64。</summary>
public sealed class CliUsageException : Exception
{
    /// <summary>构造。</summary>
    public CliUsageException(string message) : base(message)
    {
    }
}
