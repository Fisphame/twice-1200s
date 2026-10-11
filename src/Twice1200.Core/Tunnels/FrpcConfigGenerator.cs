using System.Text;
using Twice1200.Core.Configuration;

namespace Twice1200.Core.Tunnels;

/// <summary>
/// 生成 <c>frpc</c> 的 TOML 配置文本。
/// <para>
/// <b>为什么自己拼 TOML 而不引第三方库:</b> 需要写的结构非常小
/// (顶层几项 + 一个 proxies 数组),而本项目刻意保持零 NuGet 依赖。
/// 代价是转义要自己负责 —— 因此 <see cref="EscapeString"/> 有专门的测试盯着。
/// </para>
/// <para>
/// <b>字段名是按 frp 0.68.0 实测确认的</b>,不是猜的:
/// 用 <c>frpc verify</c> 逐项验证过,而且该版本 <c>--strict-config</c> 默认为真,
/// 写错字段名会直接报错。改动这些字段前请先用 <c>frpc verify</c> 复验。
/// </para>
/// </summary>
public static class FrpcConfigGenerator
{
    /// <summary>
    /// 生成配置文本。
    /// </summary>
    /// <param name="configuration">frp 配置。</param>
    /// <param name="authToken">
    /// 鉴权 token 的<b>明文</b>。调用方负责保证它不进入日志。
    /// </param>
    public static string Generate(FrpConfiguration configuration, string authToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(authToken);

        var sb = new StringBuilder();

        sb.AppendLine("# 本文件由 Twice1200 自动生成,请勿手工修改。");
        sb.AppendLine("# 它包含明文 token,因此只应存在于权限受限的临时目录中,并在用完后删除。");
        sb.AppendLine();

        sb.Append("serverAddr = ").AppendLine(Quote(configuration.ServerAddress));
        sb.Append("serverPort = ").AppendLine(configuration.ServerPort.ToString());

        // token 只在配置文件里出现,绝不走命令行 ——
        // 命令行对本机任何进程都是可读的。
        sb.Append("auth.token = ").AppendLine(Quote(authToken));

        sb.AppendLine();
        sb.Append("transport.tls.enable = ")
          .AppendLine(configuration.RequireTls ? "true" : "false");

        // 日志固定只走控制台:由我们的进程捕获并脱敏。
        // 刻意不写日志文件 —— 那会在磁盘上留下第二份可能含 token 的日志。
        sb.AppendLine();
        sb.AppendLine("log.to = \"console\"");
        sb.AppendLine("log.level = \"info\"");

        foreach (var proxy in configuration.Proxies)
        {
            sb.AppendLine();
            sb.AppendLine("[[proxies]]");
            sb.Append("name = ").AppendLine(Quote(proxy.Name));
            sb.Append("type = ").AppendLine(Quote(proxy.Type));
            sb.Append("localIP = ").AppendLine(Quote(proxy.LocalIp));
            sb.Append("localPort = ").AppendLine(proxy.LocalPort.ToString());
            sb.Append("remotePort = ").AppendLine(proxy.RemotePort.ToString());
        }

        return sb.ToString();
    }

    /// <summary>
    /// 把字符串编码成 TOML 基本字符串(双引号形式)。
    /// </summary>
    /// <remarks>
    /// 按 TOML 规范转义:反斜杠、双引号、以及所有控制字符。
    /// 控制字符用 <c>\uXXXX</c> 形式,这是唯一在所有情况下都安全的选择。
    /// </remarks>
    public static string Quote(string? value)
    {
        if (value is null)
        {
            return "\"\"";
        }

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;

                case '\\':
                    sb.Append("\\\\");
                    break;

                case '\b':
                    sb.Append("\\b");
                    break;

                case '\t':
                    sb.Append("\\t");
                    break;

                case '\n':
                    sb.Append("\\n");
                    break;

                case '\f':
                    sb.Append("\\f");
                    break;

                case '\r':
                    sb.Append("\\r");
                    break;

                default:
                    if (char.IsControl(ch))
                    {
                        sb.Append("\\u").Append(((int)ch).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 构造要传给 <c>frpc</c> 的参数。
    /// <para>
    /// <b>这里没有 token。</b> 这是刻意的:token 只通过配置文件传递。
    /// 有一条测试专门断言参数列表里不含 token。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        return ["-c", configPath];
    }
}
