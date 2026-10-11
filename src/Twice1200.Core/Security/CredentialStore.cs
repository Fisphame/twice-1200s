using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twice1200.Core.Security;

/// <summary>凭据库操作的失败原因。</summary>
public enum CredentialError
{
    /// <summary>没有错误。</summary>
    None,

    /// <summary>指定的键不存在。</summary>
    NotFound,

    /// <summary>凭据文件不存在。</summary>
    StoreMissing,

    /// <summary>凭据文件无法解析。</summary>
    StoreCorrupted,

    /// <summary>密文无法解密(换了机器或用户)。</summary>
    DecryptionFailed,

    /// <summary>读写磁盘失败。</summary>
    IoFailure,

    /// <summary>当前平台不支持。</summary>
    NotSupported,

    /// <summary>参数不合法。</summary>
    InvalidArgument,
}

/// <summary>凭据库操作的结果。</summary>
/// <param name="Error">失败原因。</param>
/// <param name="Message">人可读说明。</param>
public sealed record CredentialResult(CredentialError Error, string Message)
{
    /// <summary>是否成功。</summary>
    public bool IsSuccess => Error == CredentialError.None;

    /// <summary>成功结果。</summary>
    public static CredentialResult Ok(string message = "操作成功。") => new(CredentialError.None, message);

    /// <summary>失败结果。</summary>
    public static CredentialResult Fail(CredentialError error, string message) => new(error, message);
}

/// <summary>
/// 本机凭据库。
/// <para>
/// <b>设计目标:让"密钥落明文"这件事在结构上不可能发生。</b>
/// 因此:
/// </para>
/// <list type="bullet">
///   <item><description>
///     所有值都用 DPAPI 加密后才写盘;
///   </description></item>
///   <item><description>
///     明文<b>不作为属性暴露</b> —— 想拿到密钥必须显式调用
///     <see cref="TryGetSecret"/>,这样"读凭据"在代码里是一个看得见的动作;
///   </description></item>
///   <item><description>
///     日志与诊断信息里只出现键名与"是否已设置",永远不出现值。
///   </description></item>
/// </list>
/// <para>
/// 文件位置是 <c>%APPDATA%\Twice1200\secrets.dat</c>,与 <c>config.json</c> 同目录,
/// 便于整体备份与迁移(但密文换机器就解不开,这一点在文档里说明)。
/// </para>
/// </summary>
public sealed class CredentialStore
{
    /// <summary>文件格式版本。将来若更换加密方案,靠它做迁移。</summary>
    private const int CurrentVersion = 1;

    private readonly string _path;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>用默认路径构造。</summary>
    public CredentialStore() : this(Runtime.AppPaths.SecretsFile)
    {
    }

    /// <summary>用指定路径构造。用于测试。</summary>
    public CredentialStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>凭据库文件的完整路径。</summary>
    public string Path => _path;

    /// <summary>凭据库文件当前是否存在。</summary>
    public bool Exists => File.Exists(_path);

    // ================================================================
    //  读
    // ================================================================

    /// <summary>
    /// 取出一个密钥。
    /// <para>
    /// 需要密钥时必须显式调用这个方法 —— 凭据库不提供任何"自动附带密钥"的入口,
    /// 以免密钥在不经意间被带到不该去的地方。
    /// </para>
    /// </summary>
    public bool TryGetSecret(string handle, out string? secret, out CredentialResult result)
    {
        secret = null;

        if (string.IsNullOrWhiteSpace(handle))
        {
            result = CredentialResult.Fail(CredentialError.InvalidArgument, "键名不能为空。");
            return false;
        }

        if (!DpapiSecretProtector.IsSupported)
        {
            result = CredentialResult.Fail(CredentialError.NotSupported, "当前平台不支持 DPAPI。");
            return false;
        }

        if (!File.Exists(_path))
        {
            result = CredentialResult.Fail(
                CredentialError.StoreMissing,
                $"凭据库尚未创建:{_path}");
            return false;
        }

        CredentialFile? file;
        try
        {
            var json = File.ReadAllText(_path, Encoding.UTF8);
            file = JsonSerializer.Deserialize<CredentialFile>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            result = CredentialResult.Fail(
                CredentialError.StoreCorrupted,
                $"无法读取凭据库:{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (file is null)
        {
            result = CredentialResult.Fail(CredentialError.StoreCorrupted, "凭据库内容为空或格式不正确。");
            return false;
        }

        if (!file.Entries.TryGetValue(handle, out var base64) || string.IsNullOrEmpty(base64))
        {
            result = CredentialResult.Fail(
                CredentialError.NotFound,
                $"凭据库里没有键「{handle}」。");
            return false;
        }

        byte[] ciphertext;
        try
        {
            ciphertext = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            result = CredentialResult.Fail(
                CredentialError.StoreCorrupted,
                $"键「{handle}」的内容不是合法的 Base64,文件可能已损坏。");
            return false;
        }

        if (!DpapiSecretProtector.TryUnprotectToString(ciphertext, out var plaintext, out var error))
        {
            result = CredentialResult.Fail(CredentialError.DecryptionFailed, error ?? "解密失败。");
            return false;
        }

        secret = plaintext;
        result = CredentialResult.Ok($"已取出键「{handle}」。");
        return true;
    }

    /// <summary>取出一个密钥;失败时返回 <c>null</c>。适合只关心"有没有"的场合。</summary>
    public string? GetSecretOrNull(string handle)
        => TryGetSecret(handle, out var secret, out _) ? secret : null;

    /// <summary>判断某个键是否已设置(不解密,因此不受换机器影响)。</summary>
    public bool HasSecret(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return false;
        }

        try
        {
            if (!File.Exists(_path))
            {
                return false;
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<CredentialFile>(json, SerializerOptions);

            return file is not null
                   && file.Entries.TryGetValue(handle, out var value)
                   && !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 列出全部键名。
    /// <para><b>只返回值名,不返回值。</b> 这条 API 的形状本身就是安全约束。</para>
    /// </summary>
    public IReadOnlyList<string> ListHandles()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<CredentialFile>(json, SerializerOptions);

            return file is null
                ? []
                : file.Entries.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    // ================================================================
    //  写
    // ================================================================

    /// <summary>写入或更新一个密钥。</summary>
    public CredentialResult SetSecret(string handle, string secret)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return CredentialResult.Fail(CredentialError.InvalidArgument, "键名不能为空。");
        }

        if (string.IsNullOrEmpty(secret))
        {
            return CredentialResult.Fail(
                CredentialError.InvalidArgument,
                "密钥内容为空。如果要清除它,请使用删除操作。");
        }

        if (!DpapiSecretProtector.IsSupported)
        {
            return CredentialResult.Fail(CredentialError.NotSupported, "当前平台不支持 DPAPI。");
        }

        var file = LoadForWrite(out var loadError);
        if (file is null)
        {
            return loadError!;
        }

        string base64;
        try
        {
            base64 = Convert.ToBase64String(DpapiSecretProtector.Protect(secret));
        }
        catch (Exception ex)
        {
            return CredentialResult.Fail(
                CredentialError.IoFailure,
                $"加密失败:{ex.GetType().Name}: {ex.Message}");
        }

        file.Entries[handle] = base64;
        return Save(file, $"已保存键「{handle}」(长度 {secret.Length},DPAPI 加密)。");
    }

    /// <summary>删除一个密钥。</summary>
    public CredentialResult DeleteSecret(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return CredentialResult.Fail(CredentialError.InvalidArgument, "键名不能为空。");
        }

        var file = LoadForWrite(out var loadError);
        if (file is null)
        {
            return loadError!;
        }

        if (!file.Entries.Remove(handle))
        {
            return CredentialResult.Fail(
                CredentialError.NotFound,
                $"凭据库里没有键「{handle}」,无需删除。");
        }

        return Save(file, $"已删除键「{handle}」。");
    }

    /// <summary>清空整个凭据库。</summary>
    public CredentialResult Clear()
    {
        var file = LoadForWrite(out var loadError);
        if (file is null)
        {
            return loadError!;
        }

        var count = file.Entries.Count;
        file.Entries.Clear();

        return Save(file, $"已清空凭据库(原有 {count} 项)。");
    }

    // ================================================================
    //  内部
    // ================================================================

    private CredentialFile? LoadForWrite(out CredentialResult? error)
    {
        error = null;

        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 读取之前先把权限规范化:早先以管理员运行过的话,
            // 这里的文件所有者会是 Administrators,普通权限下可能写不进去。
            OwnershipHelper.EnsureWritableByCurrentUser(_path);

            if (!File.Exists(_path))
            {
                return new CredentialFile { Version = CurrentVersion };
            }

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var file = JsonSerializer.Deserialize<CredentialFile>(json, SerializerOptions);

            if (file is null)
            {
                error = CredentialResult.Fail(
                    CredentialError.StoreCorrupted,
                    "凭据库内容无法解析。为避免覆盖可能仍然有效的数据,本次操作已中止。");
                return null;
            }

            return file;
        }
        catch (Exception ex)
        {
            error = CredentialResult.Fail(
                CredentialError.IoFailure,
                $"读取凭据库失败:{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 原子写入。
    /// <para>
    /// 先写临时文件再替换,这样即使写到一半断电或被杀进程,
    /// 原有的凭据库也不会变成半截内容 ——
    /// 那会让用户丢掉所有已保存的密钥,而密钥恰恰是他最不想重输的东西。
    /// </para>
    /// <para>
    /// <b>临时文件名带随机后缀。</b> 早先用固定的 <c>secrets.dat.tmp</c>,
    /// 那是一个真实的脆弱点:只要有任何进程(包括上一次异常退出的自己)
    /// 恰好持有那个名字,写入就必然失败。改成唯一名字可以从根本上避免这类冲突。
    /// </para>
    /// </summary>
    private CredentialResult Save(CredentialFile file, string successMessage)
    {
        file.Version = CurrentVersion;
        var json = JsonSerializer.Serialize(file, SerializerOptions);

        // 第一次用"临时文件 + 替换",失败则退回"直接覆写"。
        //
        // 原子性有价值,但可用性更重要:如果临时文件这条路在本机环境下走不通
        // (实测确实遇到过),那退回直接覆写仍然能完成用户的目标。
        // 代价是失去了"写到一半断电"的保护,而这远小于"完全写不进去"。
        var atomicAttempt = TryAtomicSave(json, out var atomicError);

        if (atomicAttempt is null)
        {
            return CredentialResult.Ok(successMessage);
        }

        var directAttempt = TryDirectSave(json, out var directError);

        if (directAttempt is null)
        {
            return CredentialResult.Ok(
                successMessage + "(临时文件方式不可用,已直接覆写;写入过程不具备原子性)");
        }

        // 两条路都失败 —— 这时才值得把详细信息全部报出来
        return CredentialResult.Fail(
            CredentialError.IoFailure,
            BuildFailureReport(json, atomicError, atomicAttempt, directError, directAttempt));
    }

    /// <summary>尝试"临时文件 + 替换"。成功返回 <c>null</c>,失败返回错误描述。</summary>
    private string? TryAtomicSave(string json, out string? error)
    {
        error = null;

        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";

        try
        {
            OwnershipHelper.EnsureWritableByCurrentUser(_path);

            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            OwnershipHelper.EnsureWritableByCurrentUser(temporary);
            File.Move(temporary, _path, overwrite: true);

            return null;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            TryDelete(temporary);
            return temporary;
        }
    }

    /// <summary>尝试直接覆写目标文件。成功返回 <c>null</c>。</summary>
    private string? TryDirectSave(string json, out string? error)
    {
        error = null;

        try
        {
            File.WriteAllText(_path, json, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex)
        {
            error = Describe(ex);
            return _path;
        }
    }

    /// <summary>
    /// 把两条路都失败时的诊断信息拼成一段可读文本。
    /// </summary>
    /// <remarks>
    /// 这一段是刻意写详细的。此前只报一行异常消息,导致一个"上报了三次仍无法定位"
    /// 的问题 —— 因为缺少异常类型、堆栈、以及失败时刻的文件状态。
    /// 诊断信息不足的报错,比报错本身更费时间。
    /// </remarks>
    private string BuildFailureReport(
        string json,
        string? atomicError,
        string? atomicPath,
        string? directError,
        string? directPath)
    {
        var sb = new StringBuilder();

        sb.AppendLine("凭据库写入失败,两种方式都未成功。");
        sb.AppendLine();
        sb.AppendLine($"  目标文件   : {_path}");
        sb.AppendLine($"  文件已存在 : {File.Exists(_path)}");
        sb.AppendLine($"  目录已存在 : {Directory.Exists(System.IO.Path.GetDirectoryName(_path))}");
        sb.AppendLine($"  目标只读   : {IsReadOnly(_path)}");
        sb.AppendLine($"  待写入长度 : {json.Length} 字符");

        sb.AppendLine();
        sb.AppendLine("  [方式一] 临时文件 + 替换");
        sb.AppendLine($"      临时文件: {atomicPath}");
        sb.AppendLine($"      错误    : {(atomicError is null ? "(未执行)" : atomicError)}");

        sb.AppendLine();
        sb.AppendLine("  [方式二] 直接覆写");
        sb.AppendLine($"      目标文件: {directPath}");
        sb.AppendLine($"      错误    : {(directError is null ? "(未执行)" : directError)}");

        var hint = DiagnoseLikelyCause(atomicError, directError);
        if (hint is not null)
        {
            sb.AppendLine();
            sb.AppendLine("  可能原因: " + hint);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 根据错误文本给出一个**方向性**提示。
    /// </summary>
    /// <remarks>
    /// 这里刻意只给"下一步该查什么",不下结论 ——
    /// 同一个 Access Denied 可能是权限、可能是占用、也可能是安全软件,
    /// 在拿到具体错误码与文件状态之前无法区分。
    /// </remarks>
    private static string? DiagnoseLikelyCause(string? atomicError, string? directError)
    {
        var both = (atomicError ?? string.Empty) + " " + (directError ?? string.Empty);

        if (both.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
            both.Contains("正在被另一进程使用", StringComparison.Ordinal) ||
            both.Contains("0x80070020", StringComparison.OrdinalIgnoreCase))
        {
            return "文件被其它进程占用。请确认没有另一个 Twice1200 实例正在运行,然后重试。";
        }

        if (both.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            // 这里的提示必须只提到**产品里真实存在的**命令。
            //
            // 早先它指向一个工作区里的临时脚本(.\diagnose-write-path.ps1),
            // 而那个脚本不会随产品分发 —— 用户照着做只会得到"找不到文件"。
            // 报错里给出用户执行不了的建议,比不给建议更糟。
            return "被拒绝访问。请依次检查:" + Environment.NewLine +
                   "      1) 是否有另一个程序实例在运行(占用同一文件)" + Environment.NewLine +
                   "      2) 是否有安全软件拦截了本次写入(查看其拦截记录)" + Environment.NewLine +
                   "      3) 本程序是否运行在受限环境里" + Environment.NewLine +
                   "         启动日志里的「完整性级别」若显示 Low,则本进程无法写入" + Environment.NewLine +
                   "         用户配置目录(强制完整性控制所致,与文件权限无关)。" + Environment.NewLine +
                   "         解决办法:把程序放到工作区/受限目录之外再运行。" + Environment.NewLine +
                   "      4) 若确实需要从受限位置运行,可用 --data-root <可写目录>" + Environment.NewLine +
                   "         或环境变量 TWICE1200_DATA_ROOT 指定数据目录。";
        }

        if (both.Contains("Could not find a part of the path", StringComparison.OrdinalIgnoreCase))
        {
            return "路径的一部分不存在。请确认配置目录可被创建。";
        }

        return null;
    }

    /// <summary>把异常压成一段包含类型、错误码与堆栈的文本。</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();

        sb.Append(ex.GetType().Name).Append(": ").Append(ex.Message);

        if (ex is System.ComponentModel.Win32Exception win32)
        {
            sb.Append($"  (Win32 错误码 0x{win32.NativeErrorCode:X8} = {win32.NativeErrorCode})");
        }

        // HResult 能区分 Win32 错误族:0x80070005 = 拒绝访问,0x80070020 = 共享冲突
        sb.Append($"  [HResult 0x{ex.HResult:X8}]");

        var inner = ex.InnerException;
        var depth = 0;
        while (inner is not null && depth < 3)
        {
            sb.Append(Environment.NewLine).Append("        ← ")
              .Append(inner.GetType().Name).Append(": ").Append(inner.Message);
            inner = inner.InnerException;
            depth++;
        }

        if (!string.IsNullOrWhiteSpace(ex.StackTrace))
        {
            var firstFrame = ex.StackTrace
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);

            if (firstFrame is not null)
            {
                sb.Append(Environment.NewLine).Append("        堆栈首帧: ").Append(firstFrame);
            }
        }

        return sb.ToString();
    }

    private static string IsReadOnly(string path)
    {
        try
        {
            return File.Exists(path)
                ? (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0 ? "是" : "否"
                : "(文件不存在)";
        }
        catch (Exception ex)
        {
            return $"(读取属性失败: {ex.GetType().Name})";
        }
    }

    /// <summary>删除文件,忽略一切错误。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就继续尝试写入,由上层报告真实错误
        }
    }

    /// <summary>凭据文件的磁盘格式。</summary>
    private sealed class CredentialFile
    {
        /// <summary>格式版本。</summary>
        public int Version { get; set; } = CurrentVersion;

        /// <summary>键名 → Base64 编码的 DPAPI 密文。</summary>
        public Dictionary<string, string> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}
