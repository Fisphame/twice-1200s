namespace Twice1200.Core.Configuration;

/// <summary>校验发现的问题的严重程度。</summary>
public enum ValidationSeverity
{
    /// <summary>仅作提示,不影响使用。</summary>
    Info,

    /// <summary>可以继续,但存在风险或隐患。</summary>
    Warning,

    /// <summary>无法继续,必须先修复。</summary>
    Error,
}

/// <summary>
/// 一条校验问题。
/// </summary>
/// <param name="Severity">严重程度。</param>
/// <param name="Path">出问题的配置路径,例如 <c>n2n.community</c>。</param>
/// <param name="Message">问题描述。</param>
/// <param name="Suggestion">建议的处理方式。</param>
public sealed record ValidationIssue(
    ValidationSeverity Severity,
    string Path,
    string Message,
    string? Suggestion = null);
