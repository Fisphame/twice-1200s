using System.IO;
using System.Windows;
using Twice1200.Core.Runtime;

namespace Twice1200.App;

/// <summary>
/// 应用程序入口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 数据根目录开关:<c>--data-root &lt;目录&gt;</c>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么界面需要这个开关:</b> 实测确认本机存在一条应用级写入限制 ——
    /// 允许写项目树,拦住项目树之外的写入(详见开发蓝本第八节)。
    /// 那会导致保存凭据失败。
    /// </para>
    /// <para>
    /// <b>而且它必须和 CLI 的 <c>--store</c> 指到同一个地方</b>,
    /// 否则界面与命令行会读写两个不同的凭据库 —— 那比不能写更难排查。
    /// </para>
    /// </remarks>
    private const string DataRootOption = "--data-root";

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        // 【必须在任何路径被读取之前执行】
        ApplyDataRootOverride(e.Args);

        base.OnStartup(e);

        // 界面里会显示 edge / netsh 的输出以及网卡名,中文必须能正确显示。
        // 不设置的话,从子进程读回来的非 ASCII 内容会乱码。
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 以前这里是"默认位置不可写就自动换一个可写位置"。
        // 那个做法已被移除,理由是实测证明它有害:它会把配置与凭据
        // 悄悄写进 bin\Debug\... 这类构建产物目录,而这些目录会被清理 ——
        // 用户的密钥与配置就这样无声消失。
        //
        // 现在的策略由 MainWindow 负责:数据目录不可写时明确告知,并给出
        // --data-root / TWICE1200_DATA_ROOT 两种显式指定方式。
        // 数据的去向必须由用户决定。
    }

    /// <summary>解析 <c>--data-root</c> 并应用。</summary>
    private static void ApplyDataRootOverride(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            string? value = null;

            if (arg.StartsWith(DataRootOption + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(DataRootOption.Length + 1)..];
            }
            else if (arg.Equals(DataRootOption, StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length)
            {
                value = args[i + 1];
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            try
            {
                if (!Directory.Exists(value))
                {
                    Directory.CreateDirectory(value);
                }

                AppPaths.OverrideDataRoot(value);
            }
            catch (Exception ex)
            {
                // 设置失败就退回默认位置 —— 不要在启动阶段弹窗打断用户
                MessageBox.Show(
                    $"指定的数据目录不可用,将使用默认位置。\n\n" +
                    $"目录:{value}\n原因:{ex.GetType().Name}: {ex.Message}",
                    "Twice1200",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }
    }
}
