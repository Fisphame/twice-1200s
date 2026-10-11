using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Twice1200.Core.Security;

/// <summary>
/// 用 Windows DPAPI 保护密钥材料。
/// </summary>
/// <remarks>
/// <para>
/// <b>这里直接 P/Invoke <c>crypt32.dll</c>,没有使用 <c>System.Security.Cryptography.ProtectedData</c> 包。</b>
/// 两个原因:
/// </para>
/// <list type="number">
///   <item><description>
///     那个类型不在 BCL 里,需要引入 NuGet 包。而本项目刻意做到
///     <b>零 NuGet 依赖</b> —— 这样它永远可以构建,不受包还原问题影响。
///   </description></item>
///   <item><description>
///     DPAPI 本身就是两个导出函数。自己声明反而更透明:
///     内存的分配与释放都看得见,不需要猜封装层做了什么。
///   </description></item>
/// </list>
/// <para>
/// <b>作用域用的是当前用户(<c>CRYPTPROTECT_UI_FORBIDDEN</c>,不传 <c>LOCAL_MACHINE</c>)。</b>
/// 两者的区别很关键:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>当前用户</b> —— 只有同一个 Windows 用户能解密。攻击者拿到密文文件也没用,
///     除非他能以你的身份登录这台机器。这是本项目的选择。
///   </description></item>
///   <item><description>
///     <b>LOCAL_MACHINE</b> —— 这台机器上的<b>任何</b>用户都能解密。
///     只有在需要"服务账户在无人登录时也能解密"时才该用,本项目不需要。
///   </description></item>
/// </list>
/// <para>
/// <b>DPAPI 不提供什么:</b> 不防"以同一用户身份运行的其它进程";
/// <b>不能跨机器共享</b>;重装系统或更换用户配置文件后旧密文失效。
/// 这些都是用户态方案的固有边界,不是实现缺陷。
/// </para>
/// </remarks>
public static class DpapiSecretProtector
{
    /// <summary>禁止在解密时弹出任何界面提示。无人值守时这一点很重要。</summary>
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>是否可以在当前平台上使用(仅 Windows 支持 DPAPI)。</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>加密一段文本。</summary>
    public static byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Protect(Encoding.UTF8.GetBytes(plaintext));
    }

    /// <summary>加密一段字节。</summary>
    /// <exception cref="ArgumentException">输入为空。</exception>
    /// <exception cref="PlatformNotSupportedException">非 Windows 平台。</exception>
    [SupportedOSPlatform("windows")]
    public static byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        EnsureSupported();

        // DPAPI 不接受零长度输入(实测:CryptProtectData 在 cbData=0 时失败)。
        // 与其把一个 Win32 错误码透给调用方,不如在这里明确拒绝 ——
        // 而且"保存一个空密钥"本身就说不通:要清除请用删除操作。
        if (plaintext.Length == 0)
        {
            throw new ArgumentException(
                "不能加密空内容。DPAPI 不接受零长度输入;若要清除凭据请使用删除操作。",
                nameof(plaintext));
        }

        var input = new DataBlob(plaintext);
        var output = default(DataBlob);

        try
        {
            // 不传 entropy:它只是"额外的盐",而盐本身也得存到某处,
            // 存了等于没增加实质安全性,反而多一个会丢失的状态。
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "CryptProtectData 调用失败。");
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    /// <summary>解密成文本。</summary>
    public static string UnprotectToString(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        return Encoding.UTF8.GetString(Unprotect(ciphertext));
    }

    /// <summary>解密成字节。</summary>
    /// <exception cref="ArgumentException">输入为空。</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">密文损坏,或换了用户/机器。</exception>
    [SupportedOSPlatform("windows")]
    public static byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        EnsureSupported();

        if (ciphertext.Length == 0)
        {
            throw new ArgumentException("不能解密空内容。", nameof(ciphertext));
        }

        var input = new DataBlob(ciphertext);
        var output = default(DataBlob);

        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "CryptUnprotectData 调用失败。");
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    /// <summary>
    /// 尝试解密。失败时返回 <c>false</c> 而不是抛出异常。
    /// <para>
    /// 因为"密文解不开"在实践中是**预期情况**(换了机器、换了用户、重装系统),
    /// 调用方需要区别对待并给出恰当提示,而不是崩掉或者把整个凭据库判为坏掉。
    /// </para>
    /// </summary>
    public static bool TryUnprotectToString(byte[] ciphertext, out string? plaintext, out string? error)
    {
        plaintext = null;
        error = null;

        try
        {
            plaintext = UnprotectToString(ciphertext);
            return true;
        }
        catch (PlatformNotSupportedException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            error = "无法解密 —— 密文可能已损坏,或者它是在另一台机器/另一个 Windows 用户下创建的。";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("DPAPI 是 Windows 专有能力,当前平台不支持。");
        }
    }

    /// <summary>
    /// 对应 Win32 的 <c>DATA_BLOB</c>。
    /// <para>
    /// 字段顺序与布局必须与原生结构一致 —— 这是互操作代码里最容易出错的地方,
    /// 因此刻意保持它最小、并把它限制在这一个文件里。
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        /// <summary>字节数。</summary>
        public int Length;

        /// <summary>指向由 <c>LocalAlloc</c> 分配的内存。</summary>
        public IntPtr Data;

        /// <summary>从托管数组创建。内存由原生分配,因此必须在用完后释放。</summary>
        public DataBlob(byte[] content)
        {
            Length = content.Length;
            Data = content.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(content.Length);

            if (Data != IntPtr.Zero)
            {
                Marshal.Copy(content, 0, Data, content.Length);
            }
        }

        /// <summary>复制出托管数组。</summary>
        public readonly byte[] ToArray()
        {
            if (Data == IntPtr.Zero || Length <= 0)
            {
                return [];
            }

            var result = new byte[Length];
            Marshal.Copy(Data, result, 0, Length);
            return result;
        }

        /// <summary>释放内存。DPAPI 输出的那块要用 LocalFree 释放。</summary>
        public void Free()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            try
            {
                LocalFree(Data);
            }
            catch
            {
                // 释放失败不应掩盖原本的结果
            }

            Data = IntPtr.Zero;
            Length = 0;
        }
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
