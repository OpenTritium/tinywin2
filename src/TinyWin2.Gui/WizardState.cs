using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin2.Core.Plans;

namespace TinyWin2.Gui;

/// <summary>UI-facing catalog item with selection + argument state.</summary>
public partial class PlanItemViewModel : ObservableObject {
    public PlanItemViewModel(PlanDefinition definition) {
        Definition = definition;
        foreach (var argument in definition.Arguments) {
            Arguments.Add(new ArgumentViewModel(argument));
        }
        foreach (var argument in Arguments) {
            argument.ValueChanged += () => OnPropertyChanged(nameof(Summary));
        }
    }

    public PlanDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Group => Definition.Group;
    public string Risk => Definition.Risk;
    public string Tier => Definition.Tier;
    public string Description => Definition.Description;
    public ObservableCollection<ArgumentViewModel> Arguments { get; } = [];

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Summary => Arguments.Count == 0
        ? ""
        : string.Join(" · ", Arguments.Select(a => $"{a.Label}: {a.Display}"));

    public string RiskBadge => Risk switch {
        "High" => "‼高",
        "Medium" => "!中",
        _ => "·低",
    };

    public Microsoft.UI.Xaml.Media.Brush RiskBrush => Risk switch {
        "High" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkRed),
        "Medium" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkGreen),
    };

    partial void OnIsSelectedChanged(bool value) => SelectedChanged?.Invoke();

    public event Action? SelectedChanged;
}

public partial class ArgumentViewModel : ObservableObject {
    public ArgumentViewModel(PlanArgument argument) {
        Argument = argument;
        SelectedValue = argument.Options.Count > 0
            ? argument.Options[0].Value
            : argument.Default?.ToString() ?? "";
        foreach (var option in argument.Options) {
            if (argument.Default is { } d && d.ToString() == option.Value) {
                SelectedValue = option.Value;
            }
        }
    }

    public PlanArgument Argument { get; }
    public string Name => Argument.Name;
    public string Label => Argument.Label;
    public string Type => Argument.Type.ToString().ToLowerInvariant();
    public IReadOnlyList<PlanArgumentOption> Options => Argument.Options;

    [ObservableProperty]
    public partial string SelectedValue { get; set; }

    public string Display => Type switch {
        "enum" => Options.FirstOrDefault(o => o.Value == SelectedValue)?.Label ?? SelectedValue,
        _ => SelectedValue,
    };

    partial void OnSelectedValueChanged(string value) => ValueChanged?.Invoke();

    public event Action? ValueChanged;
}

public sealed record ImageIndexItem(int Index, string Name, string EditionId, string Version, string Display);

/// <summary>Shared wizard state across the four pages.</summary>
public sealed class WizardState {
    public static WizardState Current { get; } = new();

    public PlanCatalog? Catalog { get; set; }
    public string PlansDirectory { get; set; } = "";

    public string SourcePath { get; set; } = "";
    public ObservableCollection<ImageIndexItem> ImageIndexes { get; } = [];
    public ImageIndexItem? SelectedIndex { get; set; }
    public string OutputRoot { get; set; } = "";
    public string OutputMode { get; set; } = "iso"; // wim | esd | iso | iso+vhdx
    public string Granularity { get; set; } = "group";
    public bool Fast { get; set; }
    public string? OscdimgPath { get; set; }

    public ObservableCollection<PlanItemViewModel> Plans { get; } = [];
    public ObservableCollection<PlanItemViewModel> VisiblePlans { get; } = [];

    // Progress + result state filled by CliRunner.
    public ObservableCollection<LogLine> LogLines { get; } = [];
    public string CurrentPhase { get; set; } = "";
    public int Progress { get; set; }
    public bool BuildSucceeded { get; set; }
    public string? FailedStep { get; set; }
    public int? FailedLayer { get; set; }
    public string WorkspacePath { get; set; } = "";
    public string MediaPath { get; set; } = "";
    public string? IsoPath { get; set; }
    public string? VhdxPath { get; set; }
    public string ManifestPath { get; set; } = "";
    public int LayerCount { get; set; }

    public List<(string PlanId, Dictionary<string, object?> Args)> CollectSelections() {
        var result = new List<(string, Dictionary<string, object?>)>();
        foreach (var plan in Plans.Where(p => p.IsSelected)) {
            var args = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var argument in plan.Arguments) {
                args[argument.Name] = argument.Type switch {
                    "int" when int.TryParse(argument.SelectedValue, out var n) => n,
                    "bool" when bool.TryParse(argument.SelectedValue, out var b) => b,
                    _ => argument.SelectedValue,
                };
            }
            result.Add((plan.Id, args));
        }
        return result;
    }
}

public sealed record LogLine(DateTimeOffset Timestamp, string Level, string Message) {
    public string Display => $"{Timestamp:HH:mm:ss} {Message}";
}
