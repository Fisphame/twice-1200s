namespace Twice1200.Core.Runtime;

/// <summary>
/// 被管理进程的运行状态。
/// <para>
/// 界面直接绑定这个状态机。<b>不要增加"未知"之外的中间态</b> ——
/// 隧道工具的麻烦几乎都来自"看起来在连、其实早断了"这种含糊状态,
/// 状态越少、越确定,用户越不容易被误导。
/// </para>
/// </summary>
public enum TunnelState
{
    /// <summary>未启动。</summary>
    Stopped,

    /// <summary>正在启动中(进程已创建,但还没确认进入工作状态)。</summary>
    Starting,

    /// <summary>运行中。</summary>
    Running,

    /// <summary>进程已自行退出。</summary>
    Exited,

    /// <summary>启动失败,或运行中出现错误。</summary>
    Failed,
}

/// <summary>
/// 一次进程运行的结果。
/// <para>
/// 刻意<b>不</b>引用 n2n 或 frp 的诊断类型 —— 保持这一层通用,
/// 具体产品的输出解读放在各自的 Tunnel 实现里。
/// </para>
/// </summary>
/// <param name="Started">是否成功创建了进程。</param>
/// <param name="WasKilled">是否是被我们主动终止的。</param>
/// <param name="ExitCode">退出码;未正常退出时为 <c>null</c>。</param>
/// <param name="Output">捕获到的全部输出(已脱敏),与实时事件内容一致。</param>
/// <param name="Failure">启动失败的原因;成功时为 <c>null</c>。</param>
public sealed record ProcessRunResult(
    bool Started,
    bool WasKilled,
    int? ExitCode,
    string Output,
    string? Failure)
{
    /// <summary>启动失败的快捷构造。</summary>
    public static ProcessRunResult Failed(string reason) => new(false, false, null, string.Empty, reason);
}

/// <summary>
/// 进程状态变化时的通知载荷。
/// </summary>
/// <param name="State">新状态。</param>
/// <param name="Detail">人可读的说明。</param>
/// <param name="ExitCode">仅在进入终止类状态时有值。</param>
public sealed record TunnelStateChange(TunnelState State, string Detail, int? ExitCode = null);
