namespace Twice1200.Core.Configuration;

/// <summary>
/// 密钥的来源。
/// </summary>
public enum SecretSource
{
    /// <summary>未设置。校验时应视为错误。</summary>
    None,

    /// <summary>由 Windows DPAPI 加密后存放在本机凭据存储中(推荐)。</summary>
    Dpapi,
}

/// <summary>
/// 指向一个密钥的**引用**,而不是密钥本身。
/// <para>
/// 这是安全设计的核心:配置文件里永远不会出现密码或 token 的明文,
/// 只保存"这个密钥叫什么名字、从哪里取"。
/// 真正的密文存放在 <c>%APPDATA%\Twice1200\secrets.dat</c>(DPAPI 加密)。
/// </para>
/// </summary>
/// <param name="Handle">凭据存储中的键名。</param>
/// <param name="Source">来源类型。</param>
public sealed record SecretReference(string Handle, SecretSource Source = SecretSource.Dpapi)
{
    /// <summary>一个未设置的引用。</summary>
    public static SecretReference Unset { get; } = new(string.Empty, SecretSource.None);

    /// <summary>是否已有效配置。</summary>
    public bool IsSet => Source != SecretSource.None && !string.IsNullOrWhiteSpace(Handle);

    /// <summary>创建一个 DPAPI 引用。</summary>
    public static SecretReference Dpapi(string handle) => new(handle, SecretSource.Dpapi);
}
