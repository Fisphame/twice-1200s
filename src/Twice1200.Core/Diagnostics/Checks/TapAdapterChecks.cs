using Twice1200.Core.Assets;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>TAP 类虚拟网卡。判定基于描述匹配,不依赖 WMI。</summary>
internal static class TapDetection
{
    /// <summary>在注册表网络适配器表中查找使用 TAP 驱动服务的适配器。</summary>
    /// <param name="driverInstalled">驱动是否已注册。</param>
    /// <param name="instanceCount">已创建的适配器实例数量。</param>
    /// <param name="names">各实例的连接名(可能因为实例未创建而为空)。</param>
    public static void InspectRegistry(
        out bool driverInstalled,
        out int instanceCount,
        out IReadOnlyList<string> names)
    {
        driverInstalled = false;
        instanceCount = 0;
        var found = new List<string>();

        if (!OperatingSystem.IsWindows())
        {
            names = found;
            return;
        }

        try
        {
            // 网络适配器类 GUID,Windows 固定值
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");

            if (classKey is null)
            {
                names = found;
                return;
            }

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // 只处理 4 位数字的实例键,跳过 Properties 之类的杂项
                if (subKeyName.Length != 4 || !subKeyName.All(char.IsAsciiDigit))
                {
                    continue;
                }

                using var subKey = classKey.OpenSubKey(subKeyName);
                if (subKey is null)
                {
                    continue;
                }

                var service = subKey.GetValue("Service") as string;
                var isTap = !string.IsNullOrEmpty(service) &&
                            service.StartsWith("tap", StringComparison.OrdinalIgnoreCase);

                if (!isTap)
                {
                    continue;
                }

                driverInstalled = true;

                // 没有 DriverDesc 的键通常是没有实际实例的残留
                var description = subKey.GetValue("DriverDesc") as string;
                if (string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                instanceCount++;

                if (subKey.GetValue("NetConnectionID") is string connectionName &&
                    !string.IsNullOrWhiteSpace(connectionName))
                {
                    found.Add(connectionName);
                }
            }
        }
        catch
        {
            // 读注册表失败时保持"未检测到"的保守结论
        }

        names = found;
    }
}

/// <summary>检查 TAP 虚拟网卡是否已安装。</summary>
public sealed class TapPresenceCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.TapPresence;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var live = NetworkInterfaceInspector.FindTapLike();
        TapDetection.InspectRegistry(out var driverInstalled, out var instanceCount, out var registryNames);

        var details = new List<string>();

        foreach (var nic in live)
        {
            var addresses = nic.IPv4Addresses.Count > 0
                ? string.Join(", ", nic.IPv4Addresses.Select(a => a.ToString()))
                : "(无 IPv4 地址)";

            details.Add($"{nic.Name}  [{nic.Description}]");
            details.Add($"    状态 {nic.Status} · 类型 {nic.Type} · MAC {nic.MacAddress ?? "?"} · 地址 {addresses}");
        }

        if (live.Count == 0 && registryNames.Count > 0)
        {
            details.Add("注册表中存在 TAP 适配器记录,但当前没有对应的活动接口:");
            foreach (var name in registryNames)
            {
                details.Add($"  · {name}");
            }
        }

        // 提示其它虚拟局域网工具,避免用户误把 Radmin / ZeroTier 当成 n2n 的网卡
        var others = NetworkInterfaceInspector.Enumerate()
            .Select(nic => (Nic: nic, Marker: NetworkInterfaceInspector.DetectOtherVirtualAdapter(nic)))
            .Where(x => x.Marker is not null)
            .ToList();

        if (others.Count > 0)
        {
            details.Add("另检测到其它虚拟网络工具(与 n2n 无关,请勿混淆):");
            foreach (var (nic, marker) in others)
            {
                details.Add($"  · {marker} — 接口名「{nic.Name}」");
            }
        }

        if (live.Count > 0)
        {
            // 网卡上残留着 IPv4 地址通常说明上一次连接没有干净退出(或系统重启前 edge 被杀掉)。
            // 这本身不影响使用,但值得让用户知道,因为它会让"当前是否已连接"的判断变得含糊。
            var withAddress = live.Where(t => t.IPv4Addresses.Count > 0).ToList();

            if (withAddress.Count > 0)
            {
                var nic = withAddress[0];
                return Task.FromResult(CheckResult.Warn(
                    CheckId.TapPresence,
                    "TAP 虚拟网卡",
                    $"已安装 {live.Count} 个 TAP 虚拟网卡,但「{nic.Name}」上还留有 IP 地址 " +
                    $"{string.Join(", ", nic.IPv4Addresses.Select(a => a.ToString()))},可能是上次连接未正常退出。",
                    details,
                    ["重新连接前建议先结束残留的 edge 进程,避免旧地址与新连接冲突。"]));
            }

            return Task.FromResult(CheckResult.Ok(
                CheckId.TapPresence,
                "TAP 虚拟网卡",
                $"已安装 {live.Count} 个 TAP 类虚拟网卡。",
                details));
        }

        if (driverInstalled)
        {
            return Task.FromResult(CheckResult.Fail(
                CheckId.TapPresence,
                "TAP 虚拟网卡",
                instanceCount > 0
                    ? "驱动已安装,但检测不到活动的虚拟网卡接口。"
                    : "驱动已注册,但尚未创建虚拟网卡实例。",
                details,
                [
                    "先在设备管理器里确认是否存在 TAP 网卡。",
                    "若驱动已装但没有网卡实例,可用 tapinstall 手动创建(程序后续会提供该操作)。",
                    "若刚装完驱动,重启系统后再运行一次体检。",
                    "注意:这一项只说明「接口不可见」,不一定是驱动坏了 —— " +
                    "也可能是权限或参数问题导致上游工具看不到它,建议同时看 T-04 与 N-04。",
                ]));
        }

        var installer = options.Assets.Resolve(AssetPaths.TapInstaller);

        return Task.FromResult(CheckResult.Fail(
            CheckId.TapPresence,
            "TAP 虚拟网卡",
            "未检测到 TAP 虚拟网卡,也没有找到已注册的 TAP 驱动。",
            details,
            [
                "这是首次使用的正常状态 —— 需要先安装虚拟网卡驱动。",
                installer is null
                    ? "资源目录未找到,无法定位驱动安装包。请用 --assets 指定 resources 目录。"
                    : $"驱动安装包位于:{installer}",
            ]));
    }
}

/// <summary>检查 TAP 网卡是否可用于承载连接。</summary>
public sealed class TapReadinessCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.TapReadiness;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var taps = NetworkInterfaceInspector.FindTapLike();

        if (taps.Count == 0)
        {
            return Task.FromResult(CheckResult.Skip(
                CheckId.TapReadiness,
                "网卡可用性",
                "没有 TAP 虚拟网卡,跳过可用性检查。"));
        }

        var details = new List<string>
        {
            "判断依据:网卡是否处于 Up 状态,以及是否已分配 IPv4 地址。",
            "注意:TAP 网卡在 n2n 尚未连接时通常显示为 Down 且没有 IP —— 这是正常的," +
            "因为虚拟链路要等 edge 连上之后才会建立。",
        };

        var upWithAddress = taps.Where(t => t.IsUp && t.IPv4Addresses.Count > 0).ToList();
        var upWithoutAddress = taps.Where(t => t.IsUp && t.IPv4Addresses.Count == 0).ToList();
        var down = taps.Where(t => !t.IsUp).ToList();

        foreach (var nic in taps)
        {
            var state = nic.IsUp
                ? nic.IPv4Addresses.Count > 0
                    ? $"Up,已配置 {string.Join(", ", nic.IPv4Addresses.Select(a => a.ToString()))}"
                    : "Up,但未配置 IPv4 地址"
                : $"未处于工作状态({nic.Status})";

            details.Add($"  · {nic.Name} — {state}");
        }

        if (upWithAddress.Count > 0)
        {
            var nic = upWithAddress[0];
            return Task.FromResult(CheckResult.Ok(
                CheckId.TapReadiness,
                "网卡可用性",
                $"网卡「{nic.Name}」已就绪,地址 {string.Join(", ", nic.IPv4Addresses.Select(a => a.ToString()))}。",
                details));
        }

        if (upWithoutAddress.Count > 0)
        {
            return Task.FromResult(CheckResult.Warn(
                CheckId.TapReadiness,
                "网卡可用性",
                $"网卡「{upWithoutAddress[0].Name}」处于 Up 状态但还没有 IPv4 地址。",
                details,
                ["首次连接时 edge 会为该网卡分配配置里指定的虚拟 IP。"]));
        }

        // 全部处于 Down:在"还没连过"时是完全正常的
        return Task.FromResult(CheckResult.Ok(
            CheckId.TapReadiness,
            "网卡可用性",
            $"网卡「{down[0].Name}」尚未启用链路,这是未连接时的正常状态。",
            details));
    }
}

/// <summary>检查配置里指定的网卡名在实际系统中是否存在,以及名称是否会被编码破坏。</summary>
public sealed class TapNamingCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.TapNamed;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var configured = options.Configuration?.N2n.Tap;
        var expectedName = configured?.AdapterName ?? "n2n-TAP";
        var fallbackName = configured?.FallbackAdapterName ?? "n2n-TAP";

        // 只看真实网卡,排除 Windows 叠加的过滤驱动子接口
        var all = NetworkInterfaceInspector.Enumerate()
            .Where(n => !NetworkInterfaceInspector.IsFilterInterface(n))
            .ToList();
        var tapLike = all.Where(NetworkInterfaceInspector.IsTapLike).ToList();
        var matched = tapLike.FirstOrDefault(n =>
            string.Equals(n.Name, expectedName, StringComparison.OrdinalIgnoreCase));

        var codePage = SystemCodePage.Detect();
        var isChineseCodePage = codePage == SystemCodePage.SimplifiedChinese;

        var details = new List<string>
        {
            $"系统 ANSI 代码页 : {(codePage?.ToString() ?? "读取失败")}{(isChineseCodePage ? "(简体中文)" : string.Empty)}",
            $"配置的网卡名     : {expectedName}",
            $"回退网卡名       : {fallbackName}",
        };

        if (tapLike.Count > 0)
        {
            details.Add("检测到的 TAP 类网卡:");
            foreach (var nic in tapLike)
            {
                var mark = matched is not null && string.Equals(nic.Name, matched.Name, StringComparison.OrdinalIgnoreCase)
                    ? "   ← 与配置匹配"
                    : string.Empty;
                details.Add($"  · {nic.Name}{mark}");
            }
        }

        // 中文名 + 非 936 代码页 = 必然踩坑
        if (expectedName.Any(c => c > 0x7F) && codePage is not null && !isChineseCodePage)
        {
            if (configured?.AutoFallbackOnNonChineseCodePage != false)
            {
                return Task.FromResult(CheckResult.Warn(
                    CheckId.TapNamed,
                    "网卡命名",
                    $"网卡名「{expectedName}」含中文,而当前代码页是 {codePage}。" +
                    $"启动时将自动改用「{fallbackName}」以避免 edge.exe 参数乱码。",
                    details,
                    ["这是已知问题的自动规避,不需要手动处理。"]));
            }

            return Task.FromResult(CheckResult.Fail(
                CheckId.TapNamed,
                "网卡命名",
                $"网卡名「{expectedName}」含中文,而当前代码页是 {codePage},edge.exe 会因编码转换找不到网卡。",
                details,
                [$"请把网卡改名为纯 ASCII(例如 {fallbackName}),或启用自动回退。"]));
        }

        if (tapLike.Count == 0)
        {
            return Task.FromResult(CheckResult.Skip(
                CheckId.TapNamed,
                "网卡命名",
                "尚未安装虚拟网卡,暂时无法校验名称。",
                details));
        }

        if (matched is not null)
        {
            return Task.FromResult(CheckResult.Ok(
                CheckId.TapNamed,
                "网卡命名",
                $"虚拟网卡「{expectedName}」存在,名称与配置一致。",
                details));
        }

        return Task.FromResult(CheckResult.Warn(
            CheckId.TapNamed,
            "网卡命名",
            $"配置指定的「{expectedName}」不存在,但系统里有 {tapLike.Count} 个 TAP 类网卡。",
            details,
            [
                $"可以直接使用现有网卡「{tapLike[0].Name}」,或让程序把它改名为「{expectedName}」(需要管理员权限)。",
                "TAP 网卡的连接名在系统语言变更或驱动重装后可能变化,所以启动前重新核对这一步是必要的。",
            ]));
    }
}
