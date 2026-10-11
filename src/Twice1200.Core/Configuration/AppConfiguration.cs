using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twice1200.Core.Configuration;

/// <summary>
/// 应用程序配置。序列化到 <c>config.json</c>。
/// <para>
/// **本文件永远不包含任何明文密钥。** 密钥只以 <see cref="SecretReference"/> 形式引用。
/// </para>
/// </summary>
public sealed class AppConfiguration
{
    /// <summary>配置格式版本。用于以后做配置迁移。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>n2n 组网设置。</summary>
    public N2nConfiguration N2n { get; set; } = new();

    /// <summary>frp 穿透设置。</summary>
    public FrpConfiguration Frp { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转成 \uXXXX,配置文件要便于人查看与手工修改
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>从 JSON 文本解析。</summary>
    /// <exception cref="JsonException">文本不是合法 JSON 时抛出。</exception>
    public static AppConfiguration Parse(string json)
        => JsonSerializer.Deserialize<AppConfiguration>(json, SerializerOptions) ?? new AppConfiguration();

    /// <summary>序列化为 JSON 文本。</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// 生成一份**不含任何个人信息的示例配置**。
    /// 组名与密码引用留空,由用户在向导里填写。
    /// </summary>
    public static AppConfiguration CreateTemplate() => new()
    {
        SchemaVersion = 1,
        N2n = new N2nConfiguration
        {
            Supernode = "supernode.invalid:7777",
            Community = string.Empty,
            AssignedIp = null,
            VirtualNetwork = "10.42.0.0/24",
            Key = SecretReference.Dpapi("n2n/key"),
            Tap = new TapConfiguration
            {
                AdapterName = "n2n-TAP",
                FallbackAdapterName = "n2n-TAP",
                AutoFallbackOnNonChineseCodePage = true,
            },
        },
        Frp = new FrpConfiguration
        {
            ServerAddress = "frp.invalid",
            ServerPort = 7000,
            AuthToken = SecretReference.Dpapi("frp/token"),
            RequireTls = true,
            Proxies =
            [
                new FrpProxy
                {
                    Name = "web",
                    Type = "tcp",
                    LocalIp = "127.0.0.1",
                    LocalPort = 25565,
                    RemotePort = 25565,
                },
            ],
        },
    };
}
