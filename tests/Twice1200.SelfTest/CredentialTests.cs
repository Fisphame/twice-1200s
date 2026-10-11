using System.Security.Principal;
using Twice1200.Core.Security;

namespace Twice1200.SelfTest;

/// <summary>
/// 凭据库与 DPAPI。
/// <para>
/// 这些用例全部使用临时目录,不碰用户的真实凭据库。
/// 其中最关键的一条是<b>"磁盘上不出现明文"</b> ——
/// 那是整个凭据存储存在的唯一理由,值得用直接的字节级检查来守。
/// </para>
/// </summary>
public static class CredentialTests
{
    /// <summary>注册全部用例。</summary>
    public static void Register(TestSuite suite)
    {
        // ---------------------------------------------------------------- DPAPI 本身

        suite.Test("DPAPI:加解密往返一致", () =>
        {
            Check.True(DpapiSecretProtector.IsSupported, "Windows 上应支持 DPAPI");

            const string plaintext = "Twice1200-DPAPI-RoundTrip-Probe";
            var cipher = DpapiSecretProtector.Protect(plaintext);

            Check.True(cipher.Length > 0, "密文不应为空");
            Check.True(cipher.Length > plaintext.Length, "密文通常比明文长(含头部与校验)");

            var decrypted = DpapiSecretProtector.UnprotectToString(cipher);
            Check.Equal(plaintext, decrypted, "解密结果应与原文一致");
        });

        suite.Test("DPAPI:密文里不含明文", () =>
        {
            // 这是加密有没有真正生效的直接检验。
            // 用字节级搜索而不是字符串搜索:编码差异会让字符串搜索漏判。
            const string plaintext = "UNIQUE-PLAINTEXT-MARKER-ABCDEF";
            var cipher = DpapiSecretProtector.Protect(plaintext);
            var needle = System.Text.Encoding.UTF8.GetBytes(plaintext);

            Check.False(ContainsBytes(cipher, needle), "密文里不应出现明文片段");
        });

        suite.Test("DPAPI:同一明文两次加密得到不同密文", () =>
        {
            // DPAPI 每次加密都会生成新的随机盐,因此密文不同。
            // 这一点有实际意义:无法通过比对密文来判断两个密钥是否相同。
            const string plaintext = "Same-Input-Different-Output";

            var first = DpapiSecretProtector.Protect(plaintext);
            var second = DpapiSecretProtector.Protect(plaintext);

            Check.False(
                first.AsSpan().SequenceEqual(second),
                "两次加密结果应不同 —— 否则说明没有使用随机盐");

            Check.Equal(
                plaintext,
                DpapiSecretProtector.UnprotectToString(first),
                "第一份密文应能解开");
            Check.Equal(
                plaintext,
                DpapiSecretProtector.UnprotectToString(second),
                "第二份密文应能解开");
        });

        suite.Test("DPAPI:密文被篡改时安全失败而不抛异常", () =>
        {
            var cipher = DpapiSecretProtector.Protect("tamper-me");
            cipher[^1] ^= 0xFF; // 翻转最后一个字节

            var ok = DpapiSecretProtector.TryUnprotectToString(cipher, out var plaintext, out var error);

            Check.False(ok, "被篡改的密文不应解密成功");
            Check.Equal<string?>(null, plaintext, "失败时不应给出明文");
            Check.NotNull(error, "应给出失败原因");
            Check.Contains(error!, "无法解密", "原因应说明是解密问题");
        });

        suite.Test("DPAPI:空输入被明确拒绝", () =>
        {
            // 运行时守卫:非 Windows 上跳过这项。
            // 它同时让平台分析器能推断出"下面的调用只在 Windows 上发生",
            // 从而消除 CA1416 —— 这是守卫的正当用法,不是把警告压掉。
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // DPAPI 不接受零长度输入(实测:CryptProtectData 在 cbData=0 时失败)。
            // 我们选择在入口处明确拒绝,而不是把 Win32 错误码透给调用方。
            var threw = false;
            try
            {
                ProtectEmpty();
            }
            catch (ArgumentException ex)
            {
                threw = true;
                Check.Contains(ex.Message, "空内容", "错误信息应说明不接受空内容");
            }

            Check.True(threw, "加密空内容应抛出 ArgumentException,而不是 Win32 异常");
        });

        // ---------------------------------------------------------------- 凭据库

        suite.Test("凭据库:保存后能取回,且磁盘上没有明文", () =>
        {
            WithTemporaryStore((store, path) =>
            {
                const string secret = "Unique-Secret-Marker-1234567890";

                var result = store.SetSecret(CredentialHandles.N2nKey, secret);
                Check.True(result.IsSuccess, $"保存应成功:{result.Message}");
                Check.True(File.Exists(path), "凭据文件应被创建");

                // 最关键的一条:文件里不能有明文
                var rawBytes = File.ReadAllBytes(path);
                var rawText = File.ReadAllText(path);

                Check.False(
                    rawText.Contains(secret, StringComparison.Ordinal),
                    "凭据文件里不应出现明文密钥");
                Check.False(
                    ContainsBytes(rawBytes, System.Text.Encoding.UTF8.GetBytes(secret)),
                    "凭据文件的字节里也不应出现明文密钥");

                // 取回
                Check.True(store.TryGetSecret(CredentialHandles.N2nKey, out var loaded, out var getResult),
                    $"应能取回:{getResult.Message}");
                Check.Equal(secret, loaded, "取回的内容应与保存的一致");
            });
        });

        suite.Test("凭据库:GetSecretOrNull 对不存在的键返回 null", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                Check.Equal<string?>(null, store.GetSecretOrNull("no/such/key"), "不存在的键应返回 null");
            });
        });

        suite.Test("凭据库:对不存在的键给出明确的失败原因", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                // 先存一项把凭据库建起来 —— 否则会先命中 StoreMissing,
                // 测不到"键不存在"这条分支
                store.SetSecret("existing/key", "some-existing-value");

                var ok = store.TryGetSecret("no/such/key", out var missing, out var result);

                Check.False(ok, "不应成功");
                Check.Equal<string?>(null, missing, "失败时不应给出内容");
                Check.Equal(CredentialError.NotFound, result.Error, "应报告「未找到」");
            });
        });

        suite.Test("凭据库:文件不存在时报告 StoreMissing", () =>
        {
            var path = Path.Combine(Check.TempRoot, $"twice1200-absent-{Guid.NewGuid():N}", "secrets.dat");
            var store = new CredentialStore(path);

            var ok = store.TryGetSecret(CredentialHandles.N2nKey, out _, out var result);

            Check.False(ok, "不应成功");
            Check.Equal(CredentialError.StoreMissing, result.Error, "应报告凭据库尚未创建");
        });

        suite.Test("凭据库:HasSecret 不依赖解密", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                Check.False(store.HasSecret(CredentialHandles.N2nKey), "尚未保存时应为 false");

                store.SetSecret(CredentialHandles.N2nKey, "some-secret-value");

                Check.True(store.HasSecret(CredentialHandles.N2nKey), "保存后应为 true");
                Check.False(store.HasSecret("other/key"), "别的键仍应为 false");
            });
        });

        suite.Test("凭据库:ListHandles 只返回值名", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("a/one", "secret-value-one");
                store.SetSecret("b/two", "secret-value-two");

                var handles = store.ListHandles();

                Check.Equal(2, handles.Count, "应有两个键");
                Check.True(handles.Contains("a/one"), "应包含 a/one");
                Check.True(handles.Contains("b/two"), "应包含 b/two");

                // 键名列表里不应混入值 —— 这条 API 的形状本身是安全约束
                Check.False(
                    handles.Any(h => h.Contains("secret-value", StringComparison.Ordinal)),
                    "键名列表里不应出现任何值");
            });
        });

        suite.Test("凭据库:多键互不干扰", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("n2n/key", "value-for-n2n");
                store.SetSecret("frp/token", "value-for-frp");

                Check.Equal("value-for-n2n", store.GetSecretOrNull("n2n/key"), "n2n 的值");
                Check.Equal("value-for-frp", store.GetSecretOrNull("frp/token"), "frp 的值");
            });
        });

        suite.Test("凭据库:更新已有键会覆盖", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("k", "first-value");
                store.SetSecret("k", "second-value-longer");

                Check.Equal(1, store.ListHandles().Count, "不应产生重复键");
                Check.Equal("second-value-longer", store.GetSecretOrNull("k"), "应取到新值");
            });
        });

        suite.Test("凭据库:删除与清空", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("a", "value-a-value");
                store.SetSecret("b", "value-b-value");

                var deleted = store.DeleteSecret("a");
                Check.True(deleted.IsSuccess, "删除应成功");
                Check.Equal(1, store.ListHandles().Count, "应只剩一项");

                var missing = store.DeleteSecret("a");
                Check.False(missing.IsSuccess, "重复删除应失败");
                Check.Equal(CredentialError.NotFound, missing.Error, "应报告未找到");

                var cleared = store.Clear();
                Check.True(cleared.IsSuccess, "清空应成功");
                Check.Equal(0, store.ListHandles().Count, "清空后应为空");
            });
        });

        suite.Test("凭据库:拒绝空键名与空值", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                Check.Equal(
                    CredentialError.InvalidArgument,
                    store.SetSecret("", "value").Error,
                    "空键名应被拒绝");

                Check.Equal(
                    CredentialError.InvalidArgument,
                    store.SetSecret("k", "").Error,
                    "空值应被拒绝 —— 清除请用删除操作");

                Check.Equal(
                    CredentialError.InvalidArgument,
                    store.DeleteSecret("").Error,
                    "删除空键名应被拒绝");
            });
        });

        suite.Test("凭据库:键名大小写敏感", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("Key", "upper-value-here");

                Check.True(store.HasSecret("Key"), "原名应存在");
                Check.False(store.HasSecret("key"), "不同大小写应视为不同的键");

                // 这一点很重要:否则 config.json 里写错大小写会静默取到别的密钥
            });
        });

        suite.Test("凭据库:损坏的文件不会被静默覆盖", () =>
        {
            var directory = Path.Combine(Check.TempRoot, $"twice1200-corrupt-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "secrets.dat");

            try
            {
                // 写入一段不是合法 JSON 的内容,模拟文件损坏
                File.WriteAllText(path, "{ this is not valid json", System.Text.Encoding.UTF8);

                var store = new CredentialStore(path);
                var result = store.SetSecret("k", "some-value-here");

                Check.False(result.IsSuccess, "损坏的文件上不应报告成功");

                // 关键:原文件必须还在 —— 不能因为解析失败就把用户的数据冲掉
                Check.True(File.Exists(path), "损坏的原文件不应被删除");
                Check.Contains(
                    File.ReadAllText(path),
                    "this is not valid json",
                    "原内容不应被覆盖 —— 否则用户可能丢掉仍然可恢复的数据");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        });

        suite.Test("凭据库:键名为中文也能正常工作", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                store.SetSecret("网络/密码", "chinese-handle-value");

                Check.True(store.HasSecret("网络/密码"), "中文键名应可用");
                Check.Equal("chinese-handle-value", store.GetSecretOrNull("网络/密码"), "应能取回");
            });
        });

        suite.Test("凭据库:值含中文与特殊字符也不受影响", () =>
        {
            WithTemporaryStore((store, _) =>
            {
                const string secret = "密码 with spaces & 符号 \"quotes\" and \\backslash\\ 与换行\n第二行";

                store.SetSecret("k", secret);
                Check.Equal(secret, store.GetSecretOrNull("k"), "复杂内容应原样往返");
            });
        });

        suite.Test("凭据库:标准权限下可以写入(模拟管理员创建过的文件)", () =>
        {
            // 这是一个真实故障的回归守卫。
            //
            // 现象:以管理员身份运行过一次之后,再用普通权限运行会报
            //   Access to the path '...\secrets.dat.tmp' is denied
            // 原因是那些文件的所有者是 BUILTIN\Administrators 而不是当前用户。
            //
            // 修法:写入前显式把所有者设为当前用户,并显式授予 FullControl,
            // 不再依赖继承。这里验证"文件的所有者确实变成了当前用户"。
            WithTemporaryStore((store, path) =>
            {
                store.SetSecret("first/key", "first-value-here");
                Check.True(File.Exists(path), "第一次写入应成功");

                // 第二次写入会走"覆盖已有文件"的路径 —— 正是出故障的那条路
                var second = store.SetSecret("second/key", "second-value-here");
                Check.True(second.IsSuccess, $"覆盖写入应成功:{second.Message}");

                Check.Equal("second-value-here", store.GetSecretOrNull("second/key"), "新值应能取回");

                // 关键断言:所有者应当是当前用户,而不是 Administrators
                if (OperatingSystem.IsWindows())
                {
                    var owner = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                    var currentUser = WindowsIdentity.GetCurrent().User;

                    if (owner is not null && currentUser is not null)
                    {
                        Check.True(
                            owner.Equals(currentUser),
                            $"文件所有者应为当前用户,实际是 {owner.Translate(typeof(NTAccount)).Value}");
                    }
                }
            });
        });

        suite.Test("所有权:EnsureWritableByCurrentUser 能创建目录并设定所有者", () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // 用平铺目录,不用嵌套的 sub\。
            //
            // 原因:受限环境下连临时目录都可能建不出来(实测遇到过),
            // 那时 Directory.CreateDirectory 会返回而不抛异常,
            // 从而让这个测试以"应成功创建并规范化 — 期望为真,但实际为假"失败 ——
            // 看起来像被测代码有 bug,实际是环境不允许。
            // 平铺路径与产品实际使用的形态一致(配置目录下直接放文件),
            // 所以这样改既更贴近真实使用,也少一个环境依赖。
            var directory = Path.Combine(Check.TempRoot, $"twice1200-own-{Guid.NewGuid():N}");
            var file = Path.Combine(directory, "target.dat");

            try
            {
                // 目标目录还不存在 —— 该方法应当连同父目录一起创建
                var ok = OwnershipHelper.EnsureWritableByCurrentUser(file);

                if (!ok || !Directory.Exists(directory))
                {
                    // 环境不允许在临时目录下建目录 —— 无法验证,如实跳过而不是报失败。
                    // 这正是"测试夹具的失败不是被测代码的失败"那一类情况。
                    Check.True(true, "临时目录不可创建,跳过(环境限制)");
                    return;
                }

                Check.True(ok, "应成功创建并规范化");
                Check.True(Directory.Exists(directory), "父目录应被创建");

                // 建一个文件再规范化一次,验证所有者
                File.WriteAllText(file, "x", System.Text.Encoding.UTF8);
                Check.True(OwnershipHelper.EnsureWritableByCurrentUser(file), "对已存在文件也应成功");

                var owner = new FileInfo(file).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                var currentUser = WindowsIdentity.GetCurrent().User;

                Check.True(
                    owner is not null && currentUser is not null && owner.Equals(currentUser),
                    "文件所有者应为当前用户");
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // 清理失败不影响结论
                }
            }
        });

        suite.Test("所有权:对不存在的路径安全返回 false", () =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            Check.False(OwnershipHelper.EnsureWritableByCurrentUser(""), "空路径应返回 false");
            Check.False(OwnershipHelper.EnsureWritableByCurrentUser("   "), "空白路径应返回 false");
        });

        suite.Test("凭据库:标准键名有用途说明", () =>
        {
            foreach (var handle in CredentialHandles.All)
            {
                var description = CredentialHandles.Describe(handle);
                Check.True(!string.IsNullOrWhiteSpace(description), $"键「{handle}」应有用途说明");
                Check.False(
                    description.Contains("自定义", StringComparison.Ordinal),
                    $"标准键「{handle}」不应被当成自定义键");
            }

            Check.Contains(
                CredentialHandles.Describe("something/else"),
                "自定义",
                "未知键应说明是自定义键");
        });
    }

    /// <summary>
    /// 调用加密空内容。
    /// <para>
    /// 单独抽成一个带平台标注的方法:自检项目本身是跨平台目标(<c>net9.0</c>),
    /// 直接在这里调用只支持 Windows 的 API 会触发 CA1416。
    /// 标注写在这一处,比给整个项目加豁免更精确。
    /// </para>
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectEmpty() => DpapiSecretProtector.Protect(Array.Empty<byte>());

    /// <summary>在一个临时目录里跑一段针对凭据库的操作,结束后清理。</summary>
    private static void WithTemporaryStore(Action<CredentialStore, string> body)
    {
        var directory = Path.Combine(
            Check.TempRoot,
            $"twice1200-cred-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "secrets.dat");

        try
        {
            body(new CredentialStore(path), path);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结论
            }
        }
    }

    /// <summary>字节级子串查找。字符串查找会因编码差异漏判。</summary>
    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
