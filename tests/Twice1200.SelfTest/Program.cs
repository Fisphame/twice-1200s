using System.Text;
using Twice1200.Core.Diagnostics;
using Twice1200.SelfTest;

// Twice1200 自检程序。
// 开发期用来在没有 GUI、也没有测试框架的情况下验证 Core 的关键路径。
// 退出码:0 = 全部通过,1 = 有失败,70 = 自检自身出错。

Console.OutputEncoding = Encoding.UTF8;

try
{
    var suite = new TestSuite();

    IpSubnetTests.Register(suite);
    EndpointTests.Register(suite);
    ConfigurationTests.Register(suite);
    BinaryTests.Register(suite);
    AssetLocatorTests.Register(suite);
    RuntimeTests.Register(suite);
    DiagnosticsTests.Register(suite);
    EdgeDiagnosticsTests.Register(suite);
    TunnelTests.Register(suite);
    CredentialTests.Register(suite);
    FrpTests.Register(suite);
    NeutralityTests.Register(suite);

    Console.WriteLine($"Twice1200 自检 · .NET {Environment.Version} · {Environment.OSVersion.VersionString}");
    Console.WriteLine();

    var ok = suite.Run("SelfTest");

    Console.WriteLine(ok
        ? "结论:全部通过。"
        : "结论:存在失败用例,请先修复。");

    return ok ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[致命] 自检程序自身出错:{ex}");
    return 70;
}
