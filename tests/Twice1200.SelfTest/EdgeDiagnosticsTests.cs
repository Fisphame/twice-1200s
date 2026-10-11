using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics;
using Twice1200.Core.Diagnostics.Checks;

namespace Twice1200.SelfTest;

/// <summary>
/// 网卡名安全性与 edge 日志分类。
/// <para>
/// 这两块都由一次真实排查得来:网卡名含空格 + 参数拼接漏引号,
/// 导致 <c>以太网 2</c> 被拆成两个参数,现象却像驱动故障。
/// 这些用例的目的就是让那个坑不会再回来。
/// </para>
/// </summary>
public static class EdgeDiagnosticsTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        // ---------------------------------------------------------------- 网卡名安全性

        suite.Test("网卡名安全性:含空格的名称给出警告而不是错误", () =>
        {
            // 含空格本身是合法的,只要调用方正确加引号。
            // 判成错误会让用户以为必须改名,那是误导。
            var result = RunNameCheck("以太网 2");

            Check.Equal(CheckStatus.Warn, result.Status, "含空格应报为警告");
            Check.Contains(result.Message, "引号", "应指出真正的原因是引号");
        });

        suite.Test("网卡名安全性:纯 ASCII 无空格的名称直接通过", () =>
        {
            var result = RunNameCheck("n2n-TAP");

            Check.True(
                result.Status is CheckStatus.Ok or CheckStatus.Warn,
                "应通过(若系统上不存在该网卡,允许报「配置名不存在」的警告)");

            if (result.Status == CheckStatus.Warn)
            {
                Check.DoesNotContain(result.Message, "引号", "不含空格就不该提引号问题");
            }
        });

        suite.Test("网卡名安全性:含双引号或反斜杠是错误", () =>
        {
            foreach (var bad in new[] { "bad\"name", "bad\\name" })
            {
                var result = RunNameCheck(bad);
                Check.Equal(CheckStatus.Fail, result.Status, $"「{bad}」应被报为错误");
            }
        });

        suite.Test("网卡名安全性:首尾空白是错误", () =>
        {
            var result = RunNameCheck("n2n-TAP ");
            Check.Equal(CheckStatus.Fail, result.Status, "首尾空白几乎总是配置写错");
        });

        suite.Test("网卡名安全性:名称为空是错误", () =>
        {
            var result = RunNameCheck(string.Empty);
            Check.Equal(CheckStatus.Fail, result.Status, "空名称应被报为错误");
        });

        suite.Test("网卡名安全性:报告里会列出系统上真实的网卡名", () =>
        {
            var result = RunNameCheck("以太网 2");
            Check.NotNull(result.Details, "应带细节");
            Check.True(result.Details!.Count > 0, "细节不应为空");
        });

        // ---------------------------------------------------------------- edge 日志分类

        suite.Test("edge 日志:权限不足(枚举不到任何设备)", () =>
        {
            const string log = """
                11/Oct/2026 23:09:26 [edge.c:1119] using AES cipher.
                11/Oct/2026 23:09:26 [edge.c:1143] use manually set IP address
                No Windows tap devices found, did you run tapinstall.exe?
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.NoTapDevicesAtAll, "应识别出「一个设备都没枚举到」");
            Check.False(d.DeviceOpened, "不应认为设备已打开");
            Check.Equal("AES", d.Cipher, "应解析出加密算法");
        });

        suite.Test("edge 日志:名称不存在(设备列表是能枚举的)", () =>
        {
            const string log = """
                using AES cipher.
                use manually set IP address
                Cannot find tap device "ProbeNoSuchAdapter"
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.NamedDeviceNotFound, "应识别出「名称不存在」");
            Check.False(d.NoTapDevicesAtAll, "这不是「一个设备都没有」");
            Check.Equal("ProbeNoSuchAdapter", d.ReportedDeviceName, "应提取出它实际收到的名称");
        });

        suite.Test("edge 日志:能捕捉到参数被空格截断", () =>
        {
            // 这正是真实发生过的 bug:传"以太网 2",edge 收到"以太网"
            const string log = """Cannot find tap device "以太网" """;
            var d = EdgeProbeDiagnosis.Parse(log);

            Check.Equal("以太网", d.ReportedDeviceName, "应提取出被截断的名称");
            Check.False(
                string.Equals(d.ReportedDeviceName, "以太网 2", StringComparison.Ordinal),
                "截断后的名称应与传入值不同 —— 调用方据此发现参数被改变");
        });

        suite.Test("edge 日志:成功启动并打开设备", () =>
        {
            const string log = """
                using AES cipher.
                use manually set IP address
                Open device [name={2952F71C}][ip=10.236.167.114][ifName=以太网 2][MTU=1290]
                [edge.c:1258] created local tap device IP: 10.236.167.114, Mask: 255.255.255.0
                [edge.c:1356] edge started
                [edge_utils.c:1595] registering with supernode [1200s.top:7777]
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.DeviceOpened, "应识别出已打开设备");
            Check.True(d.EdgeStarted, "应识别出已启动");
            Check.True(d.AttemptedRegistration, "应识别出已发起注册");
            Check.False(d.NoTapDevicesAtAll, "不应误报");
            Check.False(d.NamedDeviceNotFound, "不应误报");
        });

        suite.Test("edge 日志:超级节点无响应", () =>
        {
            const string log = """
                using AES cipher.
                [edge.c:1356] edge started
                [edge_utils.c:1595] registering with supernode [1200s.top:7777][attempts left 2]
                [edge_utils.c:1556] WARNING: supernode not responding, now trying [1200s.top:7777]
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.AttemptedRegistration, "应识别出已发起注册");
            Check.True(d.SupernodeSilent, "应识别出超级节点没回");
            Check.False(d.SupernodeResponded, "不应认为超级节点已响应");
        });

        suite.Test("edge 日志:检测到加密被禁用(密钥没生效)", () =>
        {
            const string log = """
                using null cipher.
                [edge_utils.c:474] WARNING: encryption is disabled in edge
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.EncryptionDisabled, "应识别出加密被禁用");
            Check.Equal("null", d.Cipher, "应解析出 null cipher");
        });

        suite.Test("edge 日志:未指定 -a 时走「向超级节点要 IP」这条路径", () =>
        {
            // 这条路径与"打开网卡"互斥。实测时踩过坑:探针没传 -a,
            // 日志里完全不出现 "Cannot find tap device",一度以为分类器坏了。
            const string log = """
                using null cipher.
                [edge_utils.c:474] WARNING: encryption is disabled in edge
                [edge.c:1149] automatically assign IP address by supernode
                [edge.c:1221] send REGISTER_SUPER to supernode [1200s.top:7777] asking for IP address
                """;

            var d = EdgeProbeDiagnosis.Parse(log);

            Check.True(d.RequestedIpAssignment, "应识别出「向超级节点要 IP」这一步");
            Check.False(d.DeviceOpened, "这条路径不会打开网卡");
            Check.False(d.EdgeStarted, "还没到启动完成");
            Check.True(d.AttemptedRegistration, "REGISTER_SUPER 也算发起过注册");
        });

        suite.Test("edge 日志:设备层健康判定覆盖三条路径", () =>
        {
            var open = EdgeProbeDiagnosis.Parse("Open device [name={X}]");
            Check.True(open.TapLayerHealthy, "能打开设备 => 设备层好");

            var started = EdgeProbeDiagnosis.Parse("[edge.c:1356] edge started");
            Check.True(started.TapLayerHealthy, "能启动 => 设备层好");

            var requestedIp = EdgeProbeDiagnosis.Parse("asking for IP address");
            Check.True(requestedIp.TapLayerHealthy, "能要到 IP 这一步 => 设备层好");

            var denied = EdgeProbeDiagnosis.Parse("No Windows tap devices found, did you run tapinstall.exe?");
            Check.False(denied.TapLayerHealthy, "枚举不到设备 => 设备层不健康");
        });

        suite.Test("edge 日志:空输入不崩溃", () =>
        {
            var d = EdgeProbeDiagnosis.Parse(string.Empty);

            Check.False(d.NoTapDevicesAtAll, "空日志不应误判");
            Check.False(d.DeviceOpened, "空日志不应误判");
            Check.Equal<string?>(null, d.Cipher, "空日志没有加密信息");
        });

        suite.Test("edge 探测:硬性约束被写进了常量", () =>
        {
            // 探针必须指向不存在的东西,否则会改动真实网络配置
            Check.Contains(
                EdgeProbeRunner.NonExistentAdapterName,
                "NoSuch",
                "探针网卡名必须明显不存在,避免误配置真实网卡");
            Check.Equal("N2N_KEY", EdgeProbeRunner.KeyEnvironmentVariable, "n2n 读取密钥的环境变量名");
        });

        // ---------------------------------------------------------------- 日志脱敏

        suite.Test("脱敏:已知密钥值被替换掉", () =>
        {
            const string secret = "S3cretValueThatShouldNotLeak";
            Environment.SetEnvironmentVariable("TWICE1200_SELFTEST_SECRET", secret);

            try
            {
                var lines = new[]
                {
                    $"before {secret} after",
                    "这一行没有密钥",
                    $"重复出现 {secret} 和 {secret}",
                };

                var scrubbed = Twice1200.Core.Runtime.Redaction.ScrubSecrets(lines);

                foreach (var line in scrubbed)
                {
                    Check.DoesNotContain(line, secret, "脱敏后不应残留密钥明文");
                }

                Check.Contains(scrubbed[0], "before", "非密钥部分应保留");
                Check.Contains(scrubbed[1], "这一行没有密钥", "无关行不应被改动");
            }
            finally
            {
                Environment.SetEnvironmentVariable("TWICE1200_SELFTEST_SECRET", null);
            }
        });

        suite.Test("脱敏:不会误伤普通环境变量", () =>
        {
            // PATH 这类值含反斜杠,不该被当成密钥
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            if (path.Length < 8)
            {
                return;
            }

            var scrubbed = Twice1200.Core.Runtime.Redaction.ScrubSecrets([path]);
            Check.Contains(scrubbed[0], "Windows", "系统路径不应被脱敏破坏");
        });
    }

    /// <summary>用指定的网卡名跑一次安全性检查。</summary>
    private static CheckResult RunNameCheck(string adapterName)
    {
        var config = new AppConfiguration();
        config.N2n.Tap.AdapterName = adapterName;

        var options = new DoctorOptions
        {
            Configuration = config,
            Assets = Twice1200.Core.Assets.AssetLocator.Resolve(explicitRoot: null, startDirectory: Check.TempRoot),
        };

        return new AdapterNameSafetyCheck().RunAsync(options).GetAwaiter().GetResult();
    }
}
