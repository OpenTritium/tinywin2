using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;

namespace TinyWin2.Gui.Pages;

public sealed class RowItem {
    public string? Header { get; init; }
    public string Count => Plans.Count > 0 ? $"({Plans.Count})" : "";
    public PlanItemViewModel? Plan { get; init; }
    public List<PlanItemViewModel> Plans { get; init; } = [];
    public bool IsHeader => Plan is null;
}

public sealed class RowTemplateSelector : DataTemplateSelector {
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? PlanTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is RowItem { IsHeader: true } ? HeaderTemplate : PlanTemplate;
}

public sealed partial class ItemsPage : Page {
    private readonly ObservableCollection<RowItem> _rows = [];

    public ItemsPage() {
        InitializeComponent();
        LoadCatalog();
        PlanList.ItemsSource = _rows;
    }

    private WizardState State => WizardState.Current;

    private void LoadCatalog() {
        try {
            if (State.Catalog is null) {
                State.PlansDirectory = Services.RepositoryLocator.FindPlansDirectory();
                State.Catalog = PlanCatalog.LoadDirectory(State.PlansDirectory);
                foreach (var definition in State.Catalog.Plans) {
                    var item = new PlanItemViewModel(definition);
                    item.SelectedChanged += UpdateSelectionCount;
                    State.Plans.Add(item);
                }
            }
        }
        catch (Exception ex) {
            _ = new ContentDialog {
                Title = "加载 plan 目录失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }
        RebuildRows();
    }

    private void RebuildRows() {
        var search = (SearchBox.Text ?? "").Trim();
        var tier = (TierFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        bool Filter(PlanItemViewModel p) =>
            (tier == "all" || p.Tier == tier)
            && (search.Length == 0
                || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Id.Contains(search, StringComparison.OrdinalIgnoreCase));
        _rows.Clear();
        foreach (var group in State.Plans.Where(Filter).GroupBy(p => p.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) {
            var headerRow = new RowItem { Header = group.Key, Plans = [.. group] };
            _rows.Add(headerRow);
            foreach (var plan in group) {
                _rows.Add(new RowItem { Plan = plan, Plans = [plan] });
            }
        }
        UpdateSelectionCount();
    }

    private void UpdateSelectionCount() =>
        SelectionCount.Text = $"已选 {State.Plans.Count(p => p.IsSelected)} / {State.Plans.Count}";
}
