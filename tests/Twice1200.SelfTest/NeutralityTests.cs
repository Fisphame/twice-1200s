using Twice1200.Core.Assets;

namespace Twice1200.SelfTest;

/// <summary>
/// 守卫:产品源码里不允许出现**具体部署**的地址。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这条测试:</b> 这个工具最初是为一次具体的部署做的,
/// 于是部署者的超级节点地址、frp 服务端地址、虚拟网段都被硬编码进了
/// 配置模型的默认值和界面的预填值里。
/// </para>
/// <para>
/// 后果不是"不好看",而是<b>功能上的错误</b>:别人拿到这个工具,
/// 一打开界面看到的就是陌生人的服务器,而且很可能直接点「连接」。
/// 虚拟网段写死还会让新用户与部署者的网段冲突。
/// </para>
/// <para>
/// 已经清理过一次。这条测试的作用是<b>防止它们悄悄跑回来</b> ——
/// 靠人记住"不要写死地址"是不可靠的,尤其是当开发者的机器上
/// 恰好就是那套配置的时候。
/// </para>
/// <para>
/// <b>允许的例外:</b> 注释与文档里可以出现这些值(用于说明历史背景);
/// 拦截名单(如公开示例口令的清单)也可以出现。因此这里检查的是
/// <b>非注释行</b>,而注释行会被跳过。
/// </para>
/// </remarks>
public static class NeutralityTests
{
    /// <summary>不允许出现在代码(非注释)里的具体部署值。</summary>
    private static readonly string[] ForbiddenValues =
    [
        "1200s.top",        // 部署者的超级节点域名
        "39.105.139.45",    // 部署者的服务器 IP
        "10.236.167",       // 部署者的虚拟网段
    ];

    /// <summary>注册用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("中性化:源码里不出现具体部署的地址", () =>
        {
            var root = FindProjectRoot();

            if (root is null)
            {
                Check.True(true, "找不到项目根目录,跳过(环境限制)");
                return;
            }

            var offenders = new List<string>();
            var scanned = 0;

            foreach (var file in EnumerateSourceFiles(root))
            {
                scanned++;

                var lines = File.ReadAllLines(file);
                var inBlockComment = false;

                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();

                    // ---- 维护块注释状态 ----
                    if (inBlockComment)
                    {
                        if (trimmed.Contains("-->", StringComparison.Ordinal) ||
                            trimmed.Contains("*/", StringComparison.Ordinal))
                        {
                            inBlockComment = false;
                        }

                        continue;
                    }

                    if (trimmed.StartsWith("///", StringComparison.Ordinal) ||
                        trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("*", StringComparison.Ordinal) ||
                        trimmed.StartsWith("<!--", StringComparison.Ordinal))
                    {
                        // 行注释;若块注释在此行开启且未闭合,标记状态
                        if (trimmed.StartsWith("<!--", StringComparison.Ordinal) &&
                            !trimmed.Contains("-->", StringComparison.Ordinal))
                        {
                            inBlockComment = true;
                        }

                        continue;
                    }

                    foreach (var forbidden in ForbiddenValues)
                    {
                        if (line.Contains(forbidden, StringComparison.Ordinal))
                        {
                            offenders.Add(
                                $"{Relative(root, file)}:{i + 1} 含「{forbidden}」 → {trimmed}");
                        }
                    }
                }
            }

            Check.True(scanned > 0, "应当扫描到源码文件");

            Check.True(
                offenders.Count == 0,
                "以下位置出现了具体部署的地址,应改为中性示例或从配置读取:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, offenders));
        });

        suite.Test("中性化:默认配置不指向任何真实主机", () =>
        {
            // 默认值必须是"保证解析失败"或明确的示例域名,
            // 这样即使用户直接点连接,也不会连到别人的服务器上。
            var configuration = Twice1200.Core.Configuration.AppConfiguration.CreateTemplate();

            var supernode = configuration.N2n.Supernode;
            var frpServer = configuration.Frp.ServerAddress;

            foreach (var value in new[] { supernode, frpServer })
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue; // 留空也是可接受的
                }

                var isPlaceholder =
                    value.Contains(".invalid", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains(".example.com", StringComparison.OrdinalIgnoreCase);

                Check.True(
                    isPlaceholder,
                    $"默认地址「{value}」既不是空值,也不是保留的占位域名 —— " +
                    "它可能会把用户连到一个真实但陌生的服务器上");
            }
        });

        suite.Test("中性化:默认虚拟网段不与常见家用网段冲突", () =>
        {
            // 默认网段如果落在 192.168.x / 172.16-31.x,用户几乎必然与自己的
            // 局域网冲突。10.42.0.0/24 属于 RFC 6598 之外的私网空间,
            // 而且不常被家用路由器使用。
            var configuration = Twice1200.Core.Configuration.AppConfiguration.CreateTemplate();
            var network = configuration.N2n.VirtualNetwork;

            if (string.IsNullOrWhiteSpace(network))
            {
                return;
            }

            Check.False(
                network.StartsWith("192.168.", StringComparison.Ordinal),
                "默认网段不应使用 192.168.x —— 与绝大多数家用路由器冲突");

            Check.False(
                network.StartsWith("172.16.", StringComparison.Ordinal) ||
                network.StartsWith("172.17.", StringComparison.Ordinal) ||
                network.StartsWith("172.18.", StringComparison.Ordinal) ||
                network.StartsWith("172.19.", StringComparison.Ordinal),
                "默认网段不应使用 172.16-19.x —— 常见于企业网络");

            Check.True(
                network.StartsWith("10.", StringComparison.Ordinal),
                $"默认网段「{network}」应落在 10.0.0.0/8 私网空间内");
        });
    }

    // ================================================================

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        // 【只检查 src/】—— 刻意不检查 tests/。
        //
        // 理由:中性化规则要防的是"产品里预填了部署者的地址,用户拿到就误连",
        // 这是**分发出去的东西**才有的问题。
        // 而测试里的地址是夹具:
        //   · EdgeDiagnosticsTests 里的是 edge 的**真实日志片段**,必须保持原样
        //   · NetworkTests 里的是任意能解析的 CIDR,换成别的值毫无意义
        // 把它们也算违例,只会制造噪音并让人想关掉这条测试。
        var basePath = Path.Combine(root, "src");

        if (Directory.Exists(basePath))
        {
            foreach (var file in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);

                if (extension is not (".cs" or ".xaml"))
                {
                    continue;
                }

                // 构建产物与生成的代码不归我们管
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Relative(string root, string file)
        => file.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? file[(root.Length + 1)..]
            : file;

    /// <summary>从自检程序所在目录向上找项目根(含 <c>src</c> 与 <c>tests</c> 的那一层)。</summary>
    private static string? FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src")) &&
                Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
