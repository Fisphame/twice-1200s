namespace Twice1200.Core.Assets;

/// <summary>各外部资源在资源根目录下的相对路径约定。</summary>
public static class AssetPaths
{
    /// <summary>n2n 边缘节点。</summary>
    public const string N2nEdge = @"n2n\edge.exe";

    /// <summary>n2n 超级节点。</summary>
    public const string N2nSupernode = @"n2n\supernode.exe";

    /// <summary>TAP 虚拟网卡驱动安装包。</summary>
    public const string TapInstaller = @"tap\tap-windows-9.21.2.exe";

    /// <summary>frp 客户端。</summary>
    public const string Frpc = @"frp\frpc.exe";

    /// <summary>frp 服务端。</summary>
    public const string Frps = @"frp\frps.exe";
}

/// <summary>
/// 资源定位。
/// <para>
/// 查找顺序:显式指定 → 环境变量 → 从程序目录向上逐级查找 → 当前工作目录。
/// 之所以要"向上查找",是为了让开发期(跑在 <c>src/Twice1200.Cli/bin/...</c> 下)
/// 和分发后(跑在安装目录下)都能找到同一份 <c>assets/</c>。
/// </para>
/// </summary>
public sealed class AssetLocator
{
    /// <summary>用于覆盖资源根目录的环境变量名。</summary>
    public const string EnvironmentVariable = "TWICE1200_ASSETS";

    /// <summary>资源目录名。</summary>
    public const string AssetDirectoryName = "assets";

    /// <summary>识别资源根目录的特征文件,任一存在即认为目录有效。</summary>
    private static readonly string[] Sentinels =
    [
        AssetPaths.N2nEdge,
        AssetPaths.Frpc,
        "README.md",
    ];

    /// <summary>已解析到的资源根目录;未找到时为 <c>null</c>。</summary>
    public string? Root { get; }

    /// <summary>触发本次解析的说明,用于在报告里解释"为什么是这个目录"。</summary>
    public string Origin { get; }

    private AssetLocator(string? root, string origin)
    {
        Root = root;
        Origin = origin;
    }

    /// <summary>解析资源根目录。</summary>
    /// <param name="explicitRoot">命令行显式指定的目录,优先级最高。</param>
    /// <param name="startDirectory">向上查找的起点,默认取当前程序所在目录。</param>
    public static AssetLocator Resolve(string? explicitRoot = null, string? startDirectory = null)
    {
        // 1. 命令行显式指定
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var full = Path.GetFullPath(explicitRoot);
            return Directory.Exists(full)
                ? new AssetLocator(full, "由命令行参数 --assets 指定")
                : new AssetLocator(null, $"命令行参数 --assets 指向的目录不存在: {full}");
        }

        // 2. 环境变量
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            var full = Path.GetFullPath(fromEnv);
            return Directory.Exists(full)
                ? new AssetLocator(full, $"由环境变量 {EnvironmentVariable} 指定")
                : new AssetLocator(null, $"环境变量 {EnvironmentVariable} 指向的目录不存在: {full}");
        }

        var start = string.IsNullOrWhiteSpace(startDirectory)
            ? AppContext.BaseDirectory
            : startDirectory;

        // 3. 从起点向上逐级查找 assets/
        var directory = new DirectoryInfo(Path.GetFullPath(start));
        var depth = 0;

        while (directory is not null && depth < 12)
        {
            var candidate = Path.Combine(directory.FullName, AssetDirectoryName);
            if (IsAssetRoot(candidate))
            {
                return new AssetLocator(candidate, $"在 {directory.FullName} 下找到 {AssetDirectoryName}/");
            }

            // 起点本身可能就是 assets/
            if (string.Equals(directory.Name, AssetDirectoryName, StringComparison.OrdinalIgnoreCase) &&
                IsAssetRoot(directory.FullName))
            {
                return new AssetLocator(directory.FullName, "起点目录本身就是资源根目录");
            }

            directory = directory.Parent;
            depth++;
        }

        // 4. 当前工作目录
        var fromCwd = Path.Combine(Directory.GetCurrentDirectory(), AssetDirectoryName);
        if (IsAssetRoot(fromCwd))
        {
            return new AssetLocator(fromCwd, "在当前工作目录下找到 assets/");
        }

        return new AssetLocator(null, "未找到资源根目录");
    }

    /// <summary>资源根目录是否已解析成功。</summary>
    public bool Found => Root is not null;

    /// <summary>
    /// 把相对路径解析为绝对路径。
    /// <para>未找到资源根目录时返回 <c>null</c>,调用方据此给出清晰的错误提示。</para>
    /// </summary>
    public string? Resolve(string relativePath)
        => Root is null ? null : Path.Combine(Root, relativePath);

    /// <summary>判断一个目录是否像资源根目录。</summary>
    private static bool IsAssetRoot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        foreach (var sentinel in Sentinels)
        {
            if (File.Exists(Path.Combine(directory, sentinel)))
            {
                return true;
            }
        }

        return false;
    }
}
