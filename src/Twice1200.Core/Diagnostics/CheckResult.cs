namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 单项检查的完整结果。不可变。
/// </summary>
/// <param name="Id">稳定的检查编号,用于脚本与日志定位。见 <see cref="CheckId"/>。</param>
/// <param name="Title">人可读的检查名称。</param>
/// <param name="Status">结果状态。</param>
/// <param name="Message">一行结论。**绝不允许包含密码、token 或其它凭据。**</param>
/// <param name="Details">补充细节,可为空。</param>
/// <param name="Hints">发现问题时的建议操作,可为空。</param>
public sealed record CheckResult(
    string Id,
    string Title,
    CheckStatus Status,
    string Message,
    IReadOnlyList<string>? Details = null,
    IReadOnlyList<string>? Hints = null)
{
    /// <summary>构造一个通过结果。</summary>
    public static CheckResult Ok(string id, string title, string message, IReadOnlyList<string>? details = null)
        => new(id, title, CheckStatus.Ok, message, details);

    /// <summary>构造一个警告结果。</summary>
    public static CheckResult Warn(string id, string title, string message,
        IReadOnlyList<string>? details = null, IReadOnlyList<string>? hints = null)
        => new(id, title, CheckStatus.Warn, message, details, hints);

    /// <summary>构造一个失败结果。</summary>
    public static CheckResult Fail(string id, string title, string message,
        IReadOnlyList<string>? details = null, IReadOnlyList<string>? hints = null)
        => new(id, title, CheckStatus.Fail, message, details, hints);

    /// <summary>构造一个跳过结果。</summary>
    public static CheckResult Skip(string id, string title, string message,
        IReadOnlyList<string>? details = null)
        => new(id, title, CheckStatus.Skip, message, details);

    /// <summary>把状态换成另一个,保留其余内容。用于"发现成功但发现问题"的场景。</summary>
    public CheckResult WithStatus(CheckStatus status) => this with { Status = status };
}
