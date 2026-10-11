namespace Twice1200.Core.Runtime;

/// <summary>
/// 应用程序的数据目录约定。
/// </summary>
public static class AppPaths
{
    /// <summary>产品名,同时也是目录名。</summary>
    public const string ProductName = "Twice1200";

    /// <summary>
    /// 单独覆盖运行期临时目录的环境变量名。
    /// <para>
    /// 通常不需要它 —— 运行期目录默认跟在数据根目录下。
    /// 它的用途是把生成的 frpc 配置(含明文 token)单独放到一个确定可控的位置。
    /// </para>
    /// </summary>
    public const string RuntimeDirectoryVariable = "TWICE1200_RUNTIME";

    /// <summary>
    /// 覆盖整个数据根目录的环境变量名。
    /// <para>
    /// 设置后,配置目录与凭据库都会落到 <c>&lt;根目录&gt;\Twice1200</c> 之下。
    /// </para>
    /// </summary>
    public const string DataRootVariable = "TWICE1200_DATA_ROOT";

    private static string? _dataRootOverride;

    /// <summary>
    /// 覆盖数据根目录。
    /// <para>
    /// <b>为什么需要它:</b> 实测确认本机存在一条应用级写入限制 ——
    /// 允许写项目树,拦住项目树之外的写入(详见开发蓝本第八节)。
    /// 因此需要能把数据落到一个确实可写的位置。
    /// </para>
    /// <para>
    /// <b>必须在读取任何路径之前调用</b>(例如 App 启动时解析命令行参数之后)。
    /// 之所以做成可设置的静态值而不是构造参数,是因为路径被 Core 里多处引用,
    /// 传参会污染一大片签名。
    /// </para>
    /// </summary>
    /// <param name="rootDirectory">新的数据根目录;为 <c>null</c> 或空表示恢复默认。</param>
    public static void OverrideDataRoot(string? rootDirectory)
    {
        _dataRootOverride = string.IsNullOrWhiteSpace(rootDirectory)
            ? null
            : Path.GetFullPath(rootDirectory);
    }

    /// <summary>解析出实际使用的数据根目录。</summary>
    private static string ResolveDataRoot()
    {
        if (_dataRootOverride is not null)
        {
            return _dataRootOverride;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataRootVariable);

        return string.IsNullOrWhiteSpace(fromEnvironment)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.GetFullPath(fromEnvironment);
    }

    /// <summary>
    /// 配置目录。存放 <c>config.json</c> 与凭据库。
    /// <para>
    /// 默认是漫游的 <c>%APPDATA%\Twice1200</c> —— 它与 DPAPI 的"当前用户"作用域一致,
    /// 而密钥密文本身换台机器就解不开,所以放漫游目录不会有额外的泄露风险。
    /// </para>
    /// <para>
    /// 可通过 <see cref="OverrideDataRoot"/> 或
    /// <see cref="DataRootVariable"/> 改到别处(见开发蓝本第八节的写入限制)。
    /// </para>
    /// </summary>
    public static string ConfigurationDirectory => Path.Combine(ResolveDataRoot(), ProductName);

    /// <summary>日志目录。默认在 <c>%LOCALAPPDATA%</c> 下。</summary>
    public static string LogDirectory => Path.Combine(ResolveDataRoot(), ProductName, "logs");

    /// <summary>
    /// 运行期临时文件目录。生成的 frpc 配置放在这里,用完即删。
    /// <para>默认在数据根目录下;可用 <see cref="RuntimeDirectoryVariable"/> 单独覆盖。</para>
    /// </summary>
    public static string RuntimeDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable(RuntimeDirectoryVariable);

            return string.IsNullOrWhiteSpace(overridden)
                ? Path.Combine(ResolveDataRoot(), ProductName, "run")
                : Path.GetFullPath(overridden);
        }
    }

    /// <summary>主配置文件的完整路径。</summary>
    public static string ConfigurationFile => Path.Combine(ConfigurationDirectory, "config.json");

    /// <summary>凭据库文件的完整路径。</summary>
    public static string SecretsFile => Path.Combine(ConfigurationDirectory, "secrets.dat");

    /// <summary>确保配置目录存在。</summary>
    public static void EnsureConfigurationDirectory() => Directory.CreateDirectory(ConfigurationDirectory);

    /// <summary>
    /// 检查配置目录当前是否可写(实际写一个探针文件)。
    /// </summary>
    /// <remarks>
    /// <b>为什么要探测而不是查 ACL:</b> 实测确认存在一种情况 ——
    /// ACL 显示当前用户有 <c>FullControl</c>,但写入仍被拒绝,
    /// 因为拦截发生在 ACL <b>之外</b>的一层。查 ACL 会得出错误结论,
    /// 只有真的写一次才知道行不行。
    /// </remarks>
    public static bool IsConfigurationDirectoryWritable()
    {
        var probe = Path.Combine(ConfigurationDirectory, $"write-probe-{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(ConfigurationDirectory);
            File.WriteAllText(probe, "probe", new System.Text.UTF8Encoding(false));

            try
            {
                File.Delete(probe);
            }
            catch
            {
                // 删不掉不影响"能写"这个结论
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 生成"默认位置不可写"时给用户看的说明。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>刻意不自动回退。</b> 早先实现过自动回退(找一个可写位置就切过去),
    /// 实测证明那是有害的:它把配置与凭据悄悄写到了
    /// <c>bin\Debug\...\Twice1200</c> 这类构建产物目录里,
    /// 而这些目录会被 <c>clean</c> / 重新构建删除 ——
    /// <b>用户的密钥与配置就这样无声消失了。</b>
    /// </para>
    /// <para>
    /// 而且它保护的场景并不真实:用户自己启动程序时,
    /// <c>%APPDATA%</c> 一直是可写的;只有受限环境下的子进程才会被拦。
    /// </para>
    /// <para>
    /// 现在的策略是<b>明确告知 + 让用户显式选择位置</b>:
    /// 数据的去向必须由用户决定,不能由程序猜。
    /// </para>
    /// </remarks>
    public static string DescribeUnwritableDataDirectory()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("无法写入数据目录:");
        sb.AppendLine($"    {ConfigurationDirectory}");
        sb.AppendLine();
        sb.AppendLine("  配置与密钥需要存放在这里,因此现在无法保存。");
        sb.AppendLine();
        sb.AppendLine("  可以用下面任一方式指定一个可写的位置:");
        sb.AppendLine();
        sb.AppendLine("      命令行参数 :  Twice1200.exe --data-root <目录>");
        sb.AppendLine($"      环境变量   :  set {DataRootVariable}=<目录>");
        sb.AppendLine();
        sb.AppendLine("  指定的目录下会自动建立 Twice1200 子目录存放数据。");

        return sb.ToString();
    }

    /// <summary>确保运行期目录存在。</summary>
    public static void EnsureRuntimeDirectory() => Directory.CreateDirectory(RuntimeDirectory);

    /// <summary>确保日志目录存在。</summary>
    public static void EnsureLogDirectory() => Directory.CreateDirectory(LogDirectory);
}
