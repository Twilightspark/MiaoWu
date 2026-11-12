using MiaoWu.Core.Text;

namespace MiaoWu.App.ViewModels
{
    /// <summary>命令输出示例的元信息；P4 接真实执行结果时结构不变。</summary>
    public sealed class CommandOutputSample
    {
        public CommandOutputSample(string commandText, int exitCode, string durationText, string output)
        {
            CommandText = commandText;
            ExitCode = exitCode;
            DurationText = durationText;
            Output = output;
        }

        public string CommandText { get; }

        public int ExitCode { get; }

        public string DurationText { get; }

        public string Output { get; }
    }

    /// <summary>
    /// P2 的静态演示内容：diff 与命令输出先固定成样例，P4 接真实数据时只替换数据源，
    /// 中栏行号、配色、等宽排版与虚拟化结构不变。
    /// </summary>
    public static class SampleDocuments
    {
        private static readonly string DiffText =
            "diff --git a/src/MiaoWu.App/Views/ShellView.xaml b/src/MiaoWu.App/Views/ShellView.xaml\n" +
            "index 3f2a91c..9b4d1e0 100644\n" +
            "--- a/src/MiaoWu.App/Views/ShellView.xaml\n" +
            "+++ b/src/MiaoWu.App/Views/ShellView.xaml\n" +
            "@@ -118,18 +118,24 @@\n" +
            "             <Grid.RowDefinitions>\n" +
            "                 <RowDefinition Height=\"Auto\" />\n" +
            "                 <RowDefinition Height=\"*\" />\n" +
            "             </Grid.RowDefinitions>\n" +
            "-            <Border Grid.Row=\"1\" Background=\"{DynamicResource Brush.Canvas}\">\n" +
            "-                <TextBlock Text=\"预览占位\" Style=\"{StaticResource Text.Body}\" />\n" +
            "-            </Border>\n" +
            "+            <views:LeftPaneView Grid.Row=\"1\" DataContext=\"{Binding Left}\" />\n" +
            "+            <views:CanvasPaneView Grid.Row=\"1\" DataContext=\"{Binding Canvas}\" />\n" +
            "+            <views:OverlayView Grid.RowSpan=\"2\" DataContext=\"{Binding Overlay}\" />\n" +
            "         </Grid>\n" +
            "-    </Border>\n" +
            "+    </Grid>\n" +
            " </UserControl>\n" +
            "diff --git a/src/MiaoWu.App/ViewModels/LeftPaneViewModel.cs b/src/MiaoWu.App/ViewModels/LeftPaneViewModel.cs\n" +
            "index a1b2c3d..e4f5a6b 100644\n" +
            "--- a/src/MiaoWu.App/ViewModels/LeftPaneViewModel.cs\n" +
            "+++ b/src/MiaoWu.App/ViewModels/LeftPaneViewModel.cs\n" +
            "@@ -40,6 +40,12 @@ public sealed class LeftPaneViewModel : ObservableObject\n" +
            "         public ObservableCollection<WorkspaceItemViewModel> Workspaces\n" +
            "         {\n" +
            "             get { return _workspaces; }\n" +
            "         }\n" +
            "+\n" +
            "+        public WorkspaceItemViewModel ActiveWorkspace\n" +
            "+        {\n" +
            "+            get { return _activeWorkspace; }\n" +
            "+        }\n" +
            "     }\n" +
            "\\ No newline at end of file\n";

        private static readonly string CommandOutput =
            "活动连接\n" +
            "  协议  本地地址          外部地址        状态           PID\n" +
            "  TCP    0.0.0.0:135            0.0.0.0:0              LISTENING       1024\n" +
            "  TCP    0.0.0.0:445            0.0.0.0:0              LISTENING       4\n" +
            "  TCP    0.0.0.0:902            0.0.0.0:0              LISTENING       2764\n" +
            "  TCP    0.0.0.0:912            0.0.0.0:0              LISTENING       2764\n" +
            "  TCP    0.0.0.0:5040           0.0.0.0:0              LISTENING       7284\n" +
            "  TCP    0.0.0.0:5357           0.0.0.0:0              LISTENING       4\n" +
            "  TCP    0.0.0.0:7680           0.0.0.0:0              LISTENING       17240\n" +
            "  TCP    0.0.0.0:49664          0.0.0.0:0              LISTENING       768\n" +
            "  TCP    0.0.0.0:49665          0.0.0.0:0              LISTENING       908\n" +
            "  TCP    127.0.0.1:5939         0.0.0.0:0              LISTENING       4712\n" +
            "  TCP    127.0.0.1:6463         0.0.0.0:0              LISTENING       11344\n" +
            "  TCP    192.168.1.42:139       0.0.0.0:0              LISTENING       4\n" +
            "  TCP    192.168.1.42:49670     0.0.0.0:0              LISTENING       908\n";

        public static DiffDocument CreateDiff()
        {
            return UnifiedDiffParser.Parse(DiffText);
        }

        public static CommandOutputSample CreateCommand()
        {
            return new CommandOutputSample(
                "netstat -ano | findstr LISTENING",
                0,
                "0.42s",
                CommandOutput);
        }
    }
}
