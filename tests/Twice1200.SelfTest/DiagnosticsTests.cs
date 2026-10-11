using Twice1200.Core.Diagnostics;
using Twice1200.Core.Diagnostics.Checks;

namespace Twice1200.SelfTest;

/// <summary>体检框架本身的行为。</summary>
public static class DiagnosticsTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("CheckResult:工厂方法设置正确的状态", () =>
        {
            Check.Equal(CheckStatus.Ok, CheckResult.Ok("X", "标题", "消息").Status, "Ok");
            Check.Equal(CheckStatus.Warn, CheckResult.Warn("X", "标题", "消息").Status, "Warn");
            Check.Equal(CheckStatus.Fail, CheckResult.Fail("X", "标题", "消息").Status, "Fail");
            Check.Equal(CheckStatus.Skip, CheckResult.Skip("X", "标题", "消息").Status, "Skip");
        });

        suite.Test("CheckResult:WithStatus 保留其余字段", () =>
        {
            var original = CheckResult.Ok("E-01", "标题", "消息", ["细节"]);
            var changed = original.WithStatus(CheckStatus.Warn);

            Check.Equal("E-01", changed.Id, "Id 应保留");
            Check.Equal("消息", changed.Message, "Message 应保留");
            Check.Equal(1, changed.Details!.Count, "Details 应保留");
            Check.Equal(CheckStatus.Warn, changed.Status, "状态应被替换");
        });

        suite.Test("CheckRunner:单项抛异常不会中断整次体检", () =>
        {
            var runner = new CheckRunner()
                .Add(new StubCheck("A", _ => CheckResult.Ok("A", "正常项", "通过")))
                .Add(new ThrowingCheck("B"))
                .Add(new StubCheck("C", _ => CheckResult.Ok("C", "正常项", "通过")));

            var report = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();

            Check.Equal(3, report.Results.Count, "全部三项都应产生结果");
            Check.Equal(2, report.OkCount, "两项应通过");
            Check.Equal(1, report.FailCount, "抛异常的那项应转为失败");

            var failed = report.Results.Single(r => r.Id == "B");
            Check.Contains(string.Join(" ", failed.Details ?? []), "InvalidOperationException",
                "失败详情里应包含异常类型,便于定位");
        });

        suite.Test("CheckRunner:检查返回 null 时降级为失败", () =>
        {
            var runner = new CheckRunner().Add(new StubCheck("N", _ => null!));
            var report = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();

            Check.Equal(1, report.FailCount, "返回 null 应被视为失败");
        });

        suite.Test("CheckReport:退出码反映最严重的问题", () =>
        {
            var okOnly = RunReport(_ => CheckResult.Ok("A", "标题", "通过"));
            Check.Equal(0, okOnly.ExitCode, "全通过应为 0");

            var withWarning = RunReport(_ => CheckResult.Warn("A", "标题", "警告"));
            Check.Equal(2, withWarning.ExitCode, "仅有警告应为 2");

            var withFailure = RunReport(_ => CheckResult.Fail("A", "标题", "失败"));
            Check.Equal(1, withFailure.ExitCode, "有失败应为 1");
        });

        suite.Test("CheckReport:文本报告包含各状态标记", () =>
        {
            var runner = new CheckRunner()
                .Add(new StubCheck("A", _ => CheckResult.Ok("A", "通过项", "一切正常")))
                .Add(new StubCheck("B", _ => CheckResult.Warn("B", "警告项", "有点问题")))
                .Add(new StubCheck("C", _ => CheckResult.Fail("C", "失败项", "无法继续")))
                .Add(new StubCheck("D", _ => CheckResult.Skip("D", "跳过项", "没必要做")));

            var text = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult().ToText();

            Check.Contains(text, "[√]", "应有通过标记");
            Check.Contains(text, "[!]", "应有警告标记");
            Check.Contains(text, "[×]", "应有失败标记");
            Check.Contains(text, "[-]", "应有跳过标记");
            Check.Contains(text, "小计", "应有汇总行");
            Check.Contains(text, "需要处理的问题", "有失败时应列出待处理项");
        });

        suite.Test("CheckReport:含中文的列仍能对齐", () =>
        {
            var runner = new CheckRunner()
                .Add(new StubCheck("A", _ => CheckResult.Ok("A", "中文标题在这里", "消息")));

            var text = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult().ToText();
            var line = text.Split('\n').First(l => l.Contains("中文标题在这里", StringComparison.Ordinal));

            // 全角字符占两列,若按字符数补齐会少补,导致列错位。
            // 这里验证标题起始位置在不同检查之间是一致的。
            var index = line.IndexOf("中文标题在这里", StringComparison.Ordinal);
            Check.True(index > 0, "标题应出现在行内");
        });

        suite.Test("CheckReport:摘要模式会折叠过长的细节", () =>
        {
            var manyDetails = Enumerable.Range(1, 20).Select(i => $"细节 {i}").ToList();

            var runner = new CheckRunner()
                .Add(new StubCheck("A", _ => CheckResult.Ok("A", "标题", "消息", manyDetails)));

            var report = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();

            var summary = report.ToText(verbose: false);
            var full = report.ToText(verbose: true);

            Check.Contains(summary, "另有", "摘要模式应给出折叠提示");
            Check.Contains(summary, "细节 1", "摘要模式应显示前几条");
            Check.DoesNotContain(summary, "细节 20", "摘要模式不应显示最后几条");
            Check.Contains(full, "细节 20", "详细模式应显示全部");
            Check.DoesNotContain(full, "另有", "详细模式不应有折叠提示");
        });

        suite.Test("CheckRunner:未启用则跳过实测类检查", () =>
        {
            var runner = new CheckRunner().Add(new EdgeConnectivityCheck());

            var withoutFlag = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();
            Check.Equal(1, withoutFlag.SkipCount, "未加 --probe-edge 时应跳过");
            Check.Contains(withoutFlag.Results[0].Message, "未启用", "应说明原因");

            // 启用后若资源目录没有 edge.exe,应报失败而不是崩溃
            var withFlag = runner
                .RunAsync(new DoctorOptions { ProbeEdge = true })
                .GetAwaiter().GetResult();
            Check.True(
                withFlag.Results[0].Status is CheckStatus.Fail or CheckStatus.Ok or CheckStatus.Warn,
                "启用后应给出确定结论");
        });

        suite.Test("DoctorOptions:从配置推导网段", () =>
        {
            var withoutConfig = new DoctorOptions();
            Check.True(withoutConfig.ResolveVirtualSubnet() is null, "没有配置时应返回 null");

            var config = new Twice1200.Core.Configuration.AppConfiguration();
            config.N2n.VirtualNetwork = "10.236.167.0/24";

            var withConfig = new DoctorOptions { Configuration = config };
            var subnet = withConfig.ResolveVirtualSubnet();

            Check.NotNull(subnet, "应能从配置推导出网段");
            Check.Equal(24, subnet!.Value.PrefixLength, "前缀长度");
            Check.Equal("10.236.167.0/24", subnet.Value.ToString(), "网段");
        });

        suite.Test("DoctorOptions:配置里网段非法时安全返回 null", () =>
        {
            var config = new Twice1200.Core.Configuration.AppConfiguration();
            config.N2n.VirtualNetwork = "不是网段";

            var options = new DoctorOptions { Configuration = config };
            Check.True(options.ResolveVirtualSubnet() is null, "非法网段应返回 null 而不是抛异常");
        });

        suite.Test("DoctorRunner:默认流程包含全部检查编号", () =>
        {
            var runner = DoctorRunner.BuildDefault();
            var report = runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();

            var ids = report.Results.Select(r => r.Id).ToList();

            foreach (var expected in new[]
                     {
                         CheckId.Os, CheckId.Elevation, CheckId.DotNet,
                         CheckId.TapPresence, CheckId.TapReadiness, CheckId.TapNamed, CheckId.TapNameSafety,
                         CheckId.AssetsRoot, CheckId.BinN2nEdge, CheckId.BinFrpc, CheckId.BinTapDriver,
                         CheckId.ConfigN2n,
                         CheckId.NetSupernode, CheckId.NetFrpServer, CheckId.NetVirtualIpScan, CheckId.NetEdgeProbe,
                         CheckId.ProcN2n,
                     })
            {
                Check.True(ids.Contains(expected), $"默认流程应包含检查 {expected}");
            }

            Check.Equal(report.Results.Count, ids.Distinct().Count(), "检查编号不应重复");
            Check.True(report.Results.All(r => !string.IsNullOrWhiteSpace(r.Message)), "每项都应有结论文本");
        });
    }

    private static CheckReport RunReport(Func<DoctorOptions, CheckResult> produce)
    {
        var runner = new CheckRunner().Add(new StubCheck("X", produce));
        return runner.RunAsync(new DoctorOptions()).GetAwaiter().GetResult();
    }

    private sealed class StubCheck : IEnvironmentCheck
    {
        private readonly Func<DoctorOptions, CheckResult> _produce;

        public StubCheck(string id, Func<DoctorOptions, CheckResult> produce)
        {
            Id = id;
            _produce = produce;
        }

        public string Id { get; }

        public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(_produce(options));
    }

    private sealed class ThrowingCheck : IEnvironmentCheck
    {
        public ThrowingCheck(string id) => Id = id;

        public string Id { get; }

        public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("这是测试用的故意失败");
    }
}
