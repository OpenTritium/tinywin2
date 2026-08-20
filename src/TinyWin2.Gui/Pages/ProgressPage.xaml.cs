using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TinyWin2.Gui.Services;

namespace TinyWin2.Gui.Pages;

/// <summary>LogLine with a per-level brush (kept here so WizardState stays UI-free).</summary>
public sealed class ColoredLogLine {
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "info";
    public string Message { get; init; } = "";
    public string Display => $"{Timestamp:HH:mm:ss} {Message}";
    public Brush Brush => Level switch {
        "warn" => new SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod),
        "error" => new SolidColorBrush(Microsoft.UI.Colors.DarkRed),
        "debug" => new SolidColorBrush(Microsoft.UI.Colors.Gray),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Black),
    };
}

public sealed partial class ProgressPage : Page {
    private readonly ObservableCollection<ColoredLogLine> _lines = [];
    private CancellationTokenSource? _cts;
    private bool _finished;

    public ProgressPage() {
        InitializeComponent();
        LogList.ItemsSource = _lines;
    }

    private WizardState State => WizardState.Current;

    protected override async void OnNavigatedTo(NavigationEventArgs e) {
        base.OnNavigatedTo(e);
        if (_finished) {
            return; // back-navigation guard: do not restart a finished build
        }
        _cts = new CancellationTokenSource();
        await RunBuildAsync(_cts.Token);
    }

    private List<string> BuildArguments() {
        var arguments = new List<string>
        {
            "build",
            "-s", State.SourcePath,
            "-i", State.SelectedIndex!.Index.ToString(),
            "--out", State.OutputMode,
            "--granularity", State.Granularity,
            "--json-events",
        };
        if (State.OutputRoot.Length > 0) {
            arguments.AddRange(["-o", State.OutputRoot]);
        }
        if (State.Fast) {
            arguments.Add("--fast");
        }
        foreach (var (planId, args) in State.CollectSelections()) {
            arguments.AddRange(["--plan", planId]);
            foreach (var (name, value) in args) {
                arguments.AddRange(["--set", $"{planId}.{name}={value}"]);
            }
        }
        return arguments;
    }

    private async Task RunBuildAsync(CancellationToken ct) {
        State.LogLines.Clear();
        var dispatcher = DispatcherQueue;
        var arguments = BuildArguments();
        void HandleEvent(JsonObject evt) {
            dispatcher.TryEnqueue(() => {
                var phase = evt["phase"]?.GetValue<string>();
                var level = evt["level"]?.GetValue<string>() ?? "info";
                var message = evt["message"]?.GetValue<string>() ?? "";
                if (evt["data"]?["progress"] is { } progressNode && progressNode.GetValue<int>() is var pct) {
                    Progress.Value = pct;
                }
                if (phase is "result") {
                    var data = evt["data"]?.AsObject();
                    State.BuildSucceeded = data?["succeeded"]?.GetValue<bool>() ?? false;
                    State.MediaPath = data?["mediaPath"]?.GetValue<string>() ?? "";
                    State.IsoPath = data?["isoPath"]?.GetValue<string>();
                    State.VhdxPath = data?["vhdxPath"]?.GetValue<string>();
                    State.ManifestPath = data?["manifestPath"]?.GetValue<string>() ?? "";
                    State.LayerCount = data?["layerCount"]?.GetValue<int>() ?? 0;
                    _lines.Add(new ColoredLogLine { Timestamp = DateTimeOffset.Now, Level = "info", Message = "构建结束。" });
                    return;
                }
                if (phase is not null && phase != State.CurrentPhase) {
                    State.CurrentPhase = phase;
                    PhaseText.Text = PhaseLabel(phase);
                }
                _lines.Add(new ColoredLogLine { Timestamp = DateTimeOffset.Now, Level = level, Message = message });
                if (_lines.Count > 2000) {
                    _lines.RemoveAt(0); // keep the log bounded during very long builds
                }
                LogList.ScrollIntoView(LogList.Items.LastOrDefault());
            });
        }
        var exitCode = await new CliRunner().RunAsync(
            arguments,
            HandleEvent,
            rawLine => { },
            ex => dispatcher.TryEnqueue(() => _lines.Add(new ColoredLogLine {
                Timestamp = DateTimeOffset.Now,
                Level = "error",
                Message = "CLI 运行失败: " + ex.Message,
            })),
            ct);
        _finished = true;
        State.BuildSucceeded = exitCode == 0 && State.BuildSucceeded;
        dispatcher.TryEnqueue(() => ((MainWindow)App.MainAppWindow!).GoTo(4));
    }

    private static string PhaseLabel(string phase) => phase switch {
        "prepare" => "准备（环境检查）",
        "media" => "解析源媒体",
        "base-layer" => "应用基础层（Apply 镜像）",
        "plan" => "逐层执行精简计划",
        "capture" => "捕获输出镜像",
        "package" => "重建媒体 / 生成 ISO",
        "done" => "完成",
        "failed" => "失败",
        _ => phase,
    };

    private void CancelBuild(object sender, RoutedEventArgs e) {
        _cts?.Cancel();
        PhaseText.Text = "正在取消…";
    }
}
