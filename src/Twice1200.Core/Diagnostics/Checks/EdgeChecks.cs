using System.Text;
using Twice1200.Core.Net;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>
/// 检查虚拟网卡名会不会在传给 edge.exe 时出问题。
/// <para>
/// 这一项来自一次真实的排查:启动命令里的网卡名含空格,
/// 而参数拼接方式没有给它加引号,于是 <c>以太网 2</c> 变成 <c>以太网</c> 加一个多余的 <c>2</c>,
/// edge 报"找不到 TAP 网卡"。现象看起来像驱动故障,实际是参数问题。
/// </para>
/// <para>
/// 关键点在于:<b>这不是"中文名"的问题,而是"含空格"的问题。</b>
/// 英文默认名 <c>Local Area Connection 2</c> 同样会中招。
/// 所以这里检查的是"名字的形态",而不是"是不是中文"。
/// </para>
/// </summary>
public sealed class AdapterNameSafetyCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.TapNameSafety;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var configured = options.Configuration?.N2n.Tap;
        var name = configured?.AdapterName ?? "以太网 2";
        var fallback = configured?.FallbackAdapterName ?? "n2n-TAP";

        var details = new List<string>
        {
            $"配置的网卡名 : 「{name}」",
            $"字符长度     : {name.Length}",
            $"含空格       : {(name.Contains(' ') ? "是" : "否")}",
            $"含非 ASCII   : {(name.Any(c => c > 0x7F) ? "是" : "否")}",
            $"回退名称     : 「{fallback}」",
            "说明:网卡名会作为 edge.exe 的 -d 参数传递,其中含空格的名称必须正确加引号。",
        };

        // 列出系统里真实存在的 TAP 网卡名,供用户核对拼写
        var live = NetworkInterfaceInspector.FindTapLike();
        if (live.Count > 0)
        {
            details.Add("系统里实际存在的 TAP 类网卡:");
            foreach (var nic in live)
            {
                var mark = string.Equals(nic.Name, name, StringComparison.OrdinalIgnoreCase) ? "   ← 与配置一致" : string.Empty;
                details.Add($"  · 「{nic.Name}」{mark}");
            }
        }

        var issues = new List<string>();

        // ---- 致命:会破坏参数解析的字符 ----
        if (name.Contains('"'))
        {
            issues.Add("名称里含双引号。引号是参数分隔符本身,无法被正确转义,必须换一个名字。");
        }

        if (name.Contains('\\'))
        {
            issues.Add("名称里含反斜杠,在命令行解析中具有转义含义,建议换一个名字。");
        }

        if (name.Length == 0)
        {
            issues.Add("名称为空。");
        }
        else if (name != name.Trim())
        {
            issues.Add($"名称首尾有空白字符(实际为「{name}」)。这几乎总是配置写错,建议去掉。");
        }

        if (issues.Count > 0)
        {
            return Task.FromResult(CheckResult.Fail(
                CheckId.TapNameSafety,
                "网卡名安全性",
                issues[0],
                details,
                issues.Skip(1)
                    .Concat(["建议改用不含空格与特殊字符的纯 ASCII 名称,例如回退名称。"])
                    .ToList()));
        }

        // ---- 警告:含空格,需要正确加引号 ----
        // 这不是缺陷,只要调用方正确处理就行。但如果调用方漏了引号,
        // 现象会是"找不到 TAP 网卡",极易被误判为驱动问题 —— 所以值得提醒。
        if (name.Contains(' '))
        {
            return Task.FromResult(CheckResult.Warn(
                CheckId.TapNameSafety,
                "网卡名安全性",
                $"网卡名「{name}」含空格,必须以引号形式传给 edge.exe,否则会被拆成两个参数。",
                details,
                [
                    "这不是错误 —— 含空格的名称可以正常使用,前提是参数被正确引用。",
                    "历史教训:参数拼接漏掉引号时,edge 会报「Cannot find tap device」," +
                    "看起来像驱动或网卡故障,实际只是参数被空格拆开了。",
                    $"程序内部使用 ArgumentList 传递参数,会自动处理引号;此提示用于人工排查时参考。",
                ]));
        }

        // ---- 提示:含非 ASCII,在非中文代码页系统上可能有额外的编码风险 ----
        var codePage = SystemCodePage.Detect();
        if (name.Any(c => c > 0x7F) && codePage is not null && codePage != SystemCodePage.SimplifiedChinese)
        {
            return Task.FromResult(CheckResult.Warn(
                CheckId.TapNameSafety,
                "网卡名安全性",
                $"网卡名「{name}」含非 ASCII 字符,而系统代码页是 {codePage}。" +
                $"建议改用「{fallback}」以避免编码转换带来的额外风险。",
                details,
                ["引号问题与编码问题是两件独立的事,本项只覆盖「名字形态」这一层。"]));
        }

        // ---- 核对配置名是否真的存在 ----
        if (live.Count > 0 && !live.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(CheckResult.Warn(
                CheckId.TapNameSafety,
                "网卡名安全性",
                $"网卡名本身没有问题,但配置里的「{name}」在系统中不存在。",
                details,
                [$"系统里可用的名称是「{live[0].Name}」,请核对拼写(多一个空格也算不同)。"]));
        }

        return Task.FromResult(CheckResult.Ok(
            CheckId.TapNameSafety,
            "网卡名安全性",
            $"网卡名「{name}」不含会破坏参数解析的字符。",
            details));
    }
}

/// <summary>
/// 实际启动一次 <c>edge.exe</c>,从它的日志判断卡在哪一步。
/// <para>
/// 这是唯一可靠的 n2n 诊断方式:解析包、解析 DNS 都只是间接证据,
/// 而 edge 自己在哪一步失败、报什么错,是直接证据。
/// </para>
/// <para>
/// <b>本项默认关闭</b>,因为启动 edge 是一个真实动作(会尝试打开网卡)。
/// 探测刻意使用不存在的网卡名与社区名,因此不会改动任何真实网络配置。
/// </para>
/// </summary>
public sealed class EdgeConnectivityCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.NetEdgeProbe;

    /// <inheritdoc />
    public async Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        if (!options.ProbeEdge)
        {
            return CheckResult.Skip(
                CheckId.NetEdgeProbe,
                "edge 实测",
                "未启用实测(会真实启动一次 edge.exe,需要显式开启)。",
                details: ["要执行实测,请加上 --probe-edge。"]);
        }

        var edgePath = options.Assets.Resolve(Assets.AssetPaths.N2nEdge);
        if (edgePath is null || !File.Exists(edgePath))
        {
            return CheckResult.Fail(
                CheckId.NetEdgeProbe,
                "edge 实测",
                "找不到 edge.exe,无法实测。",
                details: [$"期望位置:{edgePath ?? "(资源目录未解析)"}"]);
        }

        var supernode = options.Configuration?.N2n.Supernode
                        ?? $"{options.SupernodeHost}:{options.SupernodePort}";

        // 刻意使用不存在的网卡名:这样 edge 会在"找网卡"这一步失败,
        // 既证明它确实能枚举设备,又不会配置任何真实网卡。
        var adapterName = EdgeProbeRunner.NonExistentAdapterName;
        var key = options.ProbeKey;

        // 说明密钥是从哪来的 —— 这句要能让用户判断"我到底有没有配上密钥"
        var keyHandle = options.Configuration?.N2n.Key.Handle;
        var keyOrigin = key is null
            ? "未提供 —— edge 将报告「加密被禁用」,这是预期行为"
            : string.IsNullOrWhiteSpace(keyHandle)
                ? "由命令行参数直接指定(仅用于测试)"
                : $"凭据库键「{keyHandle}」";

        var details = new List<string>
        {
            $"edge.exe   : {edgePath}",
            $"超级节点   : {supernode}",
            $"探针网卡名 : {adapterName}(刻意不存在,不会改动真实网卡)",
            $"探针社区名 : {EdgeProbeRunner.ProbeCommunity}",
            $"密钥来源   : {keyOrigin}",
            $"探测时长   : {options.EdgeProbeTimeout.TotalSeconds:F0} 秒",
        };

        var run = await EdgeProbeRunner.RunAsync(
            edgePath,
            adapterName,
            supernode,
            key,
            options.EdgeProbeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!run.Started)
        {
            return CheckResult.Fail(CheckId.NetEdgeProbe, "edge 实测", run.Failure ?? "无法启动 edge.exe。", details);
        }

        details.Add($"进程结果   : {(run.Exited ? $"自行退出(退出码 {run.ExitCode})" : "运行到超时后被终止")}");

        var diagnosis = EdgeProbeDiagnosis.Parse(run.Output);

        // 逐条列出它走到的位置,这是报告里信息量最大的部分
        details.Add("—— 启动流程 ——");
        details.Add($"  读取加密配置 : {(diagnosis.Cipher is null ? "未观察到" : $"{diagnosis.Cipher}")}" +
                    (diagnosis.EncryptionDisabled ? "  ← 加密被禁用,说明密钥没有生效!" : string.Empty));
        details.Add($"  枚举 TAP 设备: {(diagnosis.NoTapDevicesAtAll ? "失败 —— 一个都没枚举到" : "成功")}");
        details.Add($"  打开指定网卡 : {(diagnosis.DeviceOpened ? "成功" : diagnosis.NamedDeviceNotFound ? "失败 —— 名称不存在" : "未到达此步")}");
        details.Add($"  向超级节点要 IP: {(diagnosis.RequestedIpAssignment ? "是(未指定 -a 时走这条路径)" : "未走到")}");
        details.Add($"  启动 edge    : {(diagnosis.EdgeStarted ? "成功" : "未到达此步")}");
        details.Add($"  发起注册     : {(diagnosis.AttemptedRegistration ? "是" : "未到达此步")}");
        if (diagnosis.SupernodeSilent)
        {
            details.Add("  超级节点响应 : 无 —— edge 报告 supernode not responding");
        }

        if (diagnosis.ReportedDeviceName is not null)
        {
            details.Add($"  edge 收到的网卡名 : 「{diagnosis.ReportedDeviceName}」");
            if (!string.Equals(diagnosis.ReportedDeviceName, adapterName, StringComparison.Ordinal))
            {
                details.Add("  ⚠ 这个名称与传入的名称不一致 —— 说明参数在传递过程中被改变了!");
            }
        }

        // 原始日志:只保留前若干行,避免报告被刷屏
        var lines = run.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains("dropping Tx multicast", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count > 0)
        {
            details.Add($"—— edge 日志(共 {lines.Count} 行,显示前 {Math.Min(lines.Count, 12)} 行)——");
            foreach (var line in Redaction.ScrubSecrets(lines).Take(12))
            {
                details.Add("  " + line);
            }
        }

        // ---- 结论 ----
        // 顺序有讲究:先看"没权限"这类硬失败,再看"走到一半"的情况。
        if (diagnosis.NoTapDevicesAtAll || diagnosis.NamedDeviceNotFound)
        {
            return CheckResult.Fail(
                CheckId.NetEdgeProbe,
                "edge 实测",
                diagnosis.NoTapDevicesAtAll
                    ? "edge 枚举不到任何 TAP 设备,通常意味着当前权限不足。"
                    : "edge 能枚举到 TAP 设备,但打不开指定的网卡 —— 这一步失败同样指向权限不足。",
                details,
                [
                    "实测确认:edge.exe 需要管理员权限才能访问 TAP 网卡。",
                    "请以管理员身份重试;程序应以提权方式启动 edge。",
                    "若已提权仍失败,请对照上面「edge 收到的网卡名」检查名称是否被截断 —— " +
                    "参数拼接漏掉引号会让「以太网 2」变成「以太网」。",
                ]);
        }

        // 设备层没问题,但卡在与超级节点的交互上
        if (diagnosis.RequestedIpAssignment && !diagnosis.EdgeStarted)
        {
            return CheckResult.Fail(
                CheckId.NetEdgeProbe,
                "edge 实测",
                "本机配置正常,但超级节点没有分配虚拟 IP。",
                details,
                [
                    "关键结论:能走到「向超级节点要 IP」这一步,说明本地设备层是好的," +
                    "而且 edge 已经正确读到了 -d 参数(参数在打开设备之前就解析完成)。",
                    "问题在超级节点一侧:它没有回包。请检查服务器上 supernode 是否在运行、" +
                    "UDP 端口是否被安全组或防火墙放行。",
                    "这不是客户端故障,继续在本机排查不会有结果。",
                ]);
        }

        if (diagnosis.DeviceOpened || diagnosis.EdgeStarted)
        {
            return CheckResult.Ok(
                CheckId.NetEdgeProbe,
                "edge 实测",
                diagnosis.EdgeStarted
                    ? "edge 启动流程正常,已走到「启动完成」这一步。"
                    : "edge 启动流程正常,已走到「打开网卡」这一步。",
                details);
        }

        return CheckResult.Warn(
            CheckId.NetEdgeProbe,
            "edge 实测",
            "edge 的运行结果没有匹配到已知的成功或失败特征,请人工查看上面的日志。",
            details);
    }
}
