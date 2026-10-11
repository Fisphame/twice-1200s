namespace Twice1200.Core.Configuration;

/// <summary>
/// 配置校验。
/// <para>
/// 校验器**只处理结构性问题与已知危险值**,不接触密钥明文。
/// 需要判断密钥强度时,用 <see cref="IsWeakSecret"/> 单独检查 ——
/// 那一步在用户输入密钥时调用,而不是在读取配置时。
/// </para>
/// </summary>
public static class ConfigurationValidator
{
    /// <summary>
    /// 已知的示例 / 弱口令与弱组名。
    /// <para>
    /// 其中 <c>mygroup</c>、<c>mysecretpassword</c>、<c>mynet</c>、<c>mypasswd</c>
    /// 是 n2n 官方文档与示例文件里的默认值。因为太多人直接照抄不改,
    /// 这些值实际上等同于"没有密码" —— 任何人都能用同样的组名密码加入网络。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> KnownWeakValues = new(StringComparer.OrdinalIgnoreCase)
    {
        // n2n 官方示例值
        "mygroup", "mysecretpassword", "mynet", "mypasswd", "mycommunity", "mypassword",
        // 通用弱口令
        "password", "passwd", "123456", "12345678", "123456789", "admin", "root",
        "test", "guest", "changeme", "secret", "qwerty", "abc123", "111111", "000000",
        "n2n", "frp", "twice1200",
    };

    /// <summary>校验整个配置。</summary>
    public static IReadOnlyList<ValidationIssue> Validate(AppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var issues = new List<ValidationIssue>();
        ValidateN2n(configuration.N2n, issues);
        ValidateFrp(configuration.Frp, issues);
        return issues;

        static void ValidateN2n(N2nConfiguration n2n, List<ValidationIssue> issues)
        {
            // ---- 组名 ----
            if (string.IsNullOrWhiteSpace(n2n.Community))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.community",
                    "未设置网络组名。",
                    "组名决定谁能加入同一个网络,必须在向导里填写。"));
            }
            else
            {
                if (n2n.Community.Length < 6)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning,
                        "n2n.community",
                        $"组名「{n2n.Community}」偏短({n2n.Community.Length} 个字符)。",
                        "较短的组名更容易被他人猜中。建议使用 12 个字符以上的随机字符串。"));
                }

                if (KnownWeakValues.Contains(n2n.Community))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "n2n.community",
                        $"组名使用了公开的示例值「{n2n.Community}」。",
                        "这是 n2n 官方示例里的默认组名,使用它意味着任何人都能加入你的网络。必须换掉。"));
                }
            }

            // ---- 超级节点 ----
            if (string.IsNullOrWhiteSpace(n2n.Supernode))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.supernode",
                    "未设置超级节点地址。",
                    "格式为 主机:端口,例如 supernode.example.com:7777。"));
            }
            else if (!Net.Endpoint.TryParse(n2n.Supernode, 7777, out var endpoint, out var endpointError))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "n2n.supernode", endpointError!,
                    "正确格式:supernode.example.com:7777"));
            }
            else if (endpoint.Port is < 1 or > 65535)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error, "n2n.supernode",
                    $"超级节点端口 {endpoint.Port} 超出 1–65535 范围。"));
            }

            // ---- 网段 ----
            var networkOk = Net.IpSubnet.TryParse(n2n.VirtualNetwork, out var subnet);
            if (!networkOk)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.virtualNetwork",
                    $"虚拟网段「{n2n.VirtualNetwork}」不是合法的 CIDR。",
                    "正确格式:10.42.0.0/24"));
            }

            // ---- 本机虚拟 IP ----
            if (string.IsNullOrWhiteSpace(n2n.AssignedIp))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Info,
                    "n2n.assignedIp",
                    "未指定本机虚拟 IP,将由程序自动挑选。",
                    "同一组内每台机器的 IP 必须唯一。程序会通过 ping 扫描挑选空闲地址," +
                    "但扫描无法发现被防火墙屏蔽或当前离线的同伴,首次连接后请确认没有冲突。"));
            }
            else if (!System.Net.IPAddress.TryParse(n2n.AssignedIp, out var assigned) ||
                     assigned.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.assignedIp",
                    $"虚拟 IP「{n2n.AssignedIp}」不是合法的 IPv4 地址。"));
            }
            else if (networkOk && !subnet.Contains(assigned))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.assignedIp",
                    $"虚拟 IP {n2n.AssignedIp} 不在网段 {subnet} 内。",
                    "它必须属于 n2n.virtualNetwork 指定的网段。"));
            }

            // ---- 密钥引用 ----
            if (!n2n.Key.IsSet)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.key",
                    "未设置网络密码。",
                    "密码是所有组员必须一致的口令,它将加密存储在本机。"));
            }

            // ---- 虚拟网卡名 ----
            if (string.IsNullOrWhiteSpace(n2n.Tap.AdapterName))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "n2n.tap.adapterName",
                    "虚拟网卡名称为空。"));
            }
            else if (n2n.Tap.AutoFallbackOnNonChineseCodePage &&
                     n2n.Tap.AdapterName.Any(c => c > 0x7F) &&
                     string.IsNullOrWhiteSpace(n2n.Tap.FallbackAdapterName))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "n2n.tap.fallbackAdapterName",
                    "网卡名含非 ASCII 字符,但未设置回退名称。",
                    "在非简体中文代码页的系统上,中文网卡名会因编码转换变成乱码,导致 edge.exe 找不到网卡。"));
            }
        }

        static void ValidateFrp(FrpConfiguration frp, List<ValidationIssue> issues)
        {
            if (string.IsNullOrWhiteSpace(frp.ServerAddress))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "frp.serverAddress",
                    "未设置 frp 服务端地址。"));
            }
            else if (frp.ServerAddress.Contains("://", StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "frp.serverAddress",
                    "服务端地址不应包含协议前缀。",
                    "只填主机名或 IP,例如 frp.example.com,不要写 http://。"));
            }

            if (frp.ServerPort is < 1 or > 65535)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "frp.serverPort",
                    $"控制端口 {frp.ServerPort} 超出 1–65535 范围。"));
            }

            if (!frp.AuthToken.IsSet)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "frp.authToken",
                    "未设置 frp 鉴权 token。",
                    "没有 token 或 token 与服务端不一致时无法连接。"));
            }

            if (!frp.RequireTls)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "frp.requireTls",
                    "已关闭 frp 的 TLS 加密。",
                    "控制连接与转发数据将以明文传输,容易被中间人窃听。除非在完全可信的内网,否则应开启。"));
            }

            if (frp.Proxies.Count == 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "frp.proxies",
                    "未配置任何端口映射规则。",
                    "没有规则时 frp 连上也做不了转发。"));
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remotePorts = new HashSet<int>();

            foreach (var proxy in frp.Proxies)
            {
                var path = $"frp.proxies[{proxy.Name}]";

                if (string.IsNullOrWhiteSpace(proxy.Name))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, "frp.proxies", "存在名称为空的映射规则。",
                        "规则名用于在服务端区分不同隧道。"));
                }
                else if (!names.Add(proxy.Name))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, $"{path}.name",
                        $"规则名「{proxy.Name}」重复。",
                        "同一客户端内的规则名必须唯一。"));
                }

                if (!string.Equals(proxy.Type, "tcp", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Warning, $"{path}.type",
                        $"当前只完整支持 tcp 类型,收到的是「{proxy.Type}」。",
                        "udp/http/https 等类型后续再支持。"));
                }

                if (!System.Net.IPAddress.TryParse(proxy.LocalIp, out _))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, $"{path}.localIp",
                        $"本地地址「{proxy.LocalIp}」不是合法的 IP。"));
                }

                foreach (var (label, port) in new[] { ("localPort", proxy.LocalPort), ("remotePort", proxy.RemotePort) })
                {
                    if (port is < 1 or > 65535)
                    {
                        issues.Add(new ValidationIssue(
                            ValidationSeverity.Error, $"{path}.{label}",
                            $"端口 {port} 超出 1–65535 范围。"));
                    }
                }

                if (proxy.RemotePort is >= 1 and <= 65535 && !remotePorts.Add(proxy.RemotePort))
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error, $"{path}.remotePort",
                        $"服务端端口 {proxy.RemotePort} 被多条规则同时占用。",
                        "每条规则必须映射到不同的服务端端口。"));
                }
            }
        }
    }

    /// <summary>
    /// 判断一个密钥是否属于已知弱值或明显不合格。
    /// <para>
    /// 仅在**用户输入密钥时**调用,返回值本身不包含密钥内容,可安全记录到日志。
    /// </para>
    /// </summary>
    /// <returns>不合格时返回原因描述;合格时返回 <c>null</c>。</returns>
    public static string? DescribeWeakness(string? secret, int minimumLength = 8)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "密钥为空。";
        }

        if (secret.Length < minimumLength)
        {
            return $"密钥长度只有 {secret.Length} 个字符,建议至少 {minimumLength} 个。";
        }

        if (KnownWeakValues.Contains(secret))
        {
            return "这是公开的示例口令,等同于没有设密码。";
        }

        if (secret.Distinct().Count() <= 2)
        {
            return "密钥几乎由单一字符重复构成,强度极低。";
        }

        if (secret.All(char.IsDigit))
        {
            return "密钥全部是数字,容易被暴力破解。";
        }

        return null;
    }
}
