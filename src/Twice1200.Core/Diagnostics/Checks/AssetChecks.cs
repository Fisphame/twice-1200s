using System.Runtime.InteropServices;
using System.Text;
using Twice1200.Core.Assets;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>检查资源根目录是否已定位。</summary>
public sealed class AssetsRootCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.AssetsRoot;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var locator = options.Assets;

        var details = new List<string>
        {
            $"解析说明 : {locator.Origin}",
            $"资源目录 : {locator.Root ?? "(未找到)"}",
            $"程序目录 : {AppContext.BaseDirectory}",
            $"工作目录 : {Directory.GetCurrentDirectory()}",
            $"环境变量 : {AssetLocator.EnvironmentVariable} = " +
            $"{Environment.GetEnvironmentVariable(AssetLocator.EnvironmentVariable) ?? "(未设置)"}",
        };

        if (locator.Found)
        {
            return Task.FromResult(CheckResult.Ok(
                CheckId.AssetsRoot,
                "资源目录",
                locator.Root!,
                details));
        }

        return Task.FromResult(CheckResult.Fail(
            CheckId.AssetsRoot,
            "资源目录",
            "未找到资源目录,无法定位 edge.exe、frpc.exe 与 TAP 驱动。",
            details,
            [
                $"在程序目录旁创建 {AssetLocator.AssetDirectoryName}/ 并放入所需文件(见该目录下的 README.md)。",
                "或用命令行参数 --assets <目录> 显式指定。",
                $"或设置环境变量 {AssetLocator.EnvironmentVariable}。",
            ]));
    }
}

/// <summary>对分发包内二进制做存在性、完整性与架构检查。</summary>
public sealed class BinaryAssetCheck : IEnvironmentCheck
{
    private readonly string _id;
    private readonly string _title;
    private readonly string _relativePath;
    private readonly bool _required;
    private readonly string _purpose;

    private BinaryAssetCheck(string id, string title, string relativePath, bool required, string purpose)
    {
        _id = id;
        _title = title;
        _relativePath = relativePath;
        _required = required;
        _purpose = purpose;
    }

    /// <inheritdoc />
    public string Id => _id;

    /// <summary>n2n 边缘节点。</summary>
    public static BinaryAssetCheck N2nEdge { get; } =
        new(CheckId.BinN2nEdge, "n2n edge.exe", AssetPaths.N2nEdge, required: true, "每台客户端都必须有它才能加入 n2n 网络");

    /// <summary>n2n 超级节点。</summary>
    public static BinaryAssetCheck N2nSupernode { get; } =
        new(CheckId.BinN2nSupernode, "n2n supernode.exe", AssetPaths.N2nSupernode, required: false, "只有自建服务器时才需要");

    /// <summary>frp 客户端。</summary>
    public static BinaryAssetCheck Frpc { get; } =
        new(CheckId.BinFrpc, "frp frpc.exe", AssetPaths.Frpc, required: true, "使用 frp 端口映射功能时必须要有");

    /// <summary>frp 服务端。</summary>
    public static BinaryAssetCheck Frps { get; } =
        new(CheckId.BinFrps, "frp frps.exe", AssetPaths.Frps, required: false, "只有自建服务器时才需要");

    /// <summary>TAP 驱动安装包。</summary>
    public static BinaryAssetCheck TapInstaller { get; } =
        new(CheckId.BinTapDriver, "TAP 驱动安装包", AssetPaths.TapInstaller, required: true, "安装虚拟网卡时使用");

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Assets.Found)
        {
            return CheckResult.Skip(_id, _title, "资源目录未找到,跳过检查。");
        }

        var path = options.Assets.Resolve(_relativePath);

        if (path is null || !File.Exists(path))
        {
            var hints = new[]
            {
                _required
                    ? $"该文件是必需的 —— {_purpose}。"
                    : $"该文件不是必需的({_purpose}),缺少时相关功能不可用,但不影响其它功能。",
            };

            return _required
                ? CheckResult.Fail(_id, _title, $"文件不存在:{_relativePath}", hints: hints)
                : CheckResult.Warn(_id, _title, $"文件不存在(可选):{_relativePath}", hints: hints);
        }

        var info = new FileInfo(path);
        var details = new List<string>
        {
            $"路径   : {path}",
            $"大小   : {FormatSize(info.Length)}",
            $"修改时间: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}",
        };

        if (info.Length < 1024)
        {
            return CheckResult.Fail(
                _id,
                _title,
                $"文件异常小({info.Length} 字节),可能下载不完整或被占位文件替换。",
                details);
        }

        var architecture = PeInspector.ReadArchitecture(path);
        details.Add($"架构   : {PeInspector.Describe(architecture)}");

        if (architecture == PeArchitecture.Unknown)
        {
            return CheckResult.Fail(
                _id,
                _title,
                "文件不是合法的 Windows 可执行文件(PE 头无法解析),可能已损坏。",
                details,
                ["请重新获取该文件,或核对完整性。"]);
        }

        var expected = RuntimeInformation.OSArchitecture;
        if (architecture == PeArchitecture.X86 && expected == Architecture.X64)
        {
            details.Add("说明   : 32 位程序可以在 64 位 Windows 上运行,但建议使用 64 位版本。");
        }
        else if (architecture == PeArchitecture.Arm64 && expected == Architecture.X64)
        {
            return CheckResult.Fail(
                _id,
                _title,
                "这是 ARM64 版本的可执行文件,无法在当前 x64 系统上运行。",
                details);
        }
        else if (architecture == PeArchitecture.X64 && expected != Architecture.X64)
        {
            return CheckResult.Fail(
                _id,
                _title,
                "这是 x64 版本的可执行文件,无法在当前系统架构上运行。",
                details);
        }

        string? hash = null;
        try
        {
            hash = await Hashing.Sha256FileAsync(path, cancellationToken).ConfigureAwait(false);
            details.Add($"SHA256 : {hash}");
        }
        catch (Exception ex)
        {
            details.Add($"SHA256 : 计算失败 — {ex.GetType().Name}");
        }

        return CheckResult.Ok(_id, _title, $"{FormatSize(info.Length)} · {PeInspector.Describe(architecture)}", details);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F2} MB",
    };
}

/// <summary>检查随包分发的二进制架构是否与当前系统一致(汇总视角)。</summary>
public sealed class BinaryArchitectureCheck : IEnvironmentCheck
{
    private static readonly (string Title, string RelativePath)[] Targets =
    [
        ("n2n edge.exe", AssetPaths.N2nEdge),
        ("frp frpc.exe", AssetPaths.Frpc),
        ("TAP 驱动安装包", AssetPaths.TapInstaller),
    ];

    /// <inheritdoc />
    public string Id => CheckId.BinArchitecture;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.Assets.Found)
        {
            return Task.FromResult(CheckResult.Skip(CheckId.BinArchitecture, "二进制架构", "资源目录未找到,跳过检查。"));
        }

        var system = RuntimeInformation.OSArchitecture;
        var details = new List<string> { $"当前系统架构 : {system}" };
        var present = 0;
        var mismatched = new List<string>();

        foreach (var (title, relativePath) in Targets)
        {
            var path = options.Assets.Resolve(relativePath);
            if (path is null || !File.Exists(path))
            {
                details.Add($"  · {title,-18} 缺失");
                continue;
            }

            present++;
            var architecture = PeInspector.ReadArchitecture(path);
            details.Add($"  · {title,-18} {PeInspector.Describe(architecture)}");

            // 32 位在 64 位系统上可运行(靠 WOW64),不算不匹配
            var compatible = architecture switch
            {
                PeArchitecture.X64 => system == Architecture.X64,
                PeArchitecture.X86 => system is Architecture.X64 or Architecture.X86,
                PeArchitecture.Arm64 => system == Architecture.Arm64,
                _ => false,
            };

            if (!compatible)
            {
                mismatched.Add(title);
            }
        }

        if (present == 0)
        {
            return Task.FromResult(CheckResult.Skip(
                CheckId.BinArchitecture,
                "二进制架构",
                "没有任何可检查的二进制文件。",
                details));
        }

        return Task.FromResult(mismatched.Count == 0
            ? CheckResult.Ok(CheckId.BinArchitecture, "二进制架构", $"{present} 个文件架构均与系统兼容。", details)
            : CheckResult.Fail(
                CheckId.BinArchitecture,
                "二进制架构",
                $"{mismatched.Count} 个文件与当前系统架构不兼容:{string.Join("、", mismatched)}",
                details,
                ["请获取与系统架构匹配的版本。"]));
    }
}
