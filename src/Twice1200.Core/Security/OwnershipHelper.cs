using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Twice1200.Core.Security;

/// <summary>
/// 确保应用程序自己创建的文件对当前用户立即可写。
/// </summary>
/// <remarks>
/// <para>
/// <b>它修的是一个真实故障:</b> 用户以管理员身份运行过一次程序之后,
/// 再用普通权限运行会报 <c>Access Denied</c> ——
/// 因为那些文件的所有者是 <c>BUILTIN\Administrators</c>,而不是当前用户。
/// </para>
/// <para>
/// <b>关于根因的诚实说明:</b> 观察到的 ACL 里当前用户其实有 <c>FullControl</c>,
/// 所以"权限不足"并不能完全解释这个现象。我们没有在自己的环境里复现它
/// (受限环境不允许写 <c>%APPDATA%</c>),因此不去猜测 Windows 的具体判定规则
/// —— 那会变成编造解释。
/// </para>
/// <para>
/// 这里的做法是<b>让两种可能的原因都消失</b>:显式把所有者设为当前用户,
/// 并显式授予当前用户 <c>FullControl</c>,不再依赖继承。
/// 纯粹的"保险"式设置,代价很小。
/// </para>
/// <para>
/// <b>只动我们自己的文件:</b> 不给系统目录或别人的文件改权限。
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class OwnershipHelper
{
    /// <summary>
    /// 确保指定路径(文件或目录)可被当前用户写入。
    /// <para>
    /// 目录不存在时会连同必要的父目录一起创建。失败时静默返回 ——
    /// 调用方随后会执行真正的读写操作,由它去报告失败原因。
    /// </para>
    /// </summary>
    /// <param name="path">目标文件或目录的完整路径。</param>
    /// <returns>是否成功完成规范化。</returns>
    public static bool EnsureWritableByCurrentUser(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows())
        {
            return false;
        }

        // 分成两步,并区分对待 —— 这是刻意的:
        //
        //   创建目录  : 必要条件。它失败意味着后续写入必然失败,如实返回 false。
        //   设置权限  : 尽力而为。它失败不应让调用方以为"不可写" ——
        //               权限往往本来就是对的,这一步只是加固。
        //
        // 早先把两者混在同一个 try 里,结果是"权限那一小步失败"会被报成
        // "整个操作不可用",从而误导诊断方向。
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch
            {
                // 目录建不出来,写入不可能成功
                return false;
            }

            // 目录权限收不紧不影响功能,忽略结果
            TryGrantCurrentUser(directory, isDirectory: true);
        }

        if (File.Exists(path))
        {
            TryGrantCurrentUser(path, isDirectory: false);
        }

        return true;
    }

    /// <summary>尽力给目标授予当前用户权限;失败就放弃,不向上报错。</summary>
    private static void TryGrantCurrentUser(string path, bool isDirectory)
    {
        try
        {
            GrantCurrentUser(path, isDirectory);
        }
        catch
        {
            // 权限本来就可能是对的;这一步只是加固,失败不应影响功能
        }
    }

    /// <summary>给一个文件或目录显式授予当前用户完全控制,并把所有者设为当前用户。</summary>
    private static void GrantCurrentUser(string path, bool isDirectory)
    {
        var identity = WindowsIdentity.GetCurrent();
        var user = identity.User;

        if (user is null)
        {
            return;
        }

        FileSystemSecurity security = isDirectory
            ? new DirectorySecurity(path, AccessControlSections.Access | AccessControlSections.Owner)
            : new FileSecurity(path, AccessControlSections.Access | AccessControlSections.Owner);

        // 先看有没有现成的、针对当前用户的规则;有就不重复添加
        var existing = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule =>
                rule.IdentityReference is SecurityIdentifier sid &&
                sid.Equals(user) &&
                rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);

        var changed = false;

        if (!existing)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));

            changed = true;
        }

        // 把所有者一并设成当前用户。
        // 这是关键一步:覆盖一个"由提权进程创建、所有者是 Administrators"的文件时,
        // 非提权进程在某些配置下会被拒绝 —— 把所有者改回来可以规避这一类问题。
        try
        {
            var currentOwner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

            if (currentOwner is null || !currentOwner.Equals(user))
            {
                security.SetOwner(user);
                changed = true;
            }
        }
        catch
        {
            // 有些文件系统不支持改所有者;不影响其余的权限设置
        }

        if (!changed)
        {
            return;
        }

        if (isDirectory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }
}
