using System.Diagnostics;
using System.Text;

namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 环境体检的执行器。
/// <para>
/// 设计原则:任何单项检查都不允许把整次体检带崩。
/// 未预期的异常会被捕获并转成一条 <see cref="CheckStatus.Fail"/> 结果,
/// 因为"体检本身挂了"对排查问题毫无帮助。
/// </para>
/// </summary>
public sealed class CheckRunner
{
    private readonly List<IEnvironmentCheck> _checks = [];

    /// <summary>按顺序加入一项检查。</summary>
    public CheckRunner Add(IEnvironmentCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        _checks.Add(check);
        return this;
    }

    /// <summary>按顺序加入多项检查。</summary>
    public CheckRunner AddRange(IEnumerable<IEnvironmentCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        foreach (var check in checks)
        {
            Add(check);
        }

        return this;
    }

    /// <summary>依次执行全部检查并汇总。</summary>
    public async Task<CheckReport> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var report = new CheckReport();

        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            CheckResult result;

            try
            {
                result = await check.RunAsync(options, cancellationToken).ConfigureAwait(false)
                         ?? CheckResult.Fail(check.Id, check.Id, "检查未返回结果。");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 意图:体检必须能跑完。单项崩溃降级为一条失败记录。
                result = CheckResult.Fail(
                    check.Id,
                    check.Id,
                    "检查过程抛出未预期的异常。",
                    details: [Describe(ex)]);
            }

            stopwatch.Stop();
            report.Add(result, stopwatch.Elapsed);
        }

        return report;
    }

    /// <summary>把异常压成一行可读文本,不做堆栈展开(报告面向用户,不是日志)。</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 2)
        {
            sb.Append(" ← ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            inner = inner.InnerException;
            depth++;
        }

        return sb.ToString();
    }
}
