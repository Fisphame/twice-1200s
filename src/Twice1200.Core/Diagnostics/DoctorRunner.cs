using Twice1200.Core.Diagnostics.Checks;

namespace Twice1200.Core.Diagnostics;

/// <summary>
/// 组装并执行完整的体检流程。
/// <para>
/// 检查顺序是有讲究的:先环境、再资源、再配置、最后网络。
/// 前面的结论决定后面能不能做 —— 例如资源目录没找到,二进制检查就没有意义。
/// </para>
/// </summary>
public static class DoctorRunner
{
    /// <summary>构建标准的体检流程。</summary>
    public static CheckRunner BuildDefault()
    {
        var runner = new CheckRunner();

        // ---------- 环境 ----------
        runner.Add(new EnvironmentChecks());
        runner.Add(new ElevationCheck());
        runner.Add(new DotNetRuntimeCheck());

        // ---------- 虚拟网卡 ----------
        runner.Add(new TapPresenceCheck());
        runner.Add(new TapReadinessCheck());
        runner.Add(new TapNamingCheck());
        runner.Add(new AdapterNameSafetyCheck());

        // ---------- 资源 ----------
        runner.Add(new AssetsRootCheck());
        runner.Add(BinaryAssetCheck.N2nEdge);
        runner.Add(BinaryAssetCheck.N2nSupernode);
        runner.Add(BinaryAssetCheck.Frpc);
        runner.Add(BinaryAssetCheck.Frps);
        runner.Add(BinaryAssetCheck.TapInstaller);
        runner.Add(new BinaryArchitectureCheck());

        // ---------- 配置 ----------
        runner.Add(new ConfigurationCheck());

        // ---------- 网络 ----------
        runner.Add(new SupernodeCheck());
        runner.Add(new FrpServerCheck());
        runner.Add(new VirtualNetworkScanCheck());

        // 实测要放在最后:它会真实启动一个进程,前面所有静态检查的结论
        // 都有助于解读它的输出。
        runner.Add(new EdgeConnectivityCheck());

        // ---------- 运行期状态 ----------
        runner.Add(new RuntimeProcessCheck());

        return runner;
    }
}
