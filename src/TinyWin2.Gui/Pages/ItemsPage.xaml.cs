using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyWin2.Core.Plans;
using TinyWin2.Core.Profiles;
using Windows.UI;

namespace TinyWin2.Gui.Pages;

public sealed class RowItem
{
    public string? Header { get; init; }
    public string Count => Plans.Count > 0 ? $"({Plans.Count})" : "";
    public PlanItemViewModel? Plan { get; init; }
    public List<PlanItemViewModel> Plans { get; init; } = [];
    public bool IsHeader => Plan is null;
}

public sealed class RowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? PlanTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        item is RowItem { IsHeader: true } ? HeaderTemplate : PlanTemplate;
}

public sealed partial class ItemsPage : Page
{
    private readonly ObservableCollection<RowItem> _rows = [];

    public ItemsPage()
    {
        InitializeComponent();
        LoadCatalog();
        PlanList.ItemsSource = _rows;
    }

    private WizardState State => WizardState.Current;

    private void LoadCatalog()
    {
        try
        {
            if (State.Catalog is null)
            {
                State.PlansDirectory = Services.RepositoryLocator.FindPlansDirectory();
                State.Catalog = PlanCatalog.LoadDirectory(State.PlansDirectory);
                foreach (var definition in State.Catalog.Plans)
                {
                    var item = new PlanItemViewModel(definition);
                    item.SelectedChanged += UpdateSelectionCount;
                    State.Plans.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            _ = new ContentDialog
            {
                Title = "加载 plan 目录失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }
        RebuildRows();
    }

    private void RebuildRows()
    {
        var search = (SearchBox.Text ?? "").Trim();
        var tier = (TierFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";

        bool Filter(PlanItemViewModel p) =>
            (tier == "all" || p.Tier == tier)
            && (search.Length == 0
                || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.Id.Contains(search, StringComparison.OrdinalIgnoreCase));

        _rows.Clear();
        foreach (var group in State.Plans.Where(Filter).GroupBy(p => p.Group).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var headerRow = new RowItem { Header = group.Key, Plans = [.. group] };
            _rows.Add(headerRow);
            foreach (var plan in group)
            {
                _rows.Add(new RowItem { Plan = plan, Plans = [plan] });
            }
        }
        UpdateSelectionCount();
    }

    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => RebuildRows();

    private void TierChanged(object sender, SelectionChangedEventArgs e) => RebuildRows();

    private void SelectStandard(object sender, RoutedEventArgs e)
    {
        foreach (var plan in State.Plans)
        {
            plan.IsSelected = plan.Tier == "Standard";
        }
    }

    private void SelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var plan in State.Plans)
        {
            plan.IsSelected = false;
        }
    }

    private void GroupCheckChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { Tag: string header, IsChecked: { } checkedState } && State.Catalog is not null)
        {
            foreach (var plan in State.Plans.Where(p => p.Group == header))
            {
                plan.IsSelected = checkedState;
            }
        }
        UpdateSelectionCount();
    }

    private void PlanSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PlanList.SelectedItem is not RowItem { Plan: { } plan })
        {
            return;
        }
        DetailTitle.Text = plan.Title;
        DetailId.Text = $"{plan.Id} · {plan.Group} · 风险 {plan.Risk} · 档位 {plan.Tier}";
        DetailDescription.Text = plan.Description;

        ArgumentPanel.Children.Clear();
        foreach (var argument in plan.Arguments)
        {
            var header = new TextBlock { Text = argument.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            ArgumentPanel.Children.Add(header);
            if (argument.Type == "enum" && argument.Options.Count > 0)
            {
                var combo = new ComboBox { Width = 260 };
                foreach (var option in argument.Options)
                {
                    combo.Items.Add($"{option.Label}（风险 {option.Risk ?? "?"}）|{option.Value}");
                }
                var current = argument.Options.ToList().FindIndex(o => o.Value == argument.SelectedValue);
                combo.SelectedIndex = current >= 0 ? current : 0;
                combo.SelectionChanged += (_, _) =>
                {
                    if (combo.SelectedItem is string selected)
                    {
                        argument.SelectedValue = selected.Split('|')[^1];
                    }
                };
                ArgumentPanel.Children.Add(combo);
            }
            else
            {
                var box = new TextBox { Width = 260, Text = argument.SelectedValue };
                box.TextChanged += (_, _) => argument.SelectedValue = box.Text;
                ArgumentPanel.Children.Add(box);
            }
        }
    }

    private async void ImportProfile(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker { FileTypeFilter = { ".json" } };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        var dialog = new { FileName = file.Path };
        try
        {
            var profile = ProfileStore.Load(dialog.FileName);
            var unknown = ProfileStore.UnknownPlans(profile, State.Catalog!);
            foreach (var plan in State.Plans)
            {
                plan.IsSelected = false;
            }
            var applied = 0;
            foreach (var selection in profile.Selections.Where(s => s.Enabled))
            {
                var item = State.Plans.FirstOrDefault(p => p.Id == selection.PlanId);
                if (item is null)
                {
                    continue;
                }
                item.IsSelected = true;
                applied++;
                foreach (var argument in item.Arguments)
                {
                    if (selection.Args is not null && selection.Args.TryGetPropertyValue(argument.Name, out var value) && value is not null)
                    {
                        argument.SelectedValue = value.ToString() ?? "";
                    }
                }
            }
            var warning = unknown.Count > 0 ? $"\n\n缺失 {unknown.Count} 个 plan: {string.Join(", ", unknown.Take(5))}" : "";
            await new ContentDialog
            {
                Title = "Profile 已导入",
                Content = $"已启用 {applied} 个精简项。{warning}",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "导入失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
    }

    private async void ExportProfile(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedFileName = "my-profile",
            FileTypeChoices = { ["TinyWin2 Profile"] = new List<string> { ".json" } },
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainAppWindow!));
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }
        var dialog = new { FileName = file.Path };
        var selections = State.Plans
            .Where(p => p.IsSelected)
            .Select(p =>
            {
                var args = new JsonObject();
                foreach (var argument in p.Arguments)
                {
                    args[argument.Name] = JsonValue.Create(argument.SelectedValue);
                }
                return new ProfileSelection(p.Id, true, args);
            })
            .ToList();
        ProfileStore.Save(new Profile(
            Path.GetFileNameWithoutExtension(dialog.FileName),
            "导出自 TinyWin2 GUI",
            selections), dialog.FileName);
    }

    private void UpdateSelectionCount() =>
        SelectionCount.Text = $"已选 {State.Plans.Count(p => p.IsSelected)} / {State.Plans.Count}";

    private void GoBack(object sender, RoutedEventArgs e) => ((MainWindow)App.MainAppWindow!).GoTo(1);

    private void StartBuild(object sender, RoutedEventArgs e)
    {
        if (State.Plans.All(p => !p.IsSelected))
        {
            _ = new ContentDialog
            {
                Title = "尚未选择任何精简项",
                Content = "至少勾选一项，或导入一个 Profile。",
                CloseButtonText = "确定",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }
        ((MainWindow)App.MainAppWindow!).GoTo(3);
    }
}
