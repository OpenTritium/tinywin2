using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Navigation;

namespace TinyWin2.Gui.Pages;

public sealed partial class ResultPage {
    public ResultPage() {
        InitializeComponent();
    }

    private WizardState State => WizardState.Current;

    protected override void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        SuccessPanel.Visibility = State.BuildSucceeded ? Visibility.Visible : Visibility.Collapsed;
        FailurePanel.Visibility = State.BuildSucceeded ? Visibility.Collapsed : Visibility.Visible;

        var artifacts = new List<string>();
        if (State.OutputPath.Length > 0) {
            artifacts.Add("输出:   " + State.OutputPath);
        }

        if (State.ManifestPath.Length > 0) {
            artifacts.Add("清单:   " + State.ManifestPath);
        }

        ArtifactsText.Text = string.Join(Environment.NewLine, artifacts);
        LayerSummaryText.Text = State.LayerCount > 0 ? $"层链：{State.LayerCount} 个已提交层（详见 manifest）" : "";

        FailureHint.Text = $"""
                            # 查看层链（每层对应的 plan 与状态）
                            tinywin2 layer list --workspace "{State.WorkspacePath}"

                            # 对比失败层前后差异（文件 + 注册表语义级）
                            tinywin2 layer diff --workspace "{State.WorkspacePath}" --from <N-1> --to <N>

                            # 排除失败项后重试：去掉对应的 --plan / 在第 2 页取消勾选
                            """;
    }

    private void OpenFolder(object sender, RoutedEventArgs e) {
        var target = Path.GetDirectoryName(State.OutputPath) ?? "";
        if (Directory.Exists(target)) {
            _ = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
    }

    private void CopyDiagnostics(object sender, RoutedEventArgs e) {
        var text = $"""
                    TinyWin2 构建诊断
                    成功: {State.BuildSucceeded}
                    源: {State.SourcePath} (index {State.SelectedIndex?.Index})
                    输出格式: {State.OutputFormat}, 原子 Plan: 是, 快速: {State.Fast}
                    输出: {State.OutputPath}
                    工作目录: {State.WorkspacePath}
                    manifest: {State.ManifestPath}
                    已选 plan: {string.Join(", * ", State.Plans.Where(p => p.IsSelected).Select(p => p.Id))}
                    """;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Restart(object sender, RoutedEventArgs e) {
        State.CurrentPhase = "";
        ((MainWindow)App.MainAppWindow!).GoTo(1);
    }
}
