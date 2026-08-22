using System.Text.Json.Nodes;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using TinyWin2.Gui.Services;
using WinRT.Interop;

namespace TinyWin2.Gui.Pages;

public sealed partial class SourcePage {
    private readonly bool _isInitialized;

    public SourcePage() {
        InitializeComponent();
        _isInitialized = true;
    }

    private WizardState State => WizardState.Current;

    private void PickInput(object sender, RoutedEventArgs e) => _ = PickInputAsync();

    private async Task PickInputAsync() {
        try {
            var picker = new FileOpenPicker {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                FileTypeFilter = { ".iso", ".wim", ".esd" }
            };
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(App.MainAppWindow!)
            );
            var file = await picker.PickSingleFileAsync();
            if (file is not null) {
                SourceBox.Text = file.Path;
                await RefreshIndexesAsync();
            }
        }
        catch (Exception ex) {
            HintText.Text = "读取失败: " + ex.Message;
        }
    }

    private void PickFolder(object sender, RoutedEventArgs e) => _ = PickFolderAsync();

    private async Task PickFolderAsync() {
        try {
            var picker = new FolderPicker {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
                FileTypeFilter = { "*" }
            };
            InitializeWithWindow.Initialize(
                picker,
                WindowNative.GetWindowHandle(App.MainAppWindow!)
            );
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) {
                SourceBox.Text = folder.Path;
                await RefreshIndexesAsync();
            }
        }
        catch (Exception ex) {
            HintText.Text = "读取失败: " + ex.Message;
        }
    }

    private void SourceBox_KeyDown(object sender, KeyRoutedEventArgs e) {
        if (e.Key == VirtualKey.Enter) {
            _ = RefreshIndexesAsync();
        }
    }

    private async Task RefreshIndexesAsync() {
        var source = SourceBox.Text.Trim();
        if (source.Length == 0 || (!File.Exists(source) && !Directory.Exists(source))) {
            HintText.Text = "源不存在";
            return;
        }

        State.SourcePath = source;
        IndexCombo.Items.Clear();
        IndexSpinner.IsActive = true;
        HintText.Text = "正在读取索引…";
        try {
            var json = await RunInspectAsync(source);
            var indexes = json?["indexes"] as JsonArray ?? [];
            State.ImageIndexes.Clear();
            foreach (var node in indexes.OfType<JsonObject>()) {
                var item = new ImageIndexItem(
                    node["index"]!.GetValue<int>(),
                    $"{node["index"]}: {node["name"]}"
                    + (node["editionId"] is not null ? $" [{node["editionId"]}]" : "")
                );
                State.ImageIndexes.Add(item);
                IndexCombo.Items.Add(item);
            }

            HintText.Text = State.ImageIndexes.Count > 0 ? $"{State.ImageIndexes.Count} 个索引" : "未读到索引";
            if (State.ImageIndexes.Count > 0) {
                IndexCombo.SelectedIndex = 0;
            }
        }
        catch (Exception ex) {
            HintText.Text = "读取失败: " + ex.Message;
        }
        finally {
            IndexSpinner.IsActive = false;
        }
    }

    private static async Task<JsonObject?> RunInspectAsync(string source) {
        JsonObject? parsed = null;
        var lines = new List<string>();
        var exit = await new CliRunner().RunAsync(
            ["inspect", "--input", source, "--json"],
            _ => { },
            lines.Add,
            _ => { },
            CancellationToken.None
        );
        var text = string.Join(Environment.NewLine, lines).Trim();
        if (text.StartsWith('{')) {
            try {
                parsed = JsonNode.Parse(text) as JsonObject;
            }
            catch {
                parsed = null;
            }
        }

        return exit == 0 ? parsed : throw new InvalidOperationException(text.Length > 300 ? text[..300] : text);
    }

    private void IndexSelected(object sender, SelectionChangedEventArgs e) {
        State.SelectedIndex = IndexCombo.SelectedItem as ImageIndexItem;
        UpdateNextEnabled();
    }

    private void OutputFormatSelected(object sender, SelectionChangedEventArgs e) {
        if (OutputFormatCombo.SelectedItem is ComboBoxItem { Tag: string format }) {
            State.OutputFormat = format;
            UpdateNextEnabled();
        }
    }

    private void FastToggled(object sender, RoutedEventArgs e) => State.Fast = FastToggle.IsOn;

    private void OutputChanged(object sender, TextChangedEventArgs e) {
        State.OutputPath = OutputBox.Text.Trim();
        UpdateNextEnabled();
    }

    private void WorkspaceChanged(object sender, TextChangedEventArgs e) {
        State.WorkspacePath = WorkspaceBox.Text.Trim();
        UpdateNextEnabled();
    }

    private void UpdateNextEnabled() {
        if (!_isInitialized) {
            return;
        }

        var expectedExtension = "." + State.OutputFormat;
        NextButton.IsEnabled = State.SelectedIndex is not null
                               && State.OutputPath.Length > 0
                               && State.WorkspacePath.Length > 0
                               && string.Equals(Path.GetExtension(State.OutputPath), expectedExtension,
                                   StringComparison.OrdinalIgnoreCase);
        if (State.OutputPath.Length > 0
            && !string.Equals(Path.GetExtension(State.OutputPath), expectedExtension,
                StringComparison.OrdinalIgnoreCase)) {
            HintText.Text = $"输出文件必须使用 {expectedExtension} 扩展名";
        }
    }

    private void GoNext(object sender, RoutedEventArgs e) => ((MainWindow)App.MainAppWindow!).GoTo(2);
}
