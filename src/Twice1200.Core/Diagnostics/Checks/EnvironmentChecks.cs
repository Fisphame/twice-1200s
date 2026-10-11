using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>检查运行环境:操作系统、权限、运行时。</summary>
public sealed class EnvironmentChecks : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.Os;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var details = new List<string>
        {
            $"操作系统   : {RuntimeInformation.OSDescription}",
            $"版本       : {Environment.OSVersion.Version}",
            $"系统架构   : {RuntimeInformation.OSArchitecture}",
            $"进程架构   : {RuntimeInformation.ProcessArchitecture}",
            $"计算机名   : {Environment.MachineName}",
            $"当前用户   : {Environment.UserName}",
        };

        var warnings = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var build = Environment.OSVersion.Version.Build;

            // TAP-Windows 9.21.2 是 2020 年的驱动,在较新的 Windows 11 上存在安装失败的报告。
            if (build >= 22000)
            {
                warnings.Add(
                    $"当前是 Windows 11(build {build})。随包分发的 TAP 驱动是 2020 年的 " +
                    "9.21.2 版本,在该系统上可能安装失败或装好后无法创建网卡实例。");
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(CheckResult.Fail(
                CheckId.Os,
                "操作系统",
                "本程序依赖 Windows 专属能力(TAP 驱动、WMI、DPAPI),无法在当前系统上运行。",
                details));
        }

        return Task.FromResult(warnings.Count == 0
            ? CheckResult.Ok(CheckId.Os, "操作系统", $"{RuntimeInformation.OSDescription}(build {Environment.OSVersion.Version.Build})", details)
            : CheckResult.Warn(
                CheckId.Os,
                "操作系统",
                warnings.Count == 1
                    ? warnings[0]
                    : $"{RuntimeInformation.OSDescription} 上存在 {warnings.Count} 项兼容性提示。",
                details,
                warnings));
    }
}

/// <summary>检查当前进程是否具备管理员权限。</summary>
[SupportedOSPlatform("windows")]
public sealed class ElevationCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.Elevation;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var isAdmin = ElevationDetector.IsElevated();

        var details = new List<string>
        {
            $"当前身份   : {(isAdmin ? "管理员" : "标准用户")}",
            "需要管理员权限的操作:安装 TAP 驱动、重命名虚拟网卡、启用被禁用的网卡",
            "不需要管理员权限的操作:查看状态、查询配置、启动 edge/frpc(网卡已就绪时)",
        };

        return Task.FromResult(isAdmin
            ? CheckResult.Ok(CheckId.Elevation, "管理员权限", "当前以管理员身份运行,可以执行驱动安装等特权操作。", details)
            : CheckResult.Warn(
                CheckId.Elevation,
                "管理员权限",
                "当前是标准用户。首次使用需要安装驱动时会弹出 UAC 提示。",
                details,
                ["这是正常状态,不是错误 —— 程序平时应以标准权限运行,只在需要时才提权。"]));
    }
}

/// <summary>
/// 管理员权限检测。
/// <para>
/// 用 <see cref="System.Security.Principal.WindowsPrincipal"/> 而不是 P/Invoke,
/// 因为这是 BCL 提供的等价能力,不需要手写互操作。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ElevationDetector
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>检查 .NET 运行时版本是否满足要求。</summary>
public sealed class DotNetRuntimeCheck : IEnvironmentCheck
{
    /// <summary>所需的最低主版本。</summary>
    private const int RequiredMajorVersion = 9;

    /// <inheritdoc />
    public string Id => CheckId.DotNet;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var version = Environment.Version;
        var description = RuntimeInformation.FrameworkDescription;

        var details = new List<string>
        {
            $"运行时版本 : {description}",
            $"进程版本号 : {version}",
            $"运行时目录 : {RuntimeEnvironment.GetRuntimeDirectory()}",
        };

        if (version.Major < RequiredMajorVersion)
        {
            return Task.FromResult(CheckResult.Fail(
                CheckId.DotNet,
                ".NET 运行时",
                $"当前运行时主版本为 {version.Major},低于所需的 {RequiredMajorVersion}。",
                details,
                [$"请安装 .NET {RequiredMajorVersion} 桌面运行时后再试。"]));
        }

        return Task.FromResult(CheckResult.Ok(
            CheckId.DotNet,
            ".NET 运行时",
            $"{description}",
            details));
    }
}
