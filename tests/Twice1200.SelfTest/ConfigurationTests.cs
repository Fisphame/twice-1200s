using Twice1200.Core.Configuration;

namespace Twice1200.SelfTest;

/// <summary>配置模型、序列化与校验。</summary>
public static class ConfigurationTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        suite.Test("模板:不含任何明文密钥", () =>
        {
            var template = AppConfiguration.CreateTemplate();
            var json = template.ToJson();

            // 最关键的一条:配置文件里永远不能出现密钥本身
            Check.Contains(json, "\"handle\"", "模板应包含密钥引用");
            Check.DoesNotContain(json, "mysecretpassword", "模板不允许出现示例密码");
            Check.DoesNotContain(json, "REAL-TOKEN-MUST-NOT-LEAK", "模板不允许出现 token 值");
        });

        suite.Test("模板:结构完整但有未填写的必填项", () =>
        {
            var template = AppConfiguration.CreateTemplate();
            Check.Equal(1, template.SchemaVersion, "模板的 schema 版本");
            Check.True(template.N2n.Key.IsSet, "模板应已建立密码引用");
            Check.True(template.Frp.AuthToken.IsSet, "模板应已建立 token 引用");
            Check.True(string.IsNullOrEmpty(template.N2n.Community), "组名应由用户填写");

            var issues = ConfigurationValidator.Validate(template);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.community"),
                "空组名应被报为错误");
        });

        suite.Test("序列化:往返后内容一致", () =>
        {
            var original = AppConfiguration.CreateTemplate();
            original.N2n.Community = "a-strong-community-name";
            original.N2n.AssignedIp = "10.236.167.114";
            original.N2n.Key = SecretReference.Dpapi("n2n/key");
            original.Frp.ServerAddress = "example.invalid";
            original.Frp.Proxies[0].RemotePort = 25566;

            var restored = AppConfiguration.Parse(original.ToJson());

            Check.Equal(original.N2n.Community, restored.N2n.Community, "组名");
            Check.Equal(original.N2n.AssignedIp, restored.N2n.AssignedIp, "虚拟 IP");
            Check.Equal("n2n/key", restored.N2n.Key.Handle, "密钥引用");
            Check.Equal(SecretSource.Dpapi, restored.N2n.Key.Source, "密钥来源");
            Check.Equal("example.invalid", restored.Frp.ServerAddress, "frp 服务端");
            Check.Equal(25566, restored.Frp.Proxies[0].RemotePort, "映射端口");
        });

        suite.Test("序列化:键名使用 camelCase", () =>
        {
            var json = AppConfiguration.CreateTemplate().ToJson();
            Check.Contains(json, "\"supernode\"", "应使用 camelCase 键名");
            Check.Contains(json, "\"authToken\"", "应使用 camelCase 键名");
        });

        suite.Test("序列化:中文不被转义", () =>
        {
            var config = AppConfiguration.CreateTemplate();
            config.N2n.Tap.AdapterName = "以太网 2";

            var json = config.ToJson();
            Check.Contains(json, "以太网 2", "中文应可读,便于人工检查配置");
            Check.DoesNotContain(json, "\\u", "不应转义为 \\uXXXX");
        });

        suite.Test("校验:官方示例值被判定为危险", () =>
        {
            var config = BuildValid();
            config.N2n.Community = "mygroup";

            var issues = ConfigurationValidator.Validate(config);

            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.community"),
                "n2n 官方示例组名应被报为错误");
        });

        suite.Test("校验:过短的组名给出警告", () =>
        {
            var config = BuildValid();
            config.N2n.Community = "abc";

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Warning && i.Path == "n2n.community"),
                "过短组名应给出警告");
        });

        suite.Test("校验:一份合法配置没有任何错误", () =>
        {
            var issues = ConfigurationValidator.Validate(BuildValid());
            var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();

            Check.Equal(0, errors.Count,
                "不应有错误,实际:" + string.Join(" / ", errors.Select(e => $"{e.Path}={e.Message}")));
        });

        suite.Test("校验:虚拟 IP 落在网段之外应报错", () =>
        {
            var config = BuildValid();
            config.N2n.AssignedIp = "192.168.1.50";

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.assignedIp"),
                "网段外的 IP 应被报为错误");
        });

        suite.Test("校验:非法 CIDR 应报错", () =>
        {
            var config = BuildValid();
            config.N2n.VirtualNetwork = "10.236.167.0/99";

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.virtualNetwork"),
                "非法 CIDR 应被报为错误");
        });

        suite.Test("校验:超级节点地址格式非法应报错", () =>
        {
            var config = BuildValid();
            config.N2n.Supernode = "http://example.com:7000";

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.supernode"),
                "带协议前缀的超级节点地址应被报为错误");
        });

        suite.Test("校验:未设置密钥引用应报错", () =>
        {
            var config = BuildValid();
            config.N2n.Key = SecretReference.Unset;

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "n2n.key"),
                "未设置密码应被报为错误");
        });

        suite.Test("校验:frp 关闭 TLS 应给出警告", () =>
        {
            var config = BuildValid();
            config.Frp.RequireTls = false;

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Warning && i.Path == "frp.requireTls"),
                "关闭 TLS 应给出警告");
        });

        suite.Test("校验:重复的映射规则名应报错", () =>
        {
            var config = BuildValid();
            config.Frp.Proxies.Add(new FrpProxy { Name = "WEB", LocalPort = 2001, RemotePort = 2001 });

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path.Contains("name")),
                "名称重复(忽略大小写)应被报为错误");
        });

        suite.Test("校验:服务端端口冲突应报错", () =>
        {
            var config = BuildValid();
            config.Frp.Proxies.Add(new FrpProxy
            {
                Name = "second",
                LocalPort = 2001,
                RemotePort = 25565, // 与第一条规则冲突
            });

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path.Contains("remotePort")),
                "服务端端口重复应被报为错误");
        });

        suite.Test("校验:frp 服务端地址不应带协议前缀", () =>
        {
            var config = BuildValid();
            config.Frp.ServerAddress = "https://example.com";

            var issues = ConfigurationValidator.Validate(config);
            Check.True(
                issues.Any(i => i.Severity == ValidationSeverity.Error && i.Path == "frp.serverAddress"),
                "带协议前缀应被报为错误");
        });

        suite.Test("弱密钥:示例口令被识别", () =>
        {
            Check.NotNull(ConfigurationValidator.DescribeWeakness("mysecretpassword"), "示例口令应被判为弱");
            Check.NotNull(ConfigurationValidator.DescribeWeakness("mygroup"), "示例组名应被判为弱");
            Check.NotNull(ConfigurationValidator.DescribeWeakness("password"), "常见弱口令应被判为弱");
        });

        suite.Test("弱密钥:过短与全数字被识别", () =>
        {
            Check.NotNull(ConfigurationValidator.DescribeWeakness("abc"), "过短应被判为弱");
            Check.NotNull(ConfigurationValidator.DescribeWeakness("93827164"), "全数字应被判为弱");
            Check.NotNull(ConfigurationValidator.DescribeWeakness("aaaaaaaaaaaa"), "单一字符重复应被判为弱");
            Check.NotNull(ConfigurationValidator.DescribeWeakness(""), "空口令应被判为弱");
        });

        suite.Test("弱密钥:合格口令通过", () =>
        {
            Check.Equal<string?>(null,
                ConfigurationValidator.DescribeWeakness("Xk7#mQ2pL9vB4nR8"),
                "随机混合口令应通过");
        });

        suite.Test("弱密钥:判定结果不包含口令本身", () =>
        {
            const string secret = "mysecretpassword";
            var reason = ConfigurationValidator.DescribeWeakness(secret);

            Check.NotNull(reason, "应给出原因");
            Check.DoesNotContain(reason!, secret,
                "弱密钥的判定说明会被写入日志,绝不能回显口令内容");
        });
    }

    /// <summary>构造一份各方面都合法的配置,供各用例按需只改一处。</summary>
    private static AppConfiguration BuildValid() => new()
    {
        SchemaVersion = 1,
        N2n = new N2nConfiguration
        {
            Supernode = "1200s.top:7777",
            Community = "a-strong-community-name",
            AssignedIp = "10.236.167.114",
            VirtualNetwork = "10.236.167.0/24",
            Key = SecretReference.Dpapi("n2n/key"),
            Tap = new TapConfiguration
            {
                AdapterName = "以太网 2",
                FallbackAdapterName = "n2n-TAP",
            },
        },
        Frp = new FrpConfiguration
        {
            ServerAddress = "39.105.139.45",
            ServerPort = 7000,
            AuthToken = SecretReference.Dpapi("frp/token"),
            RequireTls = true,
            Proxies =
            [
                new FrpProxy { Name = "web", LocalPort = 25565, RemotePort = 25565 },
            ],
        },
    };
}
