using System.Diagnostics;

namespace Twice1200.Core.Runtime;

/// <summary>本机的一个进程快照。</summary>
/// <param name="Id">进程 ID。</param>
/// <param name="Name">进程名,不含扩展名。</param>
/// <param name="ExecutablePath">可执行文件完整路径,取不到时为 <c>null</c>。</param>
public sealed record ProcessSnapshot(int Id, string Name, string? ExecutablePath);

/// <summary>
/// 进程枚举。
/// <para>
/// 这里刻意用 <see cref="Process.GetProcesses"/> 而不是 WMI 的 <c>Win32_Process</c>:
/// 原因见 <c>docs/安全说明.md</c> —— 通过 WMI 读取进程的 <b>CommandLine</b> 属性能拿到
/// 完整的命令行,而命令行里可能含明文密码。不使用它既是性能考虑,也是安全纪律。
/// </para>
/// </summary>
public static class ProcessSnapshotter
{
    /// <summary>按进程名(不含扩展名)查找进程。大小写不敏感。</summary>
    public static IReadOnlyList<ProcessSnapshot> FindByName(params string[] names)
    {
        if (names is null || names.Length == 0)
        {
            return [];
        }

        var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var result = new List<ProcessSnapshot>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (!wanted.Contains(process.ProcessName))
                    {
                        continue;
                    }

                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // 访问别的用户的进程或系统进程时会失败,忽略即可
                    }

                    result.Add(new ProcessSnapshot(process.Id, process.ProcessName, path));
                }
            }
        }
        catch
        {
            // 枚举失败不应让体检崩掉,返回已收集到的部分
        }

        return result;
    }
}
