using Twice1200.Core.Runtime;

namespace Twice1200.Core.Diagnostics.Checks;

/// <summary>
/// 检查是否已有 n2n / frp 进程在运行。
/// <para>
/// 重复启动同一套隧道是最常见的"莫名其妙的故障"来源:
/// 两个 edge 抢同一块虚拟网卡,或者两个 frpc 用同一个 token 连同一个服务端,
/// 表现是连接时好时坏、日志互相穿插,而用户完全看不出原因。
/// 启动前先发现并提示,比事后排查便宜得多。
/// </para>
/// </summary>
public sealed class RuntimeProcessCheck : IEnvironmentCheck
{
    /// <inheritdoc />
    public string Id => CheckId.ProcN2n;

    /// <inheritdoc />
    public Task<CheckResult> RunAsync(DoctorOptions options, CancellationToken cancellationToken = default)
    {
        var edge = ProcessSnapshotter.FindByName("edge");
        var supernode = ProcessSnapshotter.FindByName("supernode");
        var frpc = ProcessSnapshotter.FindByName("frpc");
        var frps = ProcessSnapshotter.FindByName("frps");

        var details = new List<string>();
        var notable = new List<string>();

        Append(details, notable, edge, "n2n edge", "占用了虚拟网卡;重复启动会导致网卡冲突");
        Append(details, notable, supernode, "n2n supernode", "本机在跑超级节点");
        Append(details, notable, frpc, "frp 客户端", "重复启动会导致同一 token 建立多条连接");
        Append(details, notable, frps, "frp 服务端", "本机在跑 frp 服务端");

        if (notable.Count == 0)
        {
            return Task.FromResult(CheckResult.Ok(
                CheckId.ProcN2n,
                "运行中的进程",
                "没有发现 n2n / frp 进程在运行,环境干净。",
                details.Count > 0 ? details : ["未发现 edge、supernode、frpc、frps。"]));
        }

        return Task.FromResult(CheckResult.Warn(
            CheckId.ProcN2n,
            "运行中的进程",
            $"检测到 {notable.Count} 类相关进程正在运行。",
            details,
            [
                "启动连接前,请先确认这些进程是否是你自己之前启动的。",
                "残留进程会让新连接表现得莫名其妙,建议先结束它们再重新连接。",
            ]));

        static void Append(
            List<string> details,
            List<string> notable,
            IReadOnlyList<ProcessSnapshot> processes,
            string label,
            string consequence)
        {
            if (processes.Count == 0)
            {
                return;
            }

            notable.Add(label);

            foreach (var process in processes)
            {
                details.Add($"{label} — PID {process.Id}");
                if (process.ExecutablePath is not null && process.ExecutablePath != string.Empty)
                {
                    details.Add($"    路径: {process.ExecutablePath}");
                }
            }

            details.Add($"    ↑ {consequence}");
        }
    }
}
