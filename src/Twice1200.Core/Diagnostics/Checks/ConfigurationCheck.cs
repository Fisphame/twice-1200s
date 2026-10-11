using Twice1200.Core.Configuration;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>
/// 校验配置文件的完整性与其中的风险项。
/// <para>
/// 报告里**只出现配置路径与问题描述,不出现任何密码或 token 的内容**。
/// </para>
/// </summary>
public sealed class ConfigurationCheck : IEnvironmentCheck
{
    /// <summary>报告里最多列出多少条问题,避免刷屏。</summary>
    private const int MaxIssuesShown = 12;

    /// <inheritdoc />
    public string Id => CheckId.ConfigN2n;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Configuration is null)
        {
            return Task.FromResult(CheckResult.Fail(
                CheckId.ConfigN2n,
                "配置文件",
                options.ConfigurationPath is null
                    ? "尚未创建配置文件,程序还没有完成初始配置。"
                    : $"配置文件不存在:{options.ConfigurationPath}",
                hints:
                [
                    "首次使用时程序会引导你填写服务器地址、组名与密码,然后生成配置文件。",
                    "也可以用 --init-config 生成一份模板,再手工填写。",
                ]));
        }

        var issues = ConfigurationValidator.Validate(options.Configuration);
        var errors = issues.Count(i => i.Severity == ValidationSeverity.Error);
        var warnings = issues.Count(i => i.Severity == ValidationSeverity.Warning);
        var infos = issues.Count(i => i.Severity == ValidationSeverity.Info);

        var details = new List<string>
        {
            $"配置文件 : {options.ConfigurationPath ?? "(内存中构造)"}",
            $"n2n 组名 : {(string.IsNullOrWhiteSpace(options.Configuration.N2n.Community) ? "(未设置)" : options.Configuration.N2n.Community)}",
            $"超级节点 : {options.Configuration.N2n.Supernode}",
            $"虚拟网段 : {options.Configuration.N2n.VirtualNetwork}",
            $"本机虚拟IP: {options.Configuration.N2n.AssignedIp ?? "(自动挑选)"}",
            $"密码引用 : {DescribeReference(options.Configuration.N2n.Key)}",
            $"frp 服务端: {options.Configuration.Frp.ServerAddress}:{options.Configuration.Frp.ServerPort}",
            $"frp token: {DescribeReference(options.Configuration.Frp.AuthToken)}",
            $"frp TLS  : {(options.Configuration.Frp.RequireTls ? "要求" : "未要求")}",
        };

        if (issues.Count == 0)
        {
            return Task.FromResult(CheckResult.Ok(
                CheckId.ConfigN2n,
                "配置文件",
                "结构完整,未发现风险项。",
                details));
        }

        foreach (var issue in issues.Take(MaxIssuesShown))
        {
            var label = issue.Severity switch
            {
                ValidationSeverity.Error => "错误",
                ValidationSeverity.Warning => "警告",
                _ => "提示",
            };

            details.Add($"[{label}] {issue.Path} — {issue.Message}");
            if (issue.Suggestion is not null)
            {
                details.Add($"        → {issue.Suggestion}");
            }
        }

        if (issues.Count > MaxIssuesShown)
        {
            details.Add($"……另有 {issues.Count - MaxIssuesShown} 条未列出。");
        }

        details.Add("说明:密钥只以引用形式记录,上面的「密码引用」显示的是它在凭据库里的键名与来源,不是密码本身。");

        var summary = $"共 {errors} 个错误、{warnings} 个警告、{infos} 个提示。";

        if (errors > 0)
        {
            return Task.FromResult(CheckResult.Fail(CheckId.ConfigN2n, "配置文件", summary, details));
        }

        return Task.FromResult(warnings > 0
            ? CheckResult.Warn(CheckId.ConfigN2n, "配置文件", summary, details)
            : CheckResult.Ok(CheckId.ConfigN2n, "配置文件", summary, details));
    }

    private static string DescribeReference(SecretReference reference) => reference.Source switch
    {
        SecretSource.Dpapi => $"已引用凭据库键「{reference.Handle}」(DPAPI 加密存储)",
        _ => "(未设置)",
    };
}
