using System.Net;

namespace Twice1200.Core.Net;

/// <summary>
/// 主机:端口 的解析与表示。
/// </summary>
public readonly record struct Endpoint(string Host, int Port)
{
    /// <summary>
    /// 解析 <c>主机:端口</c>。省略端口时使用提供的默认值。
    /// </summary>
    /// <param name="text">待解析文本,允许前后空白。</param>
    /// <param name="defaultPort">文本中未包含端口时使用的端口。</param>
    /// <param name="endpoint">解析结果。</param>
    /// <param name="error">失败原因;成功时为 <c>null</c>。</param>
    public static bool TryParse(string? text, int defaultPort, out Endpoint endpoint, out string? error)
    {
        endpoint = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "地址为空。";
            return false;
        }

        var value = text.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            error = "地址不应包含协议前缀(如 tcp:// 或 http://)。";
            return false;
        }

        var host = value;
        var port = defaultPort;

        var colon = value.LastIndexOf(':');

        // IPv6 字面量形如 [::1]:7000,用方括号区分
        if (colon >= 0)
        {
            var hostPart = value[..colon];
            var portPart = value[(colon + 1)..];

            if (hostPart.StartsWith('[') && hostPart.EndsWith(']'))
            {
                hostPart = hostPart[1..^1];
            }

            if (!int.TryParse(portPart, out port))
            {
                error = $"端口部分「{portPart}」不是数字。";
                return false;
            }

            host = hostPart;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "主机名为空。";
            return false;
        }

        if (port is < 1 or > 65535)
        {
            error = $"端口 {port} 超出 1–65535 范围。";
            return false;
        }

        // 主机部分应当是 IP 或合法 DNS 名称
        if (!IPAddress.TryParse(host, out _) && !IsPlausibleHostName(host))
        {
            error = $"主机名「{host}」不是合法的 IP 或域名。";
            return false;
        }

        endpoint = new Endpoint(host, port);
        return true;
    }

    /// <summary>简单校验:只含字母数字、连字符与点,且不以点或连字符开头结尾。</summary>
    private static bool IsPlausibleHostName(string host)
    {
        if (host.Length is 0 or > 253)
        {
            return false;
        }

        if (host.StartsWith('.') || host.EndsWith('.') || host.StartsWith('-') || host.EndsWith('-'))
        {
            return false;
        }

        foreach (var ch in host)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '.' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Host}:{Port}";
}
