using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Twice1200.Core.Security;

/// <summary>
/// 报告当前进程的访问令牌形状。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它:</b> 一个反复查不出的故障 —— 同一个目录,
/// 用户自己的 PowerShell 能写,而本程序写不了;ACL 完全正常
/// (当前用户有 <c>FullControl</c>,祖先链无拒绝项)。
/// </para>
/// <para>
/// 既然文件权限排除了,剩下的可能就在<b>进程的令牌</b>上:
/// 完整性级别被降低、令牌是受限令牌(<c>TokenIsRestricted</c>)、
/// 或者进程运行在 AppContainer 里。这三种都会导致
/// <c>CreateFile</c> 返回 <c>ERROR_ACCESS_DENIED</c>,
/// 而<b>任何 ACL 查询都看不出来</b> —— 这正是"权限看起来没问题却写不进去"的原因。
/// </para>
/// <para>
/// 所以这一项不是猜,而是把令牌的真实形状读出来。
/// </para>
/// </remarks>
public static class TokenInspector
{
    private const int TokenIntegrityLevel = 25;
    private const int TokenIsAppContainer = 29;
    private const int TokenIsRestricted = 40;
    private const int TokenAppContainerSid = 31;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    /// <summary>令牌形状的快照。</summary>
    public sealed record TokenShape(
        string User,
        bool IsElevated,
        string IntegrityLevel,
        int IntegrityLevelRid,
        bool IsAppContainer,
        bool IsRestricted,
        string AppContainerSid);

    /// <summary>
    /// 读取当前进程的令牌形状。
    /// </summary>
    public static TokenShape Inspect()
    {
        string user;
        var isElevated = false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            user = identity.Name;

            var principal = new WindowsPrincipal(identity);
            isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            user = $"(读取失败: {ex.GetType().Name})";
        }

        var integrity = "(未知)";
        var rid = -1;
        var isAppContainer = false;
        var isRestricted = false;
        var containerSid = "(无)";

        try
        {
            var token = WindowsIdentity.GetCurrent(TokenAccessLevels.Query).Token;
            if (token != IntPtr.Zero)
            {
                isAppContainer = ReadBool(token, TokenIsAppContainer);
                isRestricted = ReadBool(token, TokenIsRestricted);
                containerSid = ReadAppContainerSid(token);
                (integrity, rid) = ReadIntegrity(token);
            }
        }
        catch (Exception ex)
        {
            integrity = $"(读取失败: {ex.GetType().Name})";
        }

        return new TokenShape(
            user,
            isElevated,
            integrity,
            rid,
            isAppContainer,
            isRestricted,
            containerSid);
    }

    /// <summary>把令牌形状格式化成多行文本。</summary>
    public static string Describe()
    {
        var shape = Inspect();
        var sb = new StringBuilder();

        sb.AppendLine($"用户           : {shape.User}");
        sb.AppendLine($"管理员         : {shape.IsElevated}");
        sb.AppendLine($"完整性级别     : {shape.IntegrityLevel} (RID {shape.IntegrityLevelRid})");
        sb.AppendLine($"受限令牌       : {shape.IsRestricted}");
        sb.AppendLine($"AppContainer   : {shape.IsAppContainer}");
        sb.AppendLine($"容器 SID       : {shape.AppContainerSid}");

        // 给出明确的判断,而不是让读者自己推
        sb.AppendLine();

        if (shape.IsAppContainer)
        {
            sb.AppendLine("  ⚠ 本进程运行在 AppContainer 中 —— 文件访问受容器策略限制,");
            sb.AppendLine("     这与普通用户权限无关,ACL 看起来正常也仍会被拒绝。");
        }
        else if (shape.IsRestricted)
        {
            sb.AppendLine("  ⚠ 本进程使用的是受限令牌 —— 令牌里的组被标记为「仅拒绝」,");
            sb.AppendLine("     因此权限检查结果会低于预期。");
        }
        else if (shape.IntegrityLevelRid >= 0 && shape.IntegrityLevelRid < 0x2000)
        {
            sb.AppendLine("  ⚠ 完整性与普通进程相同或更低。若目标目录带更高的完整性标签,");
            sb.AppendLine("     写入会被拒绝,而 ACL 依然显示正常。");
        }
        else
        {
            sb.AppendLine("  令牌形状正常(普通用户、无受限、无容器)。");
            sb.AppendLine("  若此时仍写不进去,原因不在令牌这一层。");
        }

        return sb.ToString();
    }

    private static bool ReadBool(IntPtr token, int informationClass)
    {
        var size = sizeof(int);
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, size, out _))
            {
                return false;
            }

            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string ReadAppContainerSid(IntPtr token)
    {
        GetTokenInformation(token, TokenAppContainerSid, IntPtr.Zero, 0, out var needed);

        if (needed <= 0)
        {
            return "(无)";
        }

        var buffer = Marshal.AllocHGlobal(needed);

        try
        {
            if (!GetTokenInformation(token, TokenAppContainerSid, buffer, needed, out _))
            {
                return "(无)";
            }

            var sidAndAttributes = Marshal.PtrToStructure<SidAndAttributes>(buffer);

            return sidAndAttributes.Sid == IntPtr.Zero
                ? "(无)"
                : SidToString(sidAndAttributes.Sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (string Name, int Rid) ReadIntegrity(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);

        if (needed <= 0)
        {
            return ("(未知)", -1);
        }

        var buffer = Marshal.AllocHGlobal(needed);

        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
            {
                return ("(未知)", -1);
            }

            var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);

            if (label.Label.Sid == IntPtr.Zero)
            {
                return ("(未知)", -1);
            }

            // 完整性级别 SID 的最后一个子授权就是级别值
            var rid = GetLastSubAuthority(label.Label.Sid);
            var name = rid switch
            {
                0x0000 => "Untrusted",
                0x1000 => "Low",
                0x2000 => "Medium",
                0x2100 => "MediumPlus",
                0x3000 => "High",
                0x4000 => "System",
                _ => "(其它)",
            };

            return (name, rid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int GetLastSubAuthority(IntPtr sid)
    {
        // SID 布局:Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[]
        var count = Marshal.ReadByte(sid, 1);

        if (count == 0)
        {
            return -1;
        }

        // 第一个子授权从偏移 8 开始,每个 4 字节
        return Marshal.ReadInt32(sid, 8 + ((count - 1) * 4));
    }

    private static string SidToString(IntPtr sid)
    {
        if (ConvertSidToStringSid(sid, out var stringSid))
        {
            try
            {
                return Marshal.PtrToStringUni(stringSid) ?? "(无)";
            }
            finally
            {
                LocalFree(stringSid);
            }
        }

        return "(转换失败)";
    }
}
