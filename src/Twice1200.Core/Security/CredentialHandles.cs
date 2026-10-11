namespace Twice1200.Core.Security;

/// <summary>
/// 凭据库里使用的标准键名。
/// <para>
/// <b>它们是稳定契约。</b> 键名会出现在 <c>config.json</c> 的密钥引用里,
/// 改了就会让用户已保存的凭据找不到。需要调整时应当提供迁移。
/// </para>
/// </summary>
public static class CredentialHandles
{
    /// <summary>n2n 网络密码。</summary>
    public const string N2nKey = "n2n/key";

    /// <summary>frp 鉴权 token。</summary>
    public const string FrpAuthToken = "frp/token";

    /// <summary>全部标准键名。</summary>
    public static IReadOnlyList<string> All { get; } = [N2nKey, FrpAuthToken];

    /// <summary>给键名附上人可读的用途说明。</summary>
    public static string Describe(string handle) => handle switch
    {
        N2nKey => "n2n 网络密码(所有组员必须一致)",
        FrpAuthToken => "frp 服务端鉴权 token",
        _ => "自定义键",
    };
}
