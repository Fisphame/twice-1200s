using System.Runtime.InteropServices;
using Twice1200.Core.Assets;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;

namespace Twice1200.SelfTest;

/// <summary>PE 头解析与哈希。</summary>
public static class BinaryTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("PeInspector:系统 DLL 被识别为可执行文件", () =>
        {
            var kernel32 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "kernel32.dll");

            Check.True(File.Exists(kernel32), "系统目录下应存在 kernel32.dll");
            Check.True(PeInspector.LooksLikeExecutable(kernel32), "内核库应是合法的 PE 文件");
            Check.True(
                PeInspector.ReadArchitecture(kernel32) is PeArchitecture.X64 or PeArchitecture.X86 or PeArchitecture.Arm64,
                "应能识别出具体架构");
        });

        suite.Test("PeInspector:非可执行文件被拒绝", () =>
        {
            var temp = Path.Combine(Check.TempRoot, $"twice1200-probe-{Guid.NewGuid():N}.txt");

            try
            {
                File.WriteAllText(temp, "这不是一个可执行文件。");

                Check.False(PeInspector.LooksLikeExecutable(temp), "纯文本不应被识别为可执行文件");
                Check.Equal(PeArchitecture.Unknown, PeInspector.ReadArchitecture(temp), "架构应为未知");
            }
            finally
            {
                File.Delete(temp);
            }
        });

        suite.Test("PeInspector:文件不存在时安全返回", () =>
        {
            var missing = Path.Combine(Check.TempRoot, $"twice1200-missing-{Guid.NewGuid():N}.exe");

            Check.Equal(PeArchitecture.Unknown, PeInspector.ReadArchitecture(missing), "不应抛异常");
            Check.False(PeInspector.LooksLikeExecutable(missing), "不应抛异常");
        });

        suite.Test("PeInspector:架构描述非空", () =>
        {
            foreach (var architecture in Enum.GetValues<PeArchitecture>())
            {
                Check.True(
                    !string.IsNullOrWhiteSpace(PeInspector.Describe(architecture)),
                    $"架构 {architecture} 应有可读描述");
            }
        });

        suite.Test("Hashing:SHA256 稳定且与已知值一致", () =>
        {
            var temp = Path.Combine(Check.TempRoot, $"twice1200-hash-{Guid.NewGuid():N}.bin");

            try
            {
                // 空内容的 SHA256 是公开的已知值,用它验证算法接对了
                File.WriteAllBytes(temp, []);

                var hash = Hashing.Sha256FileAsync(temp).GetAwaiter().GetResult();
                Check.Equal(
                    "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855",
                    hash,
                    "空文件的 SHA256");
            }
            finally
            {
                File.Delete(temp);
            }
        });

        suite.Test("Redaction:指纹稳定且不含原文", () =>
        {
            const string secret = "mysecretpassword";

            var first = Redaction.Fingerprint(secret);
            var second = Redaction.Fingerprint(secret);

            Check.Equal(first, second, "同一输入的指纹应稳定");
            Check.Equal(8, first.Length, "指纹长度");
            Check.DoesNotContain(first, secret, "指纹不应包含原文");

            Check.Equal("(空)", Redaction.Fingerprint(null), "null 的处理");
            Check.Equal("(空)", Redaction.Fingerprint(string.Empty), "空串的处理");
        });

        suite.Test("Redaction:描述不泄露内容", () =>
        {
            const string secret = "mysecretpassword";
            var description = Redaction.Describe(secret);

            Check.DoesNotContain(description, secret, "描述不应包含原文");
            Check.Contains(description, "16", "应只给出长度");
        });
    }
}

/// <summary>资源目录定位。</summary>
public static class AssetLocatorTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("AssetLocator:显式指定优先", () =>
        {
            var root = Path.Combine(Check.TempRoot, $"twice1200-assets-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try
            {
                // 放一个特征文件,让它被识别为资源根目录
                File.WriteAllText(Path.Combine(root, "README.md"), "test");

                var locator = AssetLocator.Resolve(root);

                Check.True(locator.Found, "应能找到资源目录");
                Check.Equal(Path.GetFullPath(root), locator.Root!, "解析结果应为绝对路径");
                Check.Contains(locator.Origin, "命令行", "说明应指出它来自命令行参数");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        suite.Test("AssetLocator:指向不存在的目录时明确报告", () =>
        {
            var missing = Path.Combine(Check.TempRoot, $"twice1200-nope-{Guid.NewGuid():N}");
            var locator = AssetLocator.Resolve(missing);

            Check.False(locator.Found, "不存在的目录不应被判为有效");
            Check.Contains(locator.Origin, "不存在", "说明应解释原因");
        });

        suite.Test("AssetLocator:能从子目录向上找到资源目录", () =>
        {
            var root = Path.Combine(Check.TempRoot, $"twice1200-up-{Guid.NewGuid():N}");
            var assets = Path.Combine(root, "assets");
            var deep = Path.Combine(root, "src", "Project", "bin", "Debug", "net9.0");

            Directory.CreateDirectory(assets);
            Directory.CreateDirectory(deep);

            try
            {
                File.WriteAllText(Path.Combine(assets, "README.md"), "test");

                var locator = AssetLocator.Resolve(explicitRoot: null, startDirectory: deep);

                Check.True(locator.Found, "应能向上找到 assets/");
                Check.Equal(
                    Path.GetFullPath(assets),
                    Path.GetFullPath(locator.Root!),
                    "应定位到正确的层级");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        suite.Test("AssetLocator:Resolve 返回 null 当根目录未找到", () =>
        {
            var missing = Path.Combine(Check.TempRoot, $"twice1200-null-{Guid.NewGuid():N}");
            var locator = AssetLocator.Resolve(missing);

            Check.Equal<string?>(null, locator.Resolve(AssetPaths.N2nEdge), "未找到根目录时应返回 null");
        });

        suite.Test("AssetLocator:找到根目录时能拼出完整路径", () =>
        {
            var root = Path.Combine(Check.TempRoot, $"twice1200-join-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);

            try
            {
                File.WriteAllText(Path.Combine(root, "README.md"), "test");

                var locator = AssetLocator.Resolve(root);
                var expected = Path.Combine(Path.GetFullPath(root), AssetPaths.N2nEdge);

                Check.Equal(expected, locator.Resolve(AssetPaths.N2nEdge)!, "应拼出 n2n/edge.exe 的完整路径");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        });

        suite.Test("AssetPaths:约定路径使用反斜杠", () =>
        {
            // 这些字符串直接参与 Windows 路径拼接,保持反斜杠可避免混用分隔符带来的困扰
            Check.Contains(AssetPaths.N2nEdge, "\\", "路径应使用反斜杠");
            Check.True(AssetPaths.Frpc.EndsWith("frpc.exe", StringComparison.Ordinal), "应指向 frpc.exe");
            Check.True(AssetPaths.TapInstaller.EndsWith(".exe", StringComparison.Ordinal), "应指向驱动安装包");
        });
    }
}

/// <summary>运行环境相关的工具。</summary>
public static class RuntimeTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("AppPaths:路径不含未展开的环境变量", () =>
        {
            foreach (var path in new[]
                     {
                         AppPaths.ConfigurationDirectory,
                         AppPaths.LogDirectory,
                         AppPaths.RuntimeDirectory,
                         AppPaths.ConfigurationFile,
                         AppPaths.SecretsFile,
                     })
            {
                Check.True(!string.IsNullOrWhiteSpace(path), "路径不应为空");
                Check.DoesNotContain(path, "%", $"路径不应残留未展开的变量:{path}");
                Check.True(Path.IsPathFullyQualified(path), $"应为绝对路径:{path}");
            }
        });

        suite.Test("AppPaths:配置与凭据文件在同一个目录", () =>
        {
            Check.Equal(
                Path.GetDirectoryName(AppPaths.ConfigurationFile),
                Path.GetDirectoryName(AppPaths.SecretsFile),
                "配置文件与凭据库应同目录,便于整体备份与迁移");
        });

        suite.Test("ProcessSnapshotter:查询一个必然不存在的进程名", () =>
        {
            var result = ProcessSnapshotter.FindByName($"twice1200-definitely-not-running-{Guid.NewGuid():N}");
            Check.Equal(0, result.Count, "不应找到任何结果");
        });

        suite.Test("ProcessSnapshotter:空参数安全返回", () =>
        {
            Check.Equal(0, ProcessSnapshotter.FindByName().Count, "无参数应返回空集合");
            Check.Equal(0, ProcessSnapshotter.FindByName(null!).Count, "null 应被容忍");
        });

        suite.Test("ProcessSnapshotter:能枚举到当前进程", () =>
        {
            // 用当前进程名查询,验证枚举链路是通的
            var current = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            var found = ProcessSnapshotter.FindByName(current);

            Check.NotEmpty(found, $"应能枚举到当前进程「{current}」");
            Check.True(found.Any(p => p.Id == Environment.ProcessId), "结果中应包含本进程的 PID");
        });

        suite.Test("SystemCodePage:检测结果在合理范围或为 null", () =>
        {
            var codePage = SystemCodePage.Detect();

            if (codePage is not null)
            {
                Check.True(codePage > 0, "代码页应为正整数");
            }

            // 纯 ASCII 名称在任何代码页下都不应被判定为会损坏
            Check.False(
                SystemCodePage.WouldCorruptNonAsciiName("n2n-TAP", out _),
                "ASCII 名称不应被判定为会损坏");
        });

        suite.Test("NetworkInterfaceInspector:能枚举本机接口", () =>
        {
            var interfaces = NetworkInterfaceInspector.Enumerate();

            Check.NotEmpty(interfaces, "本机应至少有一个网络接口");
            Check.True(interfaces.All(i => !string.IsNullOrWhiteSpace(i.Name)), "每个接口都应有名称");
            Check.True(
                NetworkInterfaceInspector.FindTapLike().All(NetworkInterfaceInspector.IsTapLike),
                "FindTapLike 的结果都应满足 IsTapLike");
        });

        suite.Test("NetworkInterfaceInspector:TAP 结果里不含过滤驱动子接口", () =>
        {
            // 真实的 Windows 上,一块 TAP 网卡会派生出若干 NDIS 过滤器子接口
            // (名字形如 "以太网 2-WFP Native MAC Layer LightWeight Filter-0000")。
            // 它们不是独立网卡:与宿主网卡共用同一个 MAC,只是过滤器链上的节点。
            // 若混进结果会污染"网卡数量",并可能被误当作 -d 参数的目标。
            foreach (var nic in NetworkInterfaceInspector.FindTapLike())
            {
                Check.False(
                    NetworkInterfaceInspector.IsFilterInterface(nic),
                    $"TAP 列表不应包含过滤驱动子接口,但出现了「{nic.Name}」");
            }
        });

        suite.Test("NetworkInterfaceInspector:过滤子接口识别", () =>
        {
            var real = new NetworkInterfaceSnapshot(
                "id-1", "以太网 2", "TAP-Windows Adapter V9",
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                System.Net.NetworkInformation.OperationalStatus.Down,
                "00:FF:00:00:00:01", []);

            var filter = real with
            {
                Id = "id-2",
                Name = "以太网 2-WFP Native MAC Layer LightWeight Filter-0000",
                Description = "TAP-Windows Adapter V9-WFP Native MAC Layer LightWeight Filter-0000",
            };

            Check.True(NetworkInterfaceInspector.IsTapLike(real), "真实网卡应被识别");
            Check.False(NetworkInterfaceInspector.IsFilterInterface(real), "真实网卡不是过滤接口");
            Check.True(NetworkInterfaceInspector.IsFilterInterface(filter), "过滤器子接口应被识别");
            Check.False(NetworkInterfaceInspector.IsTapLike(filter), "过滤器子接口不应算作 TAP 网卡");
        });

        suite.Test("NetworkInterfaceInspector:光凭名字带 n2n 不算 TAP", () =>
        {
            // 这是之前踩过的误报:关键字 "n2n" 太宽泛,会把无关接口也算进来
            var suspicious = new NetworkInterfaceSnapshot(
                "id-x", "n2n-tunnel", "某种其它虚拟网卡",
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                System.Net.NetworkInformation.OperationalStatus.Up,
                null, []);

            Check.False(
                NetworkInterfaceInspector.IsTapLike(suspicious),
                "仅名字含 n2n 不应被判定为 TAP 网卡");
        });
    }
}
