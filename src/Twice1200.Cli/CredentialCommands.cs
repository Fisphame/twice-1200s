using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics;
using Twice1200.Core.Runtime;
using Twice1200.Core.Security;

namespace Twice1200.Cli;

/// <summary>
/// <c>credential</c> 子命令:管理本机凭据库。
/// <para>
/// <b>这个命令的每一条输出都经过审视:绝不回显密钥内容。</b>
/// 只显示键名、长度与指纹 —— 指纹用于让用户确认"两台机器上填的是同一个密码",
/// 而它本身无法反推出密码。
/// </para>
/// </summary>
internal static class CredentialCommands
{
    /// <summary>处理 <c>credential</c> 子命令。</summary>
    public static async Task<int> RunAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var action = reader.Positional.Count > 1 ? reader.Positional[1].ToLowerInvariant() : "list";

        return action switch
        {
            "list" or "ls" => RunList(),
            "set" or "add" => await RunSetAsync(reader, cancellationToken).ConfigureAwait(false),
            "delete" or "remove" or "rm" => RunDelete(reader),
            "show" or "get" => RunShow(reader),
            "clear" => RunClear(reader),
            "diagnose" or "check" => RunDiagnose(),
            _ => throw new CliUsageException(
                $"未知的 credential 子命令「{action}」。可用:list / set / delete / show / clear / diagnose"),
        };
    }

    // ================================================================
    //  list
    // ================================================================

    private static int RunList()
    {
        var store = new CredentialStore();

        Console.WriteLine("── 凭据库 ──");
        Console.WriteLine($"  文件 : {store.Path}");
        Console.WriteLine($"  状态 : {(store.Exists ? "已创建" : "尚未创建")}");
        Console.WriteLine($"  加密 : DPAPI(绑定当前 Windows 用户与当前机器)");
        Console.WriteLine();

        var handles = store.ListHandles();

        if (handles.Count == 0)
        {
            Console.WriteLine("  尚无已保存的凭据。");
            Console.WriteLine();
            Console.WriteLine("  添加一个:");
            Console.WriteLine($"      {SelfName} credential set {CredentialHandles.N2nKey}");
            return 0;
        }

        Console.WriteLine($"  已保存 {handles.Count} 项:");
        Console.WriteLine();

        foreach (var handle in handles)
        {
            var purpose = CredentialHandles.Describe(handle);

            // 只报告"能不能解开",不回显内容
            var readable = store.TryGetSecret(handle, out var secret, out _);

            var state = readable
                ? $"可解密(长度 {secret!.Length},指纹 {Redaction.Fingerprint(secret)})"
                : "密文无法解密 —— 可能来自另一台机器或另一个用户";

            Console.WriteLine($"  「{handle}」");
            Console.WriteLine($"      用途 : {purpose}");
            Console.WriteLine($"      状态 : {state}");
            Console.WriteLine();
        }

        Console.WriteLine("  说明:指纹是密码的单向摘要,用于核对两台机器填的是否一致,无法反推密码。");

        return 0;
    }

    // ================================================================
    //  set
    // ================================================================

    private static async Task<int> RunSetAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var handle = reader.Positional.Count > 2
            ? reader.Positional[2]
            : CredentialHandles.N2nKey;

        Console.WriteLine($"── 保存凭据「{handle}」──");
        Console.WriteLine($"  用途 : {CredentialHandles.Describe(handle)}");

        // 可选的存储位置覆盖:用于把凭据库放到当前环境确实可写的位置。
        // 这是排查"写不进去"时的实用手段,也让受限环境仍能使用本工具。
        var storeOverride = reader.GetString("store");

        var store = string.IsNullOrWhiteSpace(storeOverride)
            ? new CredentialStore()
            : new CredentialStore(storeOverride);

        Console.WriteLine($"  文件 : {store.Path}");

        if (!string.IsNullOrWhiteSpace(storeOverride))
        {
            Console.WriteLine("        (已用 --store 覆盖默认位置)");
        }

        Console.WriteLine();

        string? secret;

        // 从标准输入读取允许脚本化使用;否则用掩码交互输入
        if (reader.GetFlag("stdin"))
        {
            secret = await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(secret))
            {
                Console.Error.WriteLine("[错误] 标准输入为空。");
                return 64;
            }
        }
        else
        {
            secret = PromptMasked("请输入密钥(输入时不显示): ");

            if (string.IsNullOrEmpty(secret))
            {
                Console.Error.WriteLine("[错误] 未输入内容。");
                return 64;
            }

            var confirm = PromptMasked("请再输入一次以确认: ");
            if (!string.Equals(secret, confirm, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("[错误] 两次输入不一致。");
                return 64;
            }
        }

        Console.WriteLine();

        // 强度检查:只报告问题,不显示内容
        var weakness = ConfigurationValidator.DescribeWeakness(secret);
        if (weakness is not null)
        {
            Console.WriteLine($"[!] 强度提示:{weakness}");
            Console.WriteLine();

            if (!reader.GetFlag("force"))
            {
                Console.Write("仍要保存吗?(y/N) ");
                var answer = Console.ReadLine();
                if (answer is null || !answer.Trim().StartsWith('y'))
                {
                    Console.WriteLine("已取消,凭据库未改动。");
                    return 0;
                }
            }
        }

        var result = store.SetSecret(handle, secret);

        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"[错误] {result.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  如果确认是当前环境不允许写入该位置,可以把凭据库指到别处:");
            Console.Error.WriteLine($"      {SelfName} credential set {handle} --store <可写目录>\\secrets.dat");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  排查用:");
            Console.Error.WriteLine($"      {SelfName} diagnose write     对照多个位置的写入结果");
            return 1;
        }

        Console.WriteLine($"[+] {result.Message}");
        Console.WriteLine($"    指纹:{Redaction.Fingerprint(secret)}(可用于核对两台机器是否填了同一个)");
        Console.WriteLine();

        if (string.Equals(handle, CredentialHandles.N2nKey, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  下一步:如果 config.json 里的 n2n.community 还没填,请一并设置,");
            Console.WriteLine($"          然后运行 {SelfName} n2n up。");
        }

        return 0;
    }

    // ================================================================
    //  delete / clear
    // ================================================================

    private static int RunDelete(ArgumentReader reader)
    {
        var handle = reader.Positional.Count > 2 ? reader.Positional[2] : null;

        if (string.IsNullOrWhiteSpace(handle))
        {
            Console.Error.WriteLine("[错误] 请指定要删除的键名。");
            Console.Error.WriteLine($"       例如:{SelfName} credential delete {CredentialHandles.N2nKey}");
            return 64;
        }

        var store = new CredentialStore();

        if (!store.HasSecret(handle))
        {
            Console.Error.WriteLine($"[错误] 凭据库里没有键「{handle}」。");
            return 1;
        }

        if (!reader.GetFlag("force"))
        {
            Console.Write($"确认删除「{handle}」?(y/N) ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().StartsWith('y'))
            {
                Console.WriteLine("已取消。");
                return 0;
            }
        }

        var result = store.DeleteSecret(handle);
        Console.WriteLine(result.IsSuccess ? $"[+] {result.Message}" : $"[错误] {result.Message}");

        return result.IsSuccess ? 0 : 1;
    }

    private static int RunClear(ArgumentReader reader)
    {
        var store = new CredentialStore();
        var handles = store.ListHandles();

        if (handles.Count == 0)
        {
            Console.WriteLine("凭据库已经是空的。");
            return 0;
        }

        Console.WriteLine($"即将删除全部 {handles.Count} 项凭据:");
        foreach (var handle in handles)
        {
            Console.WriteLine($"  「{handle}」");
        }

        if (!reader.GetFlag("force"))
        {
            Console.Write("确认?(y/N) ");
            var answer = Console.ReadLine();
            if (answer is null || !answer.Trim().StartsWith('y'))
            {
                Console.WriteLine("已取消。");
                return 0;
            }
        }

        var result = store.Clear();
        Console.WriteLine(result.IsSuccess ? $"[+] {result.Message}" : $"[错误] {result.Message}");

        return result.IsSuccess ? 0 : 1;
    }

    // ================================================================
    //  show —— 只显示形状,永不显示内容
    // ================================================================

    private static int RunShow(ArgumentReader reader)
    {
        var handle = reader.Positional.Count > 2 ? reader.Positional[2] : CredentialHandles.N2nKey;
        var store = new CredentialStore();

        Console.WriteLine($"── 凭据「{handle}」──");
        Console.WriteLine();

        if (!store.TryGetSecret(handle, out var secret, out var result))
        {
            Console.Error.WriteLine($"  不可用:{result.Message}");
            return 1;
        }

        // 刻意只输出这些:长度与指纹足以让用户确认"是不是它",而无法还原内容。
        Console.WriteLine($"  已设置   : 是");
        Console.WriteLine($"  长度     : {secret!.Length}");
        Console.WriteLine($"  指纹     : {Redaction.Fingerprint(secret)}");
        Console.WriteLine();
        Console.WriteLine("  出于安全考虑,这里不显示密钥内容。");
        Console.WriteLine("  指纹可用于核对两台机器上填的是否为同一个密码。");

        return 0;
    }

    // ================================================================
    //  diagnose —— 自检凭据库是否工作
    // ================================================================

    private static int RunDiagnose()
    {
        Console.WriteLine("── 凭据库自检 ──");
        Console.WriteLine();

        Console.WriteLine($"  DPAPI 可用   : {(DpapiSecretProtector.IsSupported ? "是" : "否")}");

        var store = new CredentialStore();
        Console.WriteLine($"  凭据文件     : {store.Path}");
        Console.WriteLine($"  文件存在     : {(store.Exists ? "是" : "否")}");

        if (!DpapiSecretProtector.IsSupported)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("[错误] 当前平台不支持 DPAPI,凭据库无法工作。");
            return 1;
        }

        // 真正做一次加解密往返 —— 只报告结果,不落盘
        const string probe = "Twice1200-DPAPI-SelfTest-Probe";
        try
        {
            var cipher = DpapiSecretProtector.Protect(probe);
            var plain = DpapiSecretProtector.UnprotectToString(cipher);

            var roundTripOk = string.Equals(plain, probe, StringComparison.Ordinal);

            Console.WriteLine($"  加解密往返   : {(roundTripOk ? "通过" : "失败 ← 解出的内容与原文不符")}");
            Console.WriteLine($"  密文长度     : {cipher.Length} 字节(明文 {probe.Length} 字节)");
            Console.WriteLine($"  密文含明文   : {(ContainsBytes(cipher, System.Text.Encoding.UTF8.GetBytes(probe)) ? "是 ← 加密没有生效!" : "否")}");

            if (!roundTripOk)
            {
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  加解密往返   : 失败 — {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("[+] 凭据库工作正常。密钥以 DPAPI 加密存储,不落明文。");
        Console.WriteLine();
        Console.WriteLine("  请注意 DPAPI 的固有边界:");
        Console.WriteLine("    · 不防以同一 Windows 用户身份运行的其它进程");
        Console.WriteLine("    · 不能跨机器共享 —— 换电脑需要重新输入");

        return 0;
    }

    /// <summary>检查一段字节里是否包含另一段字节。用于确认密文里没有明文残留。</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 读取一行掩码输入。
    /// <para>
    /// 输入过程中不回显字符,因此密码不会留在屏幕上,也不会进入控制台历史。
    /// 不支持掩码时(例如重定向输入)退化为普通读取并明确提示。
    /// </para>
    /// </summary>
    private static string? PromptMasked(string message)
    {
        Console.Write(message);

        if (Console.IsInputRedirected)
        {
            Console.WriteLine("(输入被重定向,无法掩码)");
            return Console.ReadLine();
        }

        var buffer = new System.Text.StringBuilder();

        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        Console.WriteLine();
                        return buffer.ToString();

                    case ConsoleKey.Backspace:
                        if (buffer.Length > 0)
                        {
                            buffer.Length--;
                            Console.Write("\b \b");
                        }

                        break;

                    case ConsoleKey.Escape:
                        Console.WriteLine();
                        return null;

                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            buffer.Append(key.KeyChar);
                            Console.Write('*');
                        }

                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // 没有可用的控制台(例如从服务里调用)
            Console.WriteLine("(无法读取键盘输入)");
            return null;
        }
    }

    /// <summary>本程序在命令行里的名字,用于生成可复制的后续命令。</summary>
    private static string SelfName
    {
        get
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path) ? "twice1200" : System.IO.Path.GetFileName(path);
        }
    }
}
