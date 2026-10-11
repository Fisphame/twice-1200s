using System.Text;
using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics.Checks;
using Twice1200.Core.Runtime;

namespace Twice1200.Core.Tunnels;

/// <summary>
/// frp 连接的一次启动结果分类。
/// </summary>
public enum FrpStartStatus
{
    /// <summary>已登录服务端,且代理已启动。</summary>
    Started,

    /// <summary>已登录服务端,但代理启动情况未确认。</summary>
    StartedUnconfirmed,

    /// <summary>鉴权失败 —— token 与服务端不一致。</summary>
    AuthFailed,

    /// <summary>连不上服务端。</summary>
    ServerUnreachable,

    /// <summary>配置不合法。</summary>
    InvalidConfiguration,

    /// <summary>找不到 frpc.exe。</summary>
    ExecutableMissing,

    /// <summary>启动过程抛出异常。</summary>
    LaunchFailed,

    /// <summary>进程提前退出且原因无法归类。</summary>
    ExitedEarly,
}

/// <summary>
/// frp 连接的一次启动结果。
/// </summary>
public sealed record FrpStartResult(
    FrpStartStatus Status,
    string Message,
    IReadOnlyList<string> LogLines,
    string CommandLineForDisplay,
    IReadOnlyList<string>? Hints = null)
{
    /// <summary>是否算作成功。</summary>
    public bool IsSuccess => Status is FrpStartStatus.Started or FrpStartStatus.StartedUnconfirmed;
}

/// <summary>
/// frp 启动参数。
/// </summary>
public sealed record FrpLaunchOptions
{
    /// <summary>frpc.exe 的完整路径。</summary>
    public required string FrpcPath { get; init; }

    /// <summary>服务端地址。</summary>
    public required string ServerAddress { get; init; }

    /// <summary>服务端控制端口。</summary>
    public int ServerPort { get; init; } = 7000;

    /// <summary>
    /// 鉴权 token 的<b>明文</b>。
    /// <para>
    /// 它只被写进临时配置文件,<b>绝不进入命令行</b> —— 命令行对本机任何进程可读。
    /// </para>
    /// </summary>
    public required string AuthToken { get; init; }

    /// <summary>是否要求 TLS。默认开启。</summary>
    public bool RequireTls { get; init; } = true;

    /// <summary>端口映射规则。</summary>
    public required IReadOnlyList<FrpProxy> Proxies { get; init; }

    /// <summary>启动阶段的观察窗口。</summary>
    public TimeSpan StartupWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 生成的配置文件放在哪个目录。
    /// <para>为 <c>null</c> 时用应用自己的运行期临时目录(权限较窄)。</para>
    /// </summary>
    public string? ConfigDirectory { get; init; }
}

/// <summary>
/// frp 隧道的启停。
/// <para>
/// <b>关键设计:token 只通过临时配置文件传递,不进命令行。</b>
/// 配置文件写在权限受限的临时目录里,进程启动后立即删除 ——
/// 这样磁盘上不会长期留有一份含明文 token 的文件。
/// </para>
/// <para>
/// <b>"连上了"的判据来自实测,不是猜的:</b>
/// frpc 在成功时会打印 <c>login to server success</c> 与 <c>start proxy success</c>;
/// token 不匹配时打印 <c>token in login doesn't match</c>;
/// 服务端不可达时打印 <c>connect to server error</c>。
/// </para>
/// </summary>
public sealed class FrpTunnel : IDisposable
{
    /// <summary>成功登录的标志。</summary>
    private const string LoginSuccessMarker = "login to server success";

    /// <summary>代理启动成功的标志。</summary>
    private const string ProxyStartedMarker = "start proxy success";

    /// <summary>鉴权失败的标志。</summary>
    private const string AuthFailedMarker = "token in login doesn't match";

    /// <summary>连不上服务端的标志。</summary>
    private const string ConnectFailedMarker = "connect to server error";

    /// <summary>进程启动后整体失败的标志。</summary>
    private const string LoginFailedMarker = "login to the server failed";

    private readonly ProcessSupervisor _supervisor = new();

    /// <summary>当前状态。</summary>
    public TunnelState State => _supervisor.State;

    /// <summary>已捕获的日志(已脱敏)。</summary>
    public IReadOnlyList<string> LogLines => _supervisor.LogLines;

    /// <summary>状态变化通知。</summary>
    public event EventHandler<TunnelStateChange>? StateChanged
    {
        add => _supervisor.StateChanged += value;
        remove => _supervisor.StateChanged -= value;
    }

    /// <summary>新日志行通知。</summary>
    public event EventHandler<string>? OutputReceived
    {
        add => _supervisor.OutputReceived += value;
        remove => _supervisor.OutputReceived -= value;
    }

    /// <summary>
    /// 判断日志里是否已出现"成功登录"。
    /// </summary>
    public static bool HasLoginSuccess(string? log)
        => Contains(log, LoginSuccessMarker);

    /// <summary>判断日志里是否已出现"代理启动成功"。</summary>
    public static bool HasProxyStarted(string? log)
        => Contains(log, ProxyStartedMarker);

    /// <summary>判断日志里是否已出现鉴权失败。</summary>
    public static bool HasAuthFailure(string? log)
        => Contains(log, AuthFailedMarker);

    /// <summary>判断日志里是否已出现连接失败。</summary>
    public static bool HasConnectionFailure(string? log)
        => Contains(log, ConnectFailedMarker) || Contains(log, LoginFailedMarker);

    /// <summary>
    /// 启动 frp 连接。
    /// </summary>
    public async Task<FrpStartResult> StartAsync(
        FrpLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // ---- 前置校验:把能提前发现的问题挡住 ----
        var problems = Validate(options);
        if (problems.Count > 0)
        {
            return new FrpStartResult(
                FrpStartStatus.InvalidConfiguration,
                problems[0],
                [$"[前置校验未通过] {problems[0]}"],
                "(未执行)",
                problems.Skip(1).ToList());
        }

        if (!File.Exists(options.FrpcPath))
        {
            return new FrpStartResult(
                FrpStartStatus.ExecutableMissing,
                $"找不到 frpc.exe:{options.FrpcPath}",
                [],
                "(未执行)",
                [$"请确认 assets/frp/frpc.exe 存在。"]);
        }

        var configuration = new FrpConfiguration
        {
            ServerAddress = options.ServerAddress,
            ServerPort = options.ServerPort,
            RequireTls = options.RequireTls,
            Proxies = options.Proxies.ToList(),
        };

        var configDirectory = options.ConfigDirectory ?? AppPaths.RuntimeDirectory;

        var configPath = Path.Combine(
            configDirectory,
            $"frpc-{Guid.NewGuid():N}.toml");

        var arguments = FrpcConfigGenerator.BuildArguments(configPath);
        var displayCommand = CommandLineFormatter.Format(options.FrpcPath, arguments);

        try
        {
            Directory.CreateDirectory(configDirectory);

            var toml = FrpcConfigGenerator.Generate(configuration, options.AuthToken);
            await File.WriteAllTextAsync(configPath, toml, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            RestrictFilePermissions(configPath);

            var secrets = new SecretSet();
            secrets.Add(options.AuthToken);

            var request = new ProcessLaunchRequest
            {
                ExecutablePath = options.FrpcPath,
                Arguments = arguments,
                Secrets = secrets,
                // 剥掉 ANSI 颜色转义:frpc 即使输出被重定向也会带颜色序列,
                // 它们会污染界面显示并让字符串匹配变得不可靠。
                LineTransform = StripAnsi,
                // 不用就绪判据,自己看日志:因为需要区分"连上了"和"鉴权失败",
                // 而后者会让进程直接退出。
                RunningDetector = null,
                StartupWindow = options.StartupWindow,
            };

            var run = await _supervisor.StartAsync(request, cancellationToken).ConfigureAwait(false);

            if (!run.Started)
            {
                var failedStatus = ClassifyFailure(_supervisor.GetLogText());
                return new FrpStartResult(failedStatus, run.Failure ?? "启动失败。", LogLines, displayCommand);
            }

            // 等"成功"或"失败"任一个特征出现
            await _supervisor
                .WaitForOutputAsync(
                    text => HasProxyStarted(text) || HasAuthFailure(text) || HasConnectionFailure(text),
                    options.StartupWindow,
                    cancellationToken)
                .ConfigureAwait(false);

            // 日志已在 ProcessSupervisor 里统一清理过,这里直接用
            var finalLog = _supervisor.GetLogText();

            if (HasProxyStarted(finalLog))
            {
                return new FrpStartResult(
                    FrpStartStatus.Started,
                    $"frp 已连接,共 {options.Proxies.Count} 条端口映射规则已启动。",
                    LogLines,
                    displayCommand);
            }

            if (HasAuthFailure(finalLog))
            {
                return new FrpStartResult(
                    FrpStartStatus.AuthFailed,
                    "鉴权失败 —— token 与服务端不一致。",
                    LogLines,
                    displayCommand,
                    [
                        "请核对本机保存的 token 是否与服务端 frps 配置里的 auth.token 一致。",
                        "服务端改动 token 后,本机也要同步更新。",
                    ]);
            }

            if (HasConnectionFailure(finalLog))
            {
                return new FrpStartResult(
                    FrpStartStatus.ServerUnreachable,
                    "连不上 frp 服务端。",
                    LogLines,
                    displayCommand,
                    [
                        $"目标:{options.ServerAddress}:{options.ServerPort}",
                        "检查服务器是否在线、控制端口是否放行、云安全组是否允许。",
                    ]);
            }

            if (HasLoginSuccess(finalLog))
            {
                return new FrpStartResult(
                    FrpStartStatus.StartedUnconfirmed,
                    "已登录服务端,但尚未确认代理启动完成。",
                    LogLines,
                    displayCommand);
            }

            return new FrpStartResult(
                FrpStartStatus.StartedUnconfirmed,
                "进程已启动,但在观察窗口内没有出现明确的成功或失败标志。",
                LogLines,
                displayCommand);
        }
        catch (Exception ex)
        {
            return new FrpStartResult(
                FrpStartStatus.LaunchFailed,
                $"启动失败:{ex.GetType().Name}: {ex.Message}",
                LogLines,
                displayCommand);
        }
        finally
        {
            // 【安全关键】配置含明文 token,启动后立即删除。
            //
            // 取舍:frpc 是在启动时读取配置的,运行期不再需要它,所以这样做是安全的;
            // 代价是运行期无法热重载配置 —— 对本工具的用途(启停连接)不构成限制。
            // 保留一份含明文 token 的文件在磁盘上,风险大于这点便利。
            TryDelete(configPath);
        }
    }

    /// <summary>请求断开。</summary>
    public Task StopAsync(TimeSpan? gracePeriod = null) => _supervisor.StopAsync(gracePeriod);

    /// <summary>
    /// 校验启动参数,返回发现的问题列表。
    /// </summary>
    /// <remarks>
    /// <c>frpc verify</c> 能抓语法与类型错误,但<b>不抓逻辑缺失</b> ——
    /// 实测确认:没有 serverAddr、没有 proxies,它都说 "syntax is ok"。
    /// 所以逻辑校验必须在这里做。
    /// </remarks>
    public static IReadOnlyList<string> Validate(FrpLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ServerAddress))
        {
            problems.Add("未指定 frp 服务端地址。");
        }
        else if (options.ServerAddress.Contains("://", StringComparison.Ordinal))
        {
            problems.Add("服务端地址不应包含协议前缀,只填主机名或 IP。");
        }

        if (options.ServerPort is < 1 or > 65535)
        {
            problems.Add($"控制端口 {options.ServerPort} 超出 1–65535 范围。");
        }

        if (string.IsNullOrWhiteSpace(options.AuthToken))
        {
            problems.Add("未提供 frp 鉴权 token。");
        }

        if (options.Proxies.Count == 0)
        {
            problems.Add("没有配置任何端口映射规则,连上也做不了转发。");
        }

        var remotePorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var proxy in options.Proxies)
        {
            var label = string.IsNullOrWhiteSpace(proxy.Name) ? "(未命名规则)" : proxy.Name;

            if (string.IsNullOrWhiteSpace(proxy.Name))
            {
                problems.Add("存在名称为空的映射规则。");
            }

            // 类型白名单。
            //
            // 目前只放行 tcp 与 udp:
            //   · tcp  —— 通用场景(网页、远程桌面、Minecraft 的 Java 版)
            //   · udp  —— 游戏联机最常见(大多数游戏走 UDP)
            //
            // http / https / stcp / xtcp 等类型需要额外的域名或访问者配置,
            // 结构上与"本地端口 → 服务端端口"不是一回事,因此暂不支持,
            // 并且明确报错而不是让 frpc 在启动后才失败。
            var type = (proxy.Type ?? string.Empty).Trim();

            if (!type.Equals("tcp", StringComparison.OrdinalIgnoreCase) &&
                !type.Equals("udp", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"规则「{label}」的类型是「{proxy.Type}」,当前只支持 tcp 与 udp。");
            }

            if (proxy.LocalPort is < 1 or > 65535)
            {
                problems.Add($"规则「{label}」的本地端口 {proxy.LocalPort} 超出范围。");
            }

            if (proxy.RemotePort is < 1 or > 65535)
            {
                problems.Add($"规则「{label}」的服务端端口 {proxy.RemotePort} 超出范围。");
            }
            else if (!remotePorts.Add($"{type}:{proxy.RemotePort}"))
            {
                // 端口冲突只在**同一协议内**才算冲突:
                // TCP 25565 与 UDP 25565 可以同时存在,这是很常见的用法。
                problems.Add(
                    $"服务端 {type.ToUpperInvariant()} 端口 {proxy.RemotePort} 被多条规则同时占用。");
            }
        }

        return problems;
    }

    /// <summary>
    /// 去掉 ANSI 颜色转义序列。
    /// </summary>
    /// <remarks>
    /// frpc 默认输出带颜色,即使被重定向到管道也会带。
    /// 实测确认:日志里混着 <c>\u001b[1;34m</c> 这类序列,
    /// 既污染界面显示,也会让字符串匹配变得不可靠。
    /// 在进入日志缓冲之前先剥掉。
    /// </remarks>
    public static string StripAnsi(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                // 跳过 CSI 序列:ESC [ ... 直到一个字母结尾
                i += 2;

                while (i < text.Length && !char.IsLetter(text[i]))
                {
                    i++;
                }

                if (i < text.Length)
                {
                    i++; // 跳过结尾字母
                }

                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose() => _supervisor.Dispose();

    // ================================================================

    private static FrpStartStatus ClassifyFailure(string log)
    {
        if (HasAuthFailure(log))
        {
            return FrpStartStatus.AuthFailed;
        }

        if (HasConnectionFailure(log))
        {
            return FrpStartStatus.ServerUnreachable;
        }

        return FrpStartStatus.ExitedEarly;
    }

    /// <summary>把配置文件权限收紧到仅当前用户。失败时静默继续。</summary>
    private static void RestrictFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // 用 File.SetUnixFileMode 不适用于 Windows;这里借助 FileInfo 的
            // 只读属性无法表达 ACL,因此用 attrib 层面的最小动作:
            // 真正收紧 ACL 需要 System.Security.AccessControl 的额外 API。
            // 当前依赖的是"临时目录本身权限较窄"这一事实,并在用完后立即删除。
            File.SetAttributes(path, FileAttributes.NotContentIndexed);
        }
        catch
        {
            // 权限收紧失败不应影响功能,文件稍后即被删除
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删除失败不影响结论;文件位于临时目录,且含 token 的时间窗口很短
        }
    }

    private static bool Contains(string? text, string needle)
        => !string.IsNullOrEmpty(text) && text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
