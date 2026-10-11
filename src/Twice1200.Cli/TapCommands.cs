using System.Text;
using System.Text.Json;
using Twice1200.Core.Assets;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;
using Twice1200.Core.Tap;

namespace Twice1200.Cli;

/// <summary>
/// <c>tap</c> 子命令:虚拟网卡的管理。
/// <para>
/// <b>这里承载了自提权的实现。</b> 需要特权的操作没法在原进程里完成,
/// 因此本程序会把自己以管理员身份重新启动一次,把要做什么和结果写在哪里
/// 通过命令行与临时文件传递。
/// </para>
/// <para>
/// 之所以不让提权进程直接把输出回传给父进程:Windows 的
/// <c>Start-Process -Verb RunAs</c> <b>无法重定向标准输出</b>。
/// 所以走"子进程自己执行并写结果文件,父进程读文件"这条路 ——
/// 顺带的好处是结果结构清晰,不依赖管道。
/// </para>
/// </summary>
internal static class TapCommands
{
    /// <summary>处理 <c>tap</c> 子命令。</summary>
    public static async Task<int> RunAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var action = reader.Positional.Count > 1 ? reader.Positional[1].ToLowerInvariant() : "status";

        return action switch
        {
            "status" or "show" => RunStatus(reader),
            "ensure" or "setup" => await RunEnsureAsync(reader, cancellationToken).ConfigureAwait(false),
            "ensure-elevated" => await RunEnsureElevatedAsync(reader, cancellationToken).ConfigureAwait(false),
            "rename" => await RunSimpleAsync(reader, cancellationToken, "rename").ConfigureAwait(false),
            "enable" => await RunSimpleAsync(reader, cancellationToken, "enable").ConfigureAwait(false),
            "create" => await RunSimpleAsync(reader, cancellationToken, "create").ConfigureAwait(false),
            _ => throw new CliUsageException($"未知的 tap 子命令「{action}」。可用:status / ensure / rename / enable / create"),
        };
    }

    // ================================================================
    //  tap status
    // ================================================================

    private static int RunStatus(ArgumentReader reader)
    {
        var assets = AssetLocator.Resolve(reader.GetString("assets"));

        Console.WriteLine("── 虚拟网卡状态 ──");
        Console.WriteLine();

        var adapters = TapAdapterManager.ListAdapters();

        if (adapters.Count == 0)
        {
            Console.WriteLine("  未检测到 TAP 类虚拟网卡。");
        }
        else
        {
            foreach (var adapter in adapters)
            {
                var addresses = adapter.IPv4Addresses.Count > 0
                    ? string.Join(", ", adapter.IPv4Addresses.Select(a => a.ToString()))
                    : "(无地址)";

                Console.WriteLine($"  「{adapter.Name}」");
                Console.WriteLine($"      描述 : {adapter.Description}");
                Console.WriteLine($"      状态 : {adapter.Status}");
                Console.WriteLine($"      MAC  : {adapter.MacAddress ?? "?"}");
                Console.WriteLine($"      地址 : {addresses}");
                Console.WriteLine();
            }
        }

        Console.WriteLine("── 驱动 ──");
        Console.WriteLine($"  驱动服务已注册 : {(TapAdapterManager.IsDriverRegistered() ? "是" : "否")}");
        Console.WriteLine($"  驱动服务名     : {TapAdapterManager.DriverServiceName}");
        Console.WriteLine($"  tapinstall.exe : {TapAdapterManager.FindTapInstall() ?? "(未找到 —— 驱动可能尚未安装)"}");
        Console.WriteLine($"  驱动 INF       : {TapAdapterManager.FindDriverInf() ?? "(未找到)"}");
        Console.WriteLine($"  随包驱动安装包 : {TapAdapterManager.FindDriverInstaller(assets) ?? "(资源目录未解析)"}");

        Console.WriteLine();
        Console.WriteLine("── 权限 ──");
        Console.WriteLine($"  当前进程 : {(TapAdapterManager.IsElevated() ? "管理员" : "标准用户")}");
        Console.WriteLine("  需要管理员权限的操作 : 安装驱动、创建网卡、重命名、启用");
        Console.WriteLine("  不需要权限的操作     : 本节显示的所有内容");

        return 0;
    }

    // ================================================================
    //  tap ensure —— 自动提权
    // ================================================================

    private static async Task<int> RunEnsureAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var assets = AssetLocator.Resolve(reader.GetString("assets"));
        var name = reader.GetString("name") ?? "n2n-TAP";

        Console.WriteLine($"目标网卡名:「{name}」");
        Console.WriteLine();

        // 已经有同名网卡:不需要提权,直接报告
        if (TapAdapterManager.FindAdapter(name) is not null)
        {
            Console.WriteLine($"[+] 虚拟网卡「{name}」已存在,无需改动。");
            return 0;
        }

        if (!TapAdapterManager.IsElevated())
        {
            Console.WriteLine("[*] 需要管理员权限才能安装驱动或创建/重命名网卡。");
            Console.WriteLine("    正在请求提权(请在弹出的 UAC 对话框点「是」)…");
            Console.WriteLine();

            return await ElevateAndRunAsync(name, assets, cancellationToken).ConfigureAwait(false);
        }

        // 已经提权:直接执行
        return await PerformEnsureAsync(name, assets, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 以管理员身份重新启动本程序,执行 <c>tap ensure-elevated</c>,然后读回结果。
    /// </summary>
    private static async Task<int> ElevateAndRunAsync(
        string name,
        AssetLocator assets,
        CancellationToken cancellationToken)
    {
        var resultFile = Path.Combine(
            Path.GetTempPath(),
            $"twice1200-tap-{Guid.NewGuid():N}.json");

        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            Console.Error.WriteLine("[错误] 无法确定本程序路径,不能自提权。");
            return 70;
        }

        // 用 -Command 而不是 -ArgumentList 拼参数:这样路径里的空格与引号
        // 由我们自己控制得更明确,也不用担心 Start-Process 的拼接规则。
        var inner = new StringBuilder();
        inner.Append("& '").Append(executable.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        inner.Append(" tap ensure-elevated");
        inner.Append(" --result-file '").Append(resultFile.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        inner.Append(" --name '").Append(name.Replace("'", "''", StringComparison.Ordinal)).Append('\'');

        if (assets.Root is not null)
        {
            inner.Append(" --assets '").Append(assets.Root.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        }

        try
        {
            using var elevated = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + inner + "\"",
                UseShellExecute = true,
                Verb = "runas",
            });

            if (elevated is null)
            {
                Console.Error.WriteLine("[错误] 提权进程未能启动。");
                return 70;
            }

            Console.WriteLine("    已启动提权进程,等待完成…");
            Console.WriteLine();

            // 提权进程没法回传输出,只能等它结束再读文件
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await elevated.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[错误] 等待提权进程超时。");
                try
                {
                    elevated.Kill();
                }
                catch
                {
                    // 忽略
                }

                return 70;
            }

            if (!File.Exists(resultFile))
            {
                Console.Error.WriteLine("[错误] 提权进程没有留下结果文件。");
                Console.Error.WriteLine("       它可能异常退出了。请以管理员身份手工运行一次本命令以查看原因。");
                return 70;
            }

            var json = await File.ReadAllTextAsync(resultFile, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            File.Delete(resultFile);

            var payload = JsonSerializer.Deserialize<ElevatedPayload>(json);
            if (payload is null)
            {
                Console.Error.WriteLine("[错误] 结果文件无法解析。");
                return 70;
            }

            PrintPayload(payload);
            return payload.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[错误] 提权被取消或失败:{ex.Message}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("若你在 UAC 对话框点了「否」,这属于正常结果 —— 虚拟网卡不会发生任何改动。");
            return 74;
        }
    }

    /// <summary>
    /// 在已提权的进程里执行实际操作,并把结果写进文件。
    /// <para>这是 <c>tap ensure</c> 的"另一半",由父进程用管理员权限拉起。</para>
    /// </summary>
    private static async Task<int> RunEnsureElevatedAsync(ArgumentReader reader, CancellationToken cancellationToken)
    {
        var resultFile = reader.GetString("result-file");
        var name = reader.GetString("name") ?? "n2n-TAP";
        var assets = AssetLocator.Resolve(reader.GetString("assets"));

        var log = new List<string>();
        var progress = new Progress<string>(message =>
        {
            log.Add(message);
            Console.WriteLine("    " + message);
        });

        TapOperationResult result;
        try
        {
            result = await new TapAdapterManager()
                .EnsureAdapterAsync(name, assets, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = TapOperationResult.Fail(
                TapOperationStatus.CommandFailed,
                $"执行时抛出异常:{ex.GetType().Name}: {ex.Message}");
        }

        var exitCode = result.IsSuccess ? 0 : 1;

        var payload = new ElevatedPayload(
            exitCode,
            result.Status.ToString(),
            result.Message,
            result.CommandLine,
            result.Output?.ToList(),
            log);

        if (resultFile is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(resultFile, json, new UTF8Encoding(false), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[错误] 无法写入结果文件:{ex.Message}");
                return 70;
            }
        }

        Console.WriteLine();
        Console.WriteLine(result.IsSuccess ? "[+] " + result.Message : "[×] " + result.Message);

        return exitCode;
    }

    private sealed record ElevatedPayload(
        int ExitCode,
        string Status,
        string Message,
        string? CommandLine,
        List<string>? Output,
        List<string> Progress);

    private static void PrintPayload(ElevatedPayload payload)
    {
        Console.WriteLine("── 提权进程的结果 ──");
        Console.WriteLine();

        if (payload.Progress.Count > 0)
        {
            foreach (var line in payload.Progress)
            {
                Console.WriteLine("  · " + line);
            }

            Console.WriteLine();
        }

        var prefix = payload.ExitCode == 0 ? "[+]" : "[×]";
        Console.WriteLine($"{prefix} {payload.Message}");

        if (payload.CommandLine is not null)
        {
            Console.WriteLine();
            Console.WriteLine("  执行的命令: " + payload.CommandLine);
        }

        if (payload.Output is { Count: > 0 })
        {
            Console.WriteLine();
            Console.WriteLine("  命令输出:");
            foreach (var line in payload.Output.TakeLast(15))
            {
                Console.WriteLine("    " + line);
            }
        }
    }

    // ================================================================
    //  rename / enable / create —— 直接执行(要求已提权)
    // ================================================================

    private static async Task<int> RunSimpleAsync(
        ArgumentReader reader,
        CancellationToken cancellationToken,
        string operation)
    {
        if (!TapAdapterManager.IsElevated())
        {
            Console.Error.WriteLine($"[错误] {operation} 需要管理员权限。");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  请改用 tap ensure —— 它会自动请求提权并处理全流程:");
            Console.Error.WriteLine("      twice1200 tap ensure --name <目标网卡名>");
            return 74;
        }

        var manager = new TapAdapterManager();
        var assets = AssetLocator.Resolve(reader.GetString("assets"));

        TapOperationResult result = operation switch
        {
            "rename" => await manager.RenameAdapterAsync(
                reader.GetString("from") ?? throw new CliUsageException("rename 需要 --from <当前名称>"),
                reader.GetString("to") ?? reader.GetString("name") ?? throw new CliUsageException("rename 需要 --to <新名称>"),
                cancellationToken).ConfigureAwait(false),

            "enable" => await manager.EnableAdapterAsync(
                reader.GetString("name") ?? throw new CliUsageException("enable 需要 --name <网卡名>"),
                cancellationToken).ConfigureAwait(false),

            "create" => await manager.CreateAdapterAsync(
                reader.GetString("install-dir"),
                cancellationToken).ConfigureAwait(false),

            _ => throw new CliUsageException($"未实现的操作「{operation}」。"),
        };

        return ReportResult(result);
    }

    /// <summary>真正执行 ensure 流程并输出结果。</summary>
    private static async Task<int> PerformEnsureAsync(
        string name,
        AssetLocator assets,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<string>(message => Console.WriteLine("    " + message));

        var result = await new TapAdapterManager()
            .EnsureAdapterAsync(name, assets, progress, cancellationToken)
            .ConfigureAwait(false);

        return ReportResult(result);
    }

    private static int ReportResult(TapOperationResult result)
    {
        Console.WriteLine();
        Console.WriteLine((result.IsSuccess ? "[+] " : "[×] ") + result.Message);

        if (result.CommandLine is not null)
        {
            Console.WriteLine("    命令: " + result.CommandLine);
        }

        if (result.Output is { Count: > 0 })
        {
            Console.WriteLine("    输出:");
            foreach (var line in result.Output.TakeLast(10))
            {
                Console.WriteLine("      " + line);
            }
        }

        if (!result.IsSuccess && result.Status == TapOperationStatus.NotElevated)
        {
            Console.WriteLine();
            Console.WriteLine("    提示:改用 tap ensure,它会自动请求提权。");
        }

        return result.IsSuccess ? 0 : 1;
    }
}
