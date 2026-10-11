namespace Twice1200.SelfTest;

/// <summary>极简测试运行器。</summary>
public sealed class TestSuite
{
    private readonly List<(string Name, Action Body)> _tests = [];
    private readonly List<string> _failures = [];
    private int _passed;

    /// <summary>注册一个测试用例。</summary>
    public TestSuite Test(string name, Action body)
    {
        _tests.Add((name, body));
        return this;
    }

    /// <summary>运行全部用例,并打印结果。</summary>
    /// <returns>全部通过时返回 <c>true</c>。</returns>
    public bool Run(string title)
    {
        Console.WriteLine($"═══ {title} ═══");
        Console.WriteLine();

        foreach (var (name, body) in _tests)
        {
            try
            {
                body();
                _passed++;
                Console.WriteLine($"  [√] {name}");
            }
            catch (Exception ex)
            {
                var message = ex is AssertionException
                    ? ex.Message
                    : $"{ex.GetType().Name}: {ex.Message}";

                _failures.Add($"{name} — {message}");
                Console.WriteLine($"  [×] {name}");
                Console.WriteLine($"        {message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  通过 {_passed} / 失败 {_failures.Count} / 共 {_tests.Count}");

        if (_failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  失败的用例:");
            foreach (var failure in _failures)
            {
                Console.WriteLine($"    × {failure}");
            }
        }

        Console.WriteLine();
        return _failures.Count == 0;
    }
}

/// <summary>断言失败。</summary>
public sealed class AssertionException : Exception
{
    /// <summary>构造。</summary>
    public AssertionException(string message) : base(message)
    {
    }
}

/// <summary>断言工具。</summary>
public static class Check
{
    /// <summary>条件必须为真。</summary>
    public static void True(bool condition, string because)
    {
        if (!condition)
        {
            throw new AssertionException($"期望为真,但实际为假:{because}");
        }
    }

    /// <summary>条件必须为假。</summary>
    public static void False(bool condition, string because)
    {
        if (condition)
        {
            throw new AssertionException($"期望为假,但实际为真:{because}");
        }
    }

    /// <summary>值必须相等。</summary>
    public static void Equal<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException($"{because}:期望 <{expected}>,实际 <{actual}>");
        }
    }

    /// <summary>字符串必须包含子串。</summary>
    public static void Contains(string haystack, string needle, string because)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"{because}:未在文本中找到「{needle}」");
        }
    }

    /// <summary>字符串必须不包含子串。</summary>
    public static void DoesNotContain(string haystack, string needle, string because)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException($"{because}:文本中不应出现「{needle}」");
        }
    }

    /// <summary>对象不能为 null。</summary>
    public static void NotNull(object? value, string because)
    {
        if (value is null)
        {
            throw new AssertionException($"{because}:不应为 null");
        }
    }

    /// <summary>集合不应为空。</summary>
    public static void NotEmpty<T>(IReadOnlyCollection<T> collection, string because)
    {
        if (collection.Count == 0)
        {
            throw new AssertionException($"{because}:集合不应为空");
        }
    }

    // ================================================================
    //  临时路径
    // ================================================================

    private static string? _tempRoot;

    /// <summary>
    /// 一个确实可写的临时根目录。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能直接写 <c>Path.GetTempPath()</c>:</b>
    /// 在受限环境里临时目录创建会被拒绝,于是所有依赖它的测试都失败,
    /// 报出来的是 <c>UnauthorizedAccessException</c> ——
    /// <b>看起来像被测代码有 bug,实际是环境不允许。</b>
    /// 这类"测试夹具的失败被误报成代码失败"最费时间,必须在基础设施层解决,
    /// 否则每次换环境都要重新怀疑一遍自己的代码。
    /// </para>
    /// <para>
    /// 优先用系统临时目录;不行就退回测试程序所在目录
    /// (它位于项目树内,通常可写)。
    /// </para>
    /// </remarks>
    public static string TempRoot => _tempRoot ??= ResolveTempRoot();

    /// <summary>在 <see cref="TempRoot"/> 下生成一个唯一路径。不会创建它。</summary>
    public static string TempPath(string prefix)
        => Path.Combine(TempRoot, $"{prefix}-{Guid.NewGuid():N}");

    private static string ResolveTempRoot()
    {
        var candidates = new[]
        {
            Path.GetTempPath(),
            AppContext.BaseDirectory,
        };

        foreach (var candidate in candidates)
        {
            try
            {
                var probe = Path.Combine(candidate, $"twice1200-writable-{Guid.NewGuid():N}");
                Directory.CreateDirectory(probe);
                Directory.Delete(probe, recursive: true);
                return candidate;
            }
            catch
            {
                // 试下一个
            }
        }

        // 都不行:返回系统临时目录,让测试如实报出环境问题,
        // 而不是在这里悄悄换成一个不可预期的位置
        return Path.GetTempPath();
    }
}
