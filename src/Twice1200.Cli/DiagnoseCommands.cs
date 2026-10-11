using System.Security.Cryptography.X509Certificates;
using System.Text;
using Twice1200.Core.Runtime;

namespace Twice1200.Cli;

/// <summary>
/// <c>diagnose</c> 子命令:定位"写不进去"这类问题的层次。
/// </summary>
/// <remarks>
/// <para>
/// 起因是一个反复查不出的故障:程序写 <c>%APPDATA%</c> 报 <c>Access Denied</c>
/// (HResult <c>0x80070005</c>),但同一用户的 PowerShell 写同一路径却成功,
/// 而且 ACL 显示该用户有 <c>FullControl</c>。
/// </para>
/// <para>
/// ACL 与观察结果矛盾,说明拦截来自 ACL <b>之外</b>的一层
/// (例如应用控制策略、安全软件)。那种拦截用 <c>takeown</c>/<c>icacls</c>
/// 是改不动的 —— 这正好解释了"修复脚本报告成功但错误依旧"。
/// </para>
/// <para>
/// 本命令的做法是<b>做对照实验而不是下结论</b>:让同一个未签名的进程
/// 分别往多个位置写,看哪些位置可以、哪些不可以。
/// 再配合"可执行文件是否签名"这一项,就能区分
/// 「按路径拦截」与「未签名程序一律拦截」这两种可能。
/// </para>
/// </remarks>
internal static class DiagnoseCommands
{
    /// <summary>处理 <c>diagnose</c> 子命令。</summary>
    public static int Run(ArgumentReader reader)
    {
        if (reader.Positional.Count > 1 &&
            reader.Positional[1].Equals("write", StringComparison.OrdinalIgnoreCase))
        {
            return RunWriteDiagnostics();
        }

        return RunOverview();
    }

    // ================================================================
    //  diagnose write —— 多位置写入对照
    // ================================================================

    private static int RunWriteDiagnostics()
    {
        Console.WriteLine("── 写入路径对照诊断 ──");
        Console.WriteLine();

        ReportProcessIdentity();

        Console.WriteLine();

        // 对照组:同一个未签名进程,往不同位置写
        var targets = new (string Label, string Directory)[]
        {
            ("配置目录(出故障的位置)", AppPaths.ConfigurationDirectory),
            ("运行期目录", AppPaths.RuntimeDirectory),
            ("日志目录", AppPaths.LogDirectory),
            ("系统临时目录", Path.GetTempPath()),
            ("程序所在目录", AppContext.BaseDirectory),
        };

        var configurationOk = false;
        var tempOk = false;
        var signatures = 0;

        foreach (var (label, directory) in targets)
        {
            var result = TryWriteProbe(directory, out var detail, out var signatureStatus);

            if (signatureStatus is not null)
            {
                signatures++;
            }

            var mark = result switch
            {
                WriteProbeResult.Success => "[√]",
                WriteProbeResult.Denied => "[×]",
                WriteProbeResult.OtherFailure => "[!]",
                _ => "[-]",
            };

            Console.WriteLine($"  {mark} {label}");
            Console.WriteLine($"      目录 : {directory}");
            Console.WriteLine($"      {detail}");

            if (label.StartsWith("配置目录", StringComparison.Ordinal) &&
                result == WriteProbeResult.Success)
            {
                configurationOk = true;
            }

            if (label == "系统临时目录" && result == WriteProbeResult.Success)
            {
                tempOk = true;
            }

            Console.WriteLine();
        }

        // ---------------------------------------------------------------- 结论
        Console.WriteLine("── 判断 ──");
        Console.WriteLine();

        if (configurationOk)
        {
            Console.WriteLine("  配置目录现在可以写入 —— 问题可能已经消失,或与运行时机有关。");
            Console.WriteLine("  请重试保存操作。");
            return 0;
        }

        Console.WriteLine("  配置目录写入被拒绝。");

        if (tempOk)
        {
            Console.WriteLine();
            Console.WriteLine("  但系统临时目录可以写入 —— 说明本进程并非完全没有写权限。");
            Console.WriteLine();
            Console.WriteLine("  → 这指向「按路径拦截」:某个策略或安全软件只拦配置目录。");
            Console.WriteLine("     ACL 显示你有 FullControl,却仍被拒绝,");
            Console.WriteLine("     说明拦截发生在 ACL 之外的一层。");
            Console.WriteLine();
            Console.WriteLine("     该层用 takeown / icacls 是改不动的 —— 这解释了");
            Console.WriteLine("     为什么修复脚本报告成功但错误依旧。");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine("  连系统临时目录也写不进去 —— 说明本进程整体缺少写权限。");
            Console.WriteLine();
            Console.WriteLine("  → 这指向「未签名程序被整体限制」,而不是某个路径的问题。");
        }

        Console.WriteLine();
        Console.WriteLine("── 请把以上全部输出反馈,特别是「本程序签名状态」一节 ──");

        return 1;
    }

    // ================================================================
    //  diagnose overview
    // ================================================================

    private static int RunOverview()
    {
        Console.WriteLine("── 诊断总览 ──");
        Console.WriteLine();
        Console.WriteLine("  可用子命令:");
        Console.WriteLine("      diagnose write     多位置写入对照(排查 Access Denied)");
        Console.WriteLine();
        Console.WriteLine("  相关的其它命令:");
        Console.WriteLine("      doctor             环境体检(21 项)");
        Console.WriteLine("      credential diagnose  凭据库与 DPAPI 自检");
        Console.WriteLine("      credential list      查看已保存的凭据");

        return 0;
    }

    // ================================================================
    //  内部
    // ================================================================

    private enum WriteProbeResult
    {
        Success,
        Denied,
        OtherFailure,
        Skipped,
    }

    /// <summary>往指定目录写一个临时文件,然后删掉。返回结果与说明文本。</summary>
    private static WriteProbeResult TryWriteProbe(
        string directory,
        out string detail,
        out string? signatureStatus)
    {
        signatureStatus = null;

        var probe = Path.Combine(directory, $"twice1200-write-probe-{Guid.NewGuid():N}.tmp");

        try
        {
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(probe, "probe", new UTF8Encoding(false));

            try
            {
                File.Delete(probe);
            }
            catch
            {
                // 删不掉不影响"能写"这个结论
            }

            detail = "新建文件: 成功";
            return WriteProbeResult.Success;
        }
        catch (UnauthorizedAccessException ex)
        {
            detail = $"新建文件: 被拒绝(HResult 0x{ex.HResult:X8})";
            return WriteProbeResult.Denied;
        }
        catch (Exception ex)
        {
            detail = $"新建文件: 失败 — {ex.GetType().Name}: {ex.Message}";
            return WriteProbeResult.OtherFailure;
        }
    }

    /// <summary>报告进程身份与可执行文件的签名状态。</summary>
    private static void ReportProcessIdentity()
    {
        Console.WriteLine("── 本程序身份 ──");

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            Console.WriteLine($"  用户       : {identity.Name}");

            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            var elevated = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Console.WriteLine($"  管理员     : {elevated}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  用户       : 读取失败 — {ex.GetType().Name}");
        }

        Console.WriteLine($"  进程架构   : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"  当前目录   : {Directory.GetCurrentDirectory()}");

        var exe = Environment.ProcessPath;
        Console.WriteLine($"  可执行文件 : {exe ?? "(未知)"}");

        // 令牌形状是判断的关键之一。
        //
        // 用户自己的 PowerShell 能写目标目录,而本程序不能,且 ACL 完全正常 ——
        // 这种情况只剩令牌层面的解释:完整性级别、受限令牌、或 AppContainer。
        // 这三者都会让 CreateFile 返回 Access Denied,而任何 ACL 查询都看不出来。
        Console.WriteLine();
        Console.WriteLine("── 令牌形状 ──");
        Console.WriteLine();
        Console.WriteLine(Twice1200.Core.Security.TokenInspector.Describe());

        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("── 本程序签名状态 ──");
        Console.WriteLine();
        Console.WriteLine("  这一节是判断的关键:如果 PowerShell 能写而本程序不能,");
        Console.WriteLine("  差别可能就在签名上 —— 应用控制策略通常只信任已签名的程序。");
        Console.WriteLine();

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(exe);
            var subject = certificate.Subject;
            var issuer = certificate.Issuer;

            Console.WriteLine($"  [已签名] 主体 : {subject}");
            Console.WriteLine($"           颁发者: {issuer}");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // 没有内嵌证书 —— 这是本地构建的正常情况
            Console.WriteLine("  [未签名] 该可执行文件没有嵌入代码签名证书。");
            Console.WriteLine();
            Console.WriteLine("  对照:PowerShell 的可执行文件由微软签名。");
            Console.WriteLine("  若某策略只信任已签名程序,就会出现");
            Console.WriteLine("  「PowerShell 能写、本程序不能写」的现象。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  签名状态读取失败: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
