using System.IO;
using System.Text;
using System.Windows;
using Twice1200.Core.Assets;
using Twice1200.Core.Configuration;
using Twice1200.Core.Diagnostics;
using Twice1200.Core.Runtime;
using Twice1200.Core.Security;
using Twice1200.Core.Tap;
using Twice1200.Core.Tunnels;

namespace Twice1200.App;

/// <summary>
/// 主窗口。
/// <para>
/// 刻意把逻辑直接写在代码后置文件里,而不是引入 MVVM 框架。
/// 理由:界面层目前只做"把已有的 Core 能力接起来"这一件事,
/// 而 Core 里的模块(<see cref="N2nTunnel"/> / <see cref="TapAdapterManager"/> /
/// <see cref="CredentialStore"/> / <see cref="DoctorRunner"/>)都已经是可独立测试的。
/// 多一层绑定框架只会让第一次跑通变得更难。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 默认虚拟网卡名。
    /// <para>
    /// 刻意用纯 ASCII 名,而不是中文默认名。理由:非 ASCII 名在 n2n 工具链上
    /// 有已知风险(编码转换、参数引用、日志可读性),而 n2n 本身要求纯 ASCII。
    /// 项目也已为零中文代码页系统准备了同名回退值,属受支持的配置。
    /// </para>
    /// </summary>
    private const string DefaultAdapterName = "n2n-TAP";

    // 这里刻意不再有"默认超级节点""默认虚拟 IP"。
    //
    // 早先它们是硬编码的具体地址(部署者自己的服务器),而界面会把它预填在输入框里。
    // 那样别人拿到这个工具,一打开看到的就是陌生人的服务器,很可能直接点「连接」。
    // 虚拟网段与 frp 服务端同理。
    //
    // 现在这些字段一律留空,由用户在向导/界面里填写 —— 地址属于部署信息,
    // 不该由程序替用户猜。

    private readonly AssetLocator _assets = AssetLocator.Resolve();
    private N2nTunnel? _tunnel;
    private FrpTunnel? _frpTunnel;
    private bool _busy;

    /// <summary>
    /// frp 映射规则表。
    /// <para>
    /// 支持多条规则共存。实测确认 <c>tcp</c> 与 <c>udp</c> 可以共用同一个端口号,
    /// 因此端口冲突只在同一协议内判定。
    /// </para>
    /// </summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<FrpProxy> _frpRules = [];

    /// <summary>构造窗口。</summary>
    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    // ================================================================
    //  生命周期
    // ================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var elevated = N2nTunnel.IsElevated();
        ElevationText.Text = elevated ? "当前:管理员" : "当前:标准用户";
        ElevationText.ToolTip = elevated
            ? "可以执行需要特权的操作。"
            : "连接时需要管理员权限 —— edge.exe 在标准权限下无法访问 TAP 虚拟网卡。";

        AppendLog("Twice1200 界面已启动。");
        AppendLog($"程序目录 : {AppContext.BaseDirectory}");
        AppendLog($"资源目录 : {_assets.Root ?? "(未找到)"}");
        AppendLog($"数据目录 : {AppPaths.ConfigurationDirectory}");
        AppendLog($"权限     : {(elevated ? "管理员" : "标准用户")}");

        // 令牌形状:这是排查"ACL 正常却写不进去"的关键数据。
        // 完整性级别被降低、受限令牌、AppContainer 都会导致 Access Denied,
        // 而任何 ACL 查询都看不出来 —— 所以让程序自己报出来,不靠外部工具推断。
        try
        {
            AppendLog("");
            foreach (var line in TokenInspector.Describe().Split('\n'))
            {
                AppendLog("  " + line.TrimEnd('\r'));
            }
        }
        catch (Exception ex)
        {
            AppendLog($"  (令牌形状读取失败:{ex.GetType().Name})");
        }

        // 数据目录不可写时明确告知。刻意不自动换位置 ——
        // 早先的自动回退会把数据写进 bin\Debug\... 这类会被清理的目录,
        // 用户的凭据就这样无声消失。数据的去向必须由用户决定。
        if (!AppPaths.IsConfigurationDirectoryWritable())
        {
            Title += "  ⚠ 数据目录不可写";
            AppendLog("");
            foreach (var line in AppPaths.DescribeUnwritableDataDirectory().Split('\n'))
            {
                AppendLog("  " + line.TrimEnd('\r'));
            }
        }

        if (!_assets.Found)
        {
            AppendLog("");
            AppendLog("[!] 未找到资源目录 —— 无法定位 edge.exe 与 TAP 驱动安装包。");
            AppendLog($"    期望在程序目录旁存在 {AssetLocator.AssetDirectoryName}/。");
        }

        AppendLog("");

        FrpRulesGrid.ItemsSource = _frpRules;

        RefreshConfigFromDisk();
        RefreshKeyState();
        RefreshFrpState();

        SetStatus("就绪。建议先点「环境体检」确认环境。");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            _tunnel?.Dispose();
            _frpTunnel?.Dispose();
        }
        catch
        {
            // 退出时不再报错
        }
    }

    // ================================================================
    //  配置与状态
    // ================================================================

    /// <summary>刷新"frp token 是否已保存"的提示。不显示任何 token 内容。</summary>
    private void RefreshFrpState()
    {
        var store = new CredentialStore(AppPaths.SecretsFile);
        var handle = CredentialHandles.FrpAuthToken;

        if (!store.TryGetSecret(handle, out var token, out _))
        {
            FrpTokenStateText.Text = $"尚未保存 token(键「{handle}」)。输入后点右侧「保存 token」。";
            return;
        }

        FrpTokenStateText.Text =
            $"已保存 token:长度 {token!.Length},指纹 {Redaction.Fingerprint(token)}。";
    }

    // ================================================================
    //  按键操作:保存 frp token
    // ================================================================

    private void OnSaveFrpToken(object sender, RoutedEventArgs e)
    {
        var token = FrpTokenBox.Password;

        if (string.IsNullOrEmpty(token))
        {
            MessageBox.Show(this, "请先输入 token。", "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var store = new CredentialStore(AppPaths.SecretsFile);
        var result = store.SetSecret(CredentialHandles.FrpAuthToken, token);

        if (!result.IsSuccess)
        {
            AppendLog($"[×] 保存 token 失败:{result.Message}");
            MessageBox.Show(this, result.Message, "Twice1200 —— 保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        FrpTokenBox.Clear();

        AppendLog($"[+] {result.Message}");
        RefreshFrpState();
        SetStatus("frp token 已保存(DPAPI 加密)。");
    }

    // ================================================================
    //  按键操作:frp 预览配置 / 检测服务端
    // ================================================================

    private void OnFrpPreview(object sender, RoutedEventArgs e)
    {
        var options = TryBuildFrpOptions(out var problem);
        if (options is null)
        {
            AppendLog("[×] " + problem);
            SetStatus(problem);
            return;
        }

        var toml = FrpcConfigGenerator.Generate(
            new FrpConfiguration
            {
                ServerAddress = options.ServerAddress,
                ServerPort = options.ServerPort,
                RequireTls = options.RequireTls,
                Proxies = options.Proxies.ToList(),
            },
            options.AuthToken);

        AppendLog("");
        AppendLog("── 将生成的 frpc 配置(仅预览,不写文件)──");

        foreach (var line in toml.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');

            // token 行打码:它在磁盘上必须是明文(frpc 要读),但屏幕上不必显示
            if (trimmed.StartsWith("auth.token", StringComparison.OrdinalIgnoreCase))
            {
                AppendLog($"  auth.token = \"[已打码 — {Redaction.Describe(options.AuthToken)}]\"");
            }
            else
            {
                AppendLog("  " + trimmed);
            }
        }

        AppendLog("");
        AppendLog("  说明:token 只写进临时配置文件,不进命令行;进程启动后该文件立即删除。");
        SetStatus("配置预览已输出到日志。");
    }

    private async void OnFrpTestServer(object sender, RoutedEventArgs e)
    {
        var host = FrpServerBox.Text.Trim();
        var portText = FrpPortBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, out var port))
        {
            MessageBox.Show(this, "请填写服务端地址与合法的控制端口。", "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync($"正在检测 {host}:{port}…", async () =>
        {
            var probe = await Twice1200.Core.Net.NetworkProbes
                .TcpAsync(host, port, TimeSpan.FromSeconds(6))
                .ConfigureAwait(true);

            AppendLog("");
            AppendLog($"── 服务端检测 {host}:{port} ──");
            AppendLog($"  结果 : {probe.Status}");
            AppendLog($"  说明 : {probe.Message}");
            AppendLog($"  耗时 : {probe.Elapsed.TotalMilliseconds:F0} ms");

            SetStatus(probe.Status == Twice1200.Core.Net.ProbeStatus.Reachable
                ? "服务端控制端口可达。"
                : $"服务端检测:{probe.Message}");
        }).ConfigureAwait(true);
    }

    // ================================================================
    //  按键操作:frp 启动 / 停止
    // ================================================================

    private async void OnFrpConnect(object sender, RoutedEventArgs e)
    {
        var options = TryBuildFrpOptions(out var problem);
        if (options is null)
        {
            MessageBox.Show(this, problem, "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunBusyAsync("正在启动 frp…", async () =>
        {
            TrySaveConfiguration(BuildConfigurationFromUi(), quiet: true);

            _frpTunnel?.Dispose();
            _frpTunnel = new FrpTunnel();
            _frpTunnel.StateChanged += (_, change) => RunOnUiThread(() => SetState(Describe(change.State)));
            _frpTunnel.OutputReceived += (_, line) => RunOnUiThread(() => AppendLog("  [frpc] " + line));

            AppendLog("");
            AppendLog($"正在启动 frpc(服务端 {options.ServerAddress}:{options.ServerPort})…");

            var result = await _frpTunnel.StartAsync(options).ConfigureAwait(true);

            AppendLog(result.IsSuccess ? $"[+] {result.Message}" : $"[×] {result.Message}");

            if (result.Hints is { Count: > 0 })
            {
                AppendLog("");
                foreach (var hint in result.Hints)
                {
                    AppendLog("    · " + hint);
                }
            }

            if (result.IsSuccess)
            {
                SetState("frp 已连接");
                FrpDisconnectButton.IsEnabled = true;
                AppendLog("");
                AppendLog($"    服务端对公网暴露的端口:{string.Join(", ", options.Proxies.Select(p => p.RemotePort))}");
            }
            else
            {
                SetState("frp 未连接");
            }

            SetStatus(result.Message);
        }).ConfigureAwait(true);
    }

    private async void OnFrpDisconnect(object sender, RoutedEventArgs e)
    {
        var tunnel = _frpTunnel;
        if (tunnel is null)
        {
            SetState("frp 未连接");
            return;
        }

        await RunBusyAsync("正在停止 frp…", async () =>
        {
            await tunnel.StopAsync().ConfigureAwait(true);
            AppendLog("[+] frp 已停止。");
            SetState("未连接");
            SetStatus("frp 已停止。");
            FrpDisconnectButton.IsEnabled = false;
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 从界面控件组装 frp 启动参数。失败时返回 <c>null</c> 并给出原因。
    /// </summary>
    private FrpLaunchOptions? TryBuildFrpOptions(out string problem)
    {
        problem = string.Empty;

        var server = FrpServerBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(server))
        {
            problem = "请填写 frp 服务端地址。";
            return null;
        }

        if (!int.TryParse(FrpPortBox.Text.Trim(), out var port))
        {
            problem = "控制端口必须是数字。";
            return null;
        }

        // 规则直接取自表格。端口合法性交给 FrpTunnel.Validate 统一判定,
        // 这里只负责装配 —— 校验逻辑只允许有一处,否则两边迟早不一致。
        var rules = FrpRulesGrid.Items
            .OfType<FrpProxy>()
            .Select(p => new FrpProxy
            {
                Name = (p.Name ?? string.Empty).Trim(),
                Type = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type.Trim(),
                LocalIp = string.IsNullOrWhiteSpace(p.LocalIp) ? "127.0.0.1" : p.LocalIp.Trim(),
                LocalPort = p.LocalPort,
                RemotePort = p.RemotePort,
            })
            .ToList();

        if (rules.Count == 0)
        {
            problem = "请至少添加一条端口映射规则。";
            return null;
        }

        var store = new CredentialStore(AppPaths.SecretsFile);
        if (!store.TryGetSecret(CredentialHandles.FrpAuthToken, out var token, out var credentialResult))
        {
            problem = $"没有可用的 token。\n\n{credentialResult.Message}\n\n" +
                      "请在上方「鉴权 token」处输入并点击「保存 token」。";
            return null;
        }

        var options = new FrpLaunchOptions
        {
            FrpcPath = _assets.Resolve(AssetPaths.Frpc) ?? "frpc.exe",
            ServerAddress = server,
            ServerPort = port,
            AuthToken = token!,
            RequireTls = FrpTlsCheck.IsChecked != false,
            Proxies = rules,
        };

        // 逻辑校验必须在这里做:实测确认 frpc verify 不抓逻辑缺失
        // (没有 serverAddr、没有 proxies 它都说 syntax is ok)。
        var problems = FrpTunnel.Validate(options);
        if (problems.Count > 0)
        {
            problem = string.Join("\n", problems);
            return null;
        }

        return options;
    }

    /// <summary>从磁盘读取配置填充界面。读不到就保留默认值。</summary>
    private void RefreshConfigFromDisk()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigurationFile))
            {
                AppendLog("尚无配置文件,界面使用默认值。");
                return;
            }

            var configuration = AppConfiguration.Parse(
                File.ReadAllText(AppPaths.ConfigurationFile, Encoding.UTF8));

            AdapterNameBox.Text = configuration.N2n.Tap.AdapterName;
            SupernodeBox.Text = configuration.N2n.Supernode;
            CommunityBox.Text = configuration.N2n.Community;

            if (!string.IsNullOrWhiteSpace(configuration.N2n.AssignedIp))
            {
                VirtualIpBox.Text = configuration.N2n.AssignedIp;
            }

            // frp 侧
            if (!string.IsNullOrWhiteSpace(configuration.Frp.ServerAddress))
            {
                FrpServerBox.Text = configuration.Frp.ServerAddress;
            }

            FrpPortBox.Text = configuration.Frp.ServerPort.ToString();
            FrpTlsCheck.IsChecked = configuration.Frp.RequireTls;

            // 全部规则都装进表格,不再只取第一条
            _frpRules.Clear();

            foreach (var proxy in configuration.Frp.Proxies)
            {
                _frpRules.Add(new FrpProxy
                {
                    Name = proxy.Name,
                    Type = string.IsNullOrWhiteSpace(proxy.Type) ? "tcp" : proxy.Type,
                    LocalIp = string.IsNullOrWhiteSpace(proxy.LocalIp) ? "127.0.0.1" : proxy.LocalIp,
                    LocalPort = proxy.LocalPort,
                    RemotePort = proxy.RemotePort,
                });
            }

            RefreshFrpRuleSummary();

            AppendLog($"已读取配置:{AppPaths.ConfigurationFile}");
        }
        catch (Exception ex)
        {
            AppendLog($"[!] 读取配置失败:{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>刷新"密码是否已保存"的提示。不显示任何密码内容。</summary>
    private void RefreshKeyState()
    {
        var store = new CredentialStore(AppPaths.SecretsFile);
        var handle = CredentialHandles.N2nKey;

        if (!store.TryGetSecret(handle, out var secret, out _))
        {
            KeyStateText.Text = $"尚未保存密码(键「{handle}」)。输入后点右侧「保存密码」。";
            return;
        }

        // 只显示长度与指纹:足以确认"是不是它",无法还原内容
        KeyStateText.Text =
            $"已保存密码:长度 {secret!.Length},指纹 {Redaction.Fingerprint(secret)}。" +
            "指纹可用于核对其它机器上填的是否一致。";
    }

    // ================================================================
    //  按键操作:保存密码
    // ================================================================

    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        var secret = KeyBox.Password;

        if (string.IsNullOrEmpty(secret))
        {
            MessageBox.Show(this, "请先输入密码。", "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 强度检查:判定说明里不含密码内容
        var weakness = ConfigurationValidator.DescribeWeakness(secret);
        if (weakness is not null)
        {
            var answer = MessageBox.Show(
                this,
                $"密码强度存在问题:\n\n{weakness}\n\n仍要保存吗?",
                "Twice1200 —— 密码强度提示",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                SetStatus("已取消保存。");
                return;
            }
        }

        var store = new CredentialStore(AppPaths.SecretsFile);
        var result = store.SetSecret(CredentialHandles.N2nKey, secret);

        if (!result.IsSuccess)
        {
            AppendLog($"[×] 保存密码失败:{result.Message}");
            MessageBox.Show(this, result.Message, "Twice1200 —— 保存失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // 立刻清掉输入框里的内容 —— 它已经没有继续留在内存界面里的理由
        KeyBox.Clear();

        AppendLog($"[+] {result.Message}");
        RefreshKeyState();
        SetStatus("密码已保存(DPAPI 加密)。");
    }

    // ================================================================
    //  按键操作:环境体检
    // ================================================================

    private async void OnRunDoctor(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("正在体检…", async () =>
        {
            var configuration = BuildConfigurationFromUi();

            // 顺便把界面上的值写进配置文件,这样体检读到的就是用户实际填的内容。
            // 这是有意的:否则"界面显示 A、体检检查 B"会让人困惑。
            TrySaveConfiguration(configuration, quiet: true);

            var options = new DoctorOptions
            {
                Assets = _assets,
                Configuration = configuration,
                ConfigurationPath = AppPaths.ConfigurationFile,
                SupernodeHost = HostOf(configuration.N2n.Supernode, "supernode.invalid"),
                SupernodePort = PortOf(configuration.N2n.Supernode, 7777),
                DesiredVirtualIp = VirtualIpBox.Text.Trim(),
            };

            var report = await DoctorRunner.BuildDefault().RunAsync(options).ConfigureAwait(true);

            AppendLog("");
            AppendLog("═══ 环境体检 ═══");
            foreach (var line in report.ToText(verbose: true).Split('\n'))
            {
                AppendLog(line.TrimEnd('\r'));
            }

            SetStatus($"体检完成:通过 {report.OkCount} / 警告 {report.WarnCount} / " +
                      $"失败 {report.FailCount} / 跳过 {report.SkipCount}。");

            // 体检有失败项时,不要弹窗打断 —— 结论已经写在日志里了
            if (report.FailCount > 0)
            {
                AppendLog("");
                AppendLog("[!] 存在失败项。若与网卡或驱动有关,可点击「准备环境」让程序自动处理。");
            }
        }).ConfigureAwait(true);
    }

    // ================================================================
    //  按键操作:准备环境
    // ================================================================

    private async void OnPrepareEnvironment(object sender, RoutedEventArgs e)
    {
        var adapterName = AdapterNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(adapterName))
        {
            MessageBox.Show(this, "请先填写虚拟网卡名。", "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!N2nTunnel.IsElevated())
        {
            MessageBox.Show(
                this,
                "准备环境需要管理员权限 —— 安装驱动、创建或重命名虚拟网卡都属于特权操作。\n\n" +
                "请以管理员身份重新启动本程序,然后重试。\n\n" +
                "(命令行版本会自动请求提权,界面版暂时需要你手动以管理员身份启动。)",
                "Twice1200 —— 需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            AppendLog("[!] 准备环境需要管理员权限,当前是标准用户。");
            return;
        }

        await RunBusyAsync("正在准备虚拟网卡…", async () =>
        {
            var progress = new Progress<string>(message =>
                RunOnUiThread(() => AppendLog("    " + message)));

            var result = await new TapAdapterManager()
                .EnsureAdapterAsync(adapterName, _assets, progress)
                .ConfigureAwait(true);

            AppendLog("");
            AppendLog(result.IsSuccess ? $"[+] {result.Message}" : $"[×] {result.Message}");

            if (result.CommandLine is not null)
            {
                AppendLog($"    命令:{result.CommandLine}");
            }

            if (result.Output is { Count: > 0 })
            {
                AppendLog("    输出:");
                foreach (var line in result.Output.TakeLast(15))
                {
                    AppendLog("      " + line);
                }
            }

            SetStatus(result.Message);
        }).ConfigureAwait(true);
    }

    // ================================================================
    //  按键操作:连接 / 断开
    // ================================================================

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var adapterName = AdapterNameBox.Text.Trim();
        var community = CommunityBox.Text.Trim();
        var supernode = SupernodeBox.Text.Trim();
        var virtualIp = VirtualIpBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(community))
        {
            MessageBox.Show(this,
                "请先填写网络组名。\n\n组名与密码决定谁能加入这个网络,必须在所有组员之间保持一致。",
                "Twice1200", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 密钥来源:凭据库。界面不保存明文,也不把它传进命令行。
        var store = new CredentialStore(AppPaths.SecretsFile);
        if (!store.TryGetSecret(CredentialHandles.N2nKey, out var key, out var keyResult))
        {
            MessageBox.Show(this,
                $"没有可用的网络密码。\n\n{keyResult.Message}\n\n" +
                "请在上方「网络密码」处输入并点击「保存密码」。",
                "Twice1200", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await RunBusyAsync("正在连接…", async () =>
        {
            // 把界面上的值落盘,便于下次启动恢复
            TrySaveConfiguration(BuildConfigurationFromUi(), quiet: true);

            _tunnel?.Dispose();
            _tunnel = new N2nTunnel();
            _tunnel.StateChanged += OnTunnelStateChanged;
            _tunnel.OutputReceived += OnTunnelOutput;

            var options = new N2nLaunchOptions
            {
                EdgePath = _assets.Resolve(AssetPaths.N2nEdge) ?? "edge.exe",
                AdapterName = adapterName,
                Community = community,
                Supernode = supernode,
                AssignedIp = string.IsNullOrWhiteSpace(virtualIp) ? null : virtualIp,
                Key = key,
            };

            SetState("连接中");
            AppendLog("");
            AppendLog($"正在启动 edge(超级节点 {supernode})…");

            var result = await _tunnel.StartAsync(options).ConfigureAwait(true);

            AppendLog(result.IsSuccess ? $"[+] {result.Message}" : $"[×] {result.Message}");

            if (!result.IsSuccess)
            {
                AppendLog($"    执行的命令:{result.CommandLineForDisplay}");

                // 提示逐行输出:多行字符串塞进一行日志会破坏可读性
                if (result.Hints is { Count: > 0 })
                {
                    AppendLog("");
                    foreach (var hint in result.Hints)
                    {
                        AppendLog("    · " + hint);
                    }
                }

                ExplainFailure(result.Status);
                SetState("未连接");
                SetStatus(result.Message);
                return;
            }

            // 真正"连上了"的判据是握手,而不是进程活着
            SetStatus("已启动,等待与超级节点握手…");

            var handshake = await _tunnel
                .WaitForHandshakeAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(true);

            if (handshake)
            {
                AppendLog("[+] 握手成功 —— 已与超级节点建立通信。");
                SetState("已连接");
                SetStatus("连接正常。");
                DisconnectButton.IsEnabled = true;
            }
            else
            {
                AppendLog("[×] 未完成握手 —— 超级节点没有回包。");
                AppendLog("");
                AppendLog("    下一步建议:点击「本机自测」,在本机起一个超级节点并连它。");
                AppendLog("    本机能通、远端不通  →  服务端问题,继续在本机排查不会有结果。");
                AppendLog("    本机也不通          →  客户端问题,请先跑一次「环境体检」。");
                SetState("已启动但未握手");
                SetStatus("未能与超级节点握手。");
                DisconnectButton.IsEnabled = true;
            }
        }).ConfigureAwait(true);
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        var tunnel = _tunnel;
        if (tunnel is null)
        {
            SetState("未连接");
            return;
        }

        await RunBusyAsync("正在断开…", async () =>
        {
            await tunnel.StopAsync().ConfigureAwait(true);
            AppendLog("[+] 已断开。");
            SetState("未连接");
            SetStatus("已断开。");
            DisconnectButton.IsEnabled = false;
        }).ConfigureAwait(true);
    }

    // ================================================================
    //  按键操作:本机自测
    // ================================================================

    private async void OnLocalVerify(object sender, RoutedEventArgs e)
    {
        var adapterName = AdapterNameBox.Text.Trim();
        var virtualIp = VirtualIpBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(virtualIp))
        {
            // 这一条不是形式主义:不指定 IP 时 edge 会接受超级节点分配的地址
            // 并写进虚拟网卡,从而改动用户现有的网络配置。
            MessageBox.Show(this,
                "本机自测必须指定「本机虚拟 IP」。\n\n" +
                "原因:不指定时,edge 会接受超级节点分配的地址并写入虚拟网卡,\n" +
                "      这会改动你现有的网络配置。诊断功能不应有这样的副作用。",
                "Twice1200", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var store = new CredentialStore(AppPaths.SecretsFile);
        store.TryGetSecret(CredentialHandles.N2nKey, out var key, out _);

        await RunBusyAsync("本机自测中…", async () =>
        {
            // 每一次自测用不同的社区名,避免与之前的运行互相干扰
            var community = $"ui-probe-{DateTime.Now:HHmmss}";

            LocalSupernode? supernode = null;

            try
            {
                var supernodePath = _assets.Resolve(AssetPaths.N2nSupernode);
                if (string.IsNullOrWhiteSpace(supernodePath) || !File.Exists(supernodePath))
                {
                    AppendLog("[×] 找不到 supernode.exe,无法做本机自测。");
                    SetStatus("本机自测失败:缺少 supernode.exe。");
                    return;
                }

                supernode = new LocalSupernode();
                supernode.OutputReceived += (_, line) => RunOnUiThread(() => AppendLog("  [supernode] " + line));

                var port = 7777;
                AppendLog("");
                AppendLog($"═══ 本机自测(超级节点 127.0.0.1:{port})═══");

                if (!await supernode.StartAsync(supernodePath, port).ConfigureAwait(true))
                {
                    AppendLog($"[×] 本机超级节点启动失败 —— 端口 {port} 可能已被占用。");
                    foreach (var line in supernode.LogLines.TakeLast(10))
                    {
                        AppendLog("    " + line);
                    }

                    SetStatus("本机自测失败:超级节点无法启动。");
                    return;
                }

                AppendLog("[+] 超级节点已启动。");

                using var tunnel = new N2nTunnel();
                tunnel.OutputReceived += (_, line) => RunOnUiThread(() => AppendLog("  [edge] " + line));

                var result = await tunnel.StartAsync(new N2nLaunchOptions
                {
                    EdgePath = _assets.Resolve(AssetPaths.N2nEdge) ?? "edge.exe",
                    AdapterName = adapterName,
                    Community = community,
                    Supernode = $"127.0.0.1:{port}",
                    AssignedIp = virtualIp,
                    Key = key,
                }).ConfigureAwait(true);

                AppendLog(result.IsSuccess ? $"[+] {result.Message}" : $"[×] {result.Message}");

                if (!result.IsSuccess)
                {
                    ExplainFailure(result.Status);
                    SetStatus("本机自测失败 —— 问题在客户端一侧。");
                    return;
                }

                var handshake = await tunnel.WaitForHandshakeAsync(TimeSpan.FromSeconds(20))
                    .ConfigureAwait(true);

                if (handshake)
                {
                    AppendLog("[+] 握手成功。");
                    AppendLog("");
                    AppendLog("[√] 本机回环验证通过:客户端功能正常。");
                    AppendLog("    这条结论可以直接用于判断将来的故障是在客户端还是服务端。");
                    SetStatus("本机自测通过:客户端功能正常。");
                }
                else
                {
                    AppendLog("[×] 本机回环也没能握手 —— 问题在客户端一侧。");
                    SetStatus("本机自测失败 —— 问题在客户端一侧。");
                }
            }
            finally
            {
                if (supernode is not null)
                {
                    await supernode.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                    supernode.Dispose();
                    AppendLog("  (已停止本机超级节点)");
                }
            }
        }).ConfigureAwait(true);
    }

    // ================================================================
    //  按键操作:日志
    // ================================================================

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(LogBox.Text))
            {
                SetStatus("日志为空,没有可复制的内容。");
                return;
            }

            Clipboard.SetText(LogBox.Text);
            SetStatus("日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败:{ex.Message}");
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        LogSummaryText.Text = string.Empty;
        SetStatus("日志已清空。");
    }

    // ================================================================
    //  内部
    // ================================================================

    /// <summary>把界面上的值组装成配置对象。</summary>
    private AppConfiguration BuildConfigurationFromUi()
    {
        var configuration = AppConfiguration.CreateTemplate();

        configuration.N2n.Tap.AdapterName = AdapterNameBox.Text.Trim();
        configuration.N2n.Supernode = SupernodeBox.Text.Trim();
        configuration.N2n.Community = CommunityBox.Text.Trim();

        var virtualIp = VirtualIpBox.Text.Trim();
        configuration.N2n.AssignedIp = string.IsNullOrWhiteSpace(virtualIp) ? null : virtualIp;

        // 密钥引用固定指向标准键名 —— 界面不保存明文
        configuration.N2n.Key = SecretReference.Dpapi(CredentialHandles.N2nKey);
        configuration.Frp.AuthToken = SecretReference.Dpapi(CredentialHandles.FrpAuthToken);

        // frp 侧:把界面上的值也落进配置,便于下次启动恢复
        configuration.Frp.ServerAddress = FrpServerBox.Text.Trim();

        if (int.TryParse(FrpPortBox.Text.Trim(), out var frpPort))
        {
            configuration.Frp.ServerPort = frpPort;
        }

        configuration.Frp.RequireTls = FrpTlsCheck.IsChecked != false;

        // 把表格里的全部规则落盘,保留类型与本地 IP
        var rules = FrpRulesGrid.Items
            .OfType<FrpProxy>()
            .Select(p => new FrpProxy
            {
                Name = (p.Name ?? string.Empty).Trim(),
                Type = string.IsNullOrWhiteSpace(p.Type) ? "tcp" : p.Type.Trim(),
                LocalIp = string.IsNullOrWhiteSpace(p.LocalIp) ? "127.0.0.1" : p.LocalIp.Trim(),
                LocalPort = p.LocalPort,
                RemotePort = p.RemotePort,
            })
            .ToList();

        if (rules.Count > 0)
        {
            configuration.Frp.Proxies = rules;
        }

        return configuration;
    }

    // ================================================================
    //  frp 映射规则(增删)
    // ================================================================

    private void OnFrpAddRule(object sender, RoutedEventArgs e)
    {
        // 新规则的端口给一个中性的起点,而不是任何具体服务的端口
        var next = 10000 + _frpRules.Count;

        var rule = new FrpProxy
        {
            Name = $"rule{_frpRules.Count + 1}",
            Type = "tcp",
            LocalIp = "127.0.0.1",
            LocalPort = next,
            RemotePort = next,
        };

        _frpRules.Add(rule);
        FrpRulesGrid.SelectedItem = rule;
        RefreshFrpRuleSummary();
    }

    private void OnFrpRemoveRule(object sender, RoutedEventArgs e)
    {
        if (FrpRulesGrid.SelectedItem is not FrpProxy selected)
        {
            MessageBox.Show(this, "请先在表格里选中要删除的规则。", "Twice1200",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _frpRules.Remove(selected);
        RefreshFrpRuleSummary();
    }

    /// <summary>刷新规则计数与冲突提示。</summary>
    private void RefreshFrpRuleSummary()
    {
        if (FrpRuleSummaryText is null)
        {
            return;
        }

        var rules = FrpRulesGrid?.Items.OfType<FrpProxy>().ToList() ?? [];

        if (rules.Count == 0)
        {
            FrpRuleSummaryText.Text = "尚无规则,请点「添加规则」。";
            return;
        }

        // 端口冲突提示:只在同一协议内算冲突(tcp 与 udp 可以共用端口号)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        foreach (var rule in rules)
        {
            var key = $"{(rule.Type ?? "tcp").ToUpperInvariant()}:{rule.RemotePort}";

            if (!seen.Add(key))
            {
                conflicts.Add($"{rule.Type?.ToUpperInvariant()} {rule.RemotePort}");
            }
        }

        FrpRuleSummaryText.Text = conflicts.Count == 0
            ? $"共 {rules.Count} 条规则"
            : $"共 {rules.Count} 条规则 · ⚠ 端口冲突:{string.Join("、", conflicts)}";
    }

    /// <summary>把配置写入磁盘。失败时按需提示,但绝不中断当前操作。</summary>
    private void TrySaveConfiguration(AppConfiguration configuration, bool quiet)
    {
        try
        {
            AppPaths.EnsureConfigurationDirectory();

            // 写入前先规范化权限。
            // 这一步修的真实故障:以管理员身份运行过一次之后,
            // 非管理员运行会因为文件所有者是 Administrators 而写入失败。
            OwnershipHelper.EnsureWritableByCurrentUser(AppPaths.ConfigurationFile);

            File.WriteAllText(
                AppPaths.ConfigurationFile,
                configuration.ToJson(),
                new UTF8Encoding(false));

            if (!quiet)
            {
                AppendLog($"已保存配置:{AppPaths.ConfigurationFile}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[!] 保存配置失败:{ex.GetType().Name}: {ex.Message}");
            AppendLog("    若之前以管理员身份运行过,可能是文件权限问题。");
            AppendLog("    可以尝试:以管理员身份运行一次,让程序修正文件所有者。");
        }
    }

    /// <summary>把启动失败翻译成下一步该做什么。</summary>
    private void ExplainFailure(TunnelStartStatus status)
    {
        switch (status)
        {
            case TunnelStartStatus.NotElevated:
                AppendLog("");
                AppendLog("    原因:edge.exe 需要管理员权限才能访问 TAP 虚拟网卡。");
                AppendLog("    下一步:以管理员身份重新启动本程序。");
                AppendLog("            (这不是缺陷 —— 原手工方案让你「以管理员身份打开 cmd」,做的正是同一件事。)");
                break;

            case TunnelStartStatus.NoTapDevices:
            case TunnelStartStatus.AdapterMissing:
                AppendLog("");
                AppendLog("    下一步:点击「准备环境」,程序会自动安装驱动、创建或重命名网卡。");
                break;

            case TunnelStartStatus.ExecutableMissing:
                AppendLog("");
                AppendLog("    下一步:确认 assets/ 目录里有 n2n/edge.exe。");
                break;

            case TunnelStartStatus.InvalidConfiguration:
                AppendLog("");
                AppendLog("    下一步:检查上方填写的网卡名与组名。");
                break;
        }
    }

    private void OnTunnelStateChanged(object? sender, TunnelStateChange change)
    {
        RunOnUiThread(() => SetState(Describe(change.State)));
    }

    private void OnTunnelOutput(object? sender, string line)
    {
        RunOnUiThread(() => AppendLog("  [edge] " + line));
    }

    private static string Describe(TunnelState state) => state switch
    {
        TunnelState.Stopped => "未连接",
        TunnelState.Starting => "启动中",
        TunnelState.Running => "运行中",
        TunnelState.Exited => "已退出",
        TunnelState.Failed => "出错",
        _ => state.ToString(),
    };

    /// <summary>统一的忙碌包装:禁用按钮、显示进度条、保证异常不会让界面卡住。</summary>
    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        if (_busy)
        {
            SetStatus("上一个操作还没结束。");
            return;
        }

        _busy = true;
        SetBusy(true);
        SetStatus(status);

        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 界面层必须兜住所有异常:否则一个未处理异常会让窗口直接消失,
            // 而用户连发生了什么都不知道。
            AppendLog($"[×] 操作出错:{ex.GetType().Name}: {ex.Message}");
            SetStatus($"操作出错:{ex.Message}");
        }
        finally
        {
            _busy = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        // n2n 侧
        ConnectButton.IsEnabled = !busy;
        DisconnectButton.IsEnabled = !busy && _tunnel is { State: TunnelState.Running };
        PrepareEnvButton.IsEnabled = !busy;
        LocalVerifyButton.IsEnabled = !busy;
        RunDoctorButton.IsEnabled = !busy;
        SaveKeyButton.IsEnabled = !busy;

        // frp 侧
        FrpConnectButton.IsEnabled = !busy;
        FrpDisconnectButton.IsEnabled = !busy && _frpTunnel is { State: TunnelState.Running };
        FrpPreviewButton.IsEnabled = !busy;
        FrpTestServerButton.IsEnabled = !busy;
        SaveTokenButton.IsEnabled = !busy;
    }

    private void SetState(string state) => StateText.Text = state;

    private void SetStatus(string status) => StatusText.Text = status;

    /// <summary>追加一行日志到界面。必须从 UI 线程调用。</summary>
    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        LogSummaryText.Text = $"{LogBox.LineCount} 行";
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static string HostOf(string endpoint, string fallback)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 ? endpoint[..colon] : (string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint);
    }

    private static int PortOf(string endpoint, int fallback)
    {
        var colon = endpoint.LastIndexOf(':');
        return colon > 0 && int.TryParse(endpoint[(colon + 1)..], out var port) ? port : fallback;
    }
}
