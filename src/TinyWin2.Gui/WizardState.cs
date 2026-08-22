using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TinyWin2.Core.Plans;

namespace TinyWin2.Gui;

/// <summary>UI-facing catalog item with selection + parameter state.</summary>
public partial class PlanItemViewModel : ObservableObject {
    public PlanItemViewModel(PlanDefinition definition) {
        Definition = definition;
        foreach (var parameter in definition.Parameters) {
            Parameters.Add(new ParameterViewModel(parameter));
        }
        foreach (var parameter in Parameters) {
            parameter.ValueChanged += () => OnPropertyChanged(nameof(Summary));
        }
    }

    private PlanDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Title => Definition.Title;
    public string Category => Definition.Category;
    public string RiskLevel => Definition.RiskLevel;
    public string Description => Definition.Description;
    public ObservableCollection<ParameterViewModel> Parameters { get; } = [];

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public string Summary => Parameters.Count == 0
        ? ""
        : string.Join(" · ", Parameters.Select(p => $"{p.Label}: {p.Display}"));

    public string RiskBadge => RiskLevel switch {
        "High" => "‼高",
        "Medium" => "!中",
        _ => "·低",
    };

    public Microsoft.UI.Xaml.Media.Brush RiskBrush => RiskLevel switch {
        "High" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkRed),
        "Medium" => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkGoldenrod),
        _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkGreen),
    };

    partial void OnIsSelectedChanged(bool value) => SelectedChanged?.Invoke();

    public event Action? SelectedChanged;
}

public partial class ParameterViewModel : ObservableObject {
    public ParameterViewModel(PlanParameter parameter) {
        Parameter = parameter;
        SelectedValue = parameter.Options.Count > 0
            ? parameter.Options[0].Value
            : parameter.Default?.ToString() ?? "";
        foreach (var option in parameter.Options) {
            if (parameter.Default is { } d && d.ToString() == option.Value) {
                SelectedValue = option.Value;
            }
        }
    }

    private PlanParameter Parameter { get; }
    public string Name => Parameter.Name;
    public string Label => Parameter.Label;
    public string Type => Parameter.Type.ToString().ToLowerInvariant();
    public IReadOnlyList<PlanParameterOption> Options => Parameter.Options;

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
    public string OutputFormat { get; set; } = "esd"; // wim | esd | vhdx
    public bool CreateIso { get; set; } = true;
    public bool Fast { get; set; }

    public ObservableCollection<PlanItemViewModel> Plans { get; } = [];

    // Progress + result state filled by the CLI event stream on ProgressPage.
    public string CurrentPhase { get; set; } = "";
    public bool BuildSucceeded { get; set; }
    public string MediaPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string? IsoPath { get; set; }
    public string ManifestPath { get; set; } = "";
    public int LayerCount { get; set; }

    public List<(string PlanId, Dictionary<string, object?> Parameters)> CollectSelections() {
        var result = new List<(string, Dictionary<string, object?>)>();
        foreach (var plan in Plans.Where(p => p.IsSelected)) {
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var parameter in plan.Parameters) {
                parameters[parameter.Name] = parameter.Type switch {
                    "int" when int.TryParse(parameter.SelectedValue, out var n) => n,
                    "bool" when bool.TryParse(parameter.SelectedValue, out var b) => b,
                    _ => parameter.SelectedValue,
                };
            }
            result.Add((plan.Id, parameters));
        }
        return result;
    }
}
