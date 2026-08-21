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

    private static async Task<JsonObject?> RunInspectAsync(string source) {
        JsonObject? parsed = null;
        var lines = new List<string>();
        var exit = await new CliRunner().RunAsync(
            ["inspect", source, "--json"],
            _ => { },
            lines.Add,
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
}
