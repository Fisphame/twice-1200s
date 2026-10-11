namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 一项环境检查。实现应当是幂等且只读的 —— 除了显式允许的网段扫描,
/// 体检过程**不应该修改任何系统状态**。
/// </summary>
public interface IEnvironmentCheck
{
    /// <summary>检查编号,取自 <see cref="CheckId"/>。</summary>
    string Id { get; }

    /// <summary>执行检查。不应抛出异常;无法完成时返回 <see cref="CheckStatus.Fail"/>。</summary>
    Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default);
}
