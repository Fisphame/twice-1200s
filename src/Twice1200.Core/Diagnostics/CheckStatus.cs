namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 单项检查的结果状态。
/// </summary>
public enum CheckStatus
{
    /// <summary>通过。</summary>
    Ok,

    /// <summary>可用但存在隐患,需要用户注意或后续处理。</summary>
    Warn,

    /// <summary>失败,会阻碍目标功能。</summary>
    Fail,

    /// <summary>因前置条件不满足而未执行。</summary>
    Skip,
}
