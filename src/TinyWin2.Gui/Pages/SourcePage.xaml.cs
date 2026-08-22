using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyWin2.Gui.Services;

namespace TinyWin2.Gui.Pages;

public sealed partial class SourcePage : Page {
    public SourcePage() {
        InitializeComponent();
    }

    private WizardState State => WizardState.Current;

    private async void PickIso(object sender, RoutedEventArgs e) {
        var picker = new Windows.Storage.Pickers.FileOpenPicker {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            FileTypeFilter = { ".iso" },
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) {
            SourceBox.Text = file.Path;
            await RefreshIndexesAsync();
        }
    }

    private async void PickFolder(object sender, RoutedEventArgs e) {
        var picker = new Windows.Storage.Pickers.FolderPicker {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            FileTypeFilter = { "*" },
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) {
            SourceBox.Text = folder.Path;
            await RefreshIndexesAsync();
        }
    }

    private async void SourceBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) {
        if (e.Key == Windows.System.VirtualKey.Enter) {
            await RefreshIndexesAsync();
        }
    }

    private async Task RefreshIndexesAsync() {
        var source = SourceBox.Text.Trim();
        if (source.Length == 0 || !File.Exists(source) && !Directory.Exists(source)) {
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
                    node["name"]?.GetValue<string>() ?? "",
                    node["editionId"]?.GetValue<string>() ?? "",
                    node["version"]?.GetValue<string>() ?? "",
                    $"{node["index"]}: {node["name"]}" + (node["editionId"] is not null ? $" [{node["editionId"]}]" : ""));
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
            ["inspect", source, "--json"],
            evt => { },
            line => lines.Add(line),
            _ => { },
            CancellationToken.None);
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
        if (OutputFormatCombo.SelectedItem is ComboBoxItem item && item.Tag is string format) {
            State.OutputFormat = format;
            var isVhdx = string.Equals(format, "vhdx", StringComparison.OrdinalIgnoreCase);
            IsoToggle.IsEnabled = !isVhdx;
            if (isVhdx) {
                State.CreateIso = false;
                IsoToggle.IsOn = false;
            }
        }
    }

    private void IsoToggled(object sender, RoutedEventArgs e) => State.CreateIso = IsoToggle.IsOn;

    private void FastToggled(object sender, RoutedEventArgs e) => State.Fast = FastToggle.IsOn;

    private void OutputChanged(object sender, TextChangedEventArgs e) => State.OutputRoot = OutputBox.Text.Trim();

    private void UpdateNextEnabled() {
        NextButton.IsEnabled = State.SelectedIndex is not null;
    }

    private void GoNext(object sender, RoutedEventArgs e) {
        ((MainWindow)App.MainAppWindow!).GoTo(2);
    }
}
