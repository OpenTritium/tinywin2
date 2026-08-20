using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TinyWin2.Gui.Pages;
using Windows.UI;

namespace TinyWin2.Gui;

public sealed partial class MainWindow : Window
{
    private static readonly Color ActiveColor = Color.FromArgb(255, 0, 120, 212);
    private static readonly Color DoneColor = Color.FromArgb(255, 16, 124, 16);
    private static readonly Color IdleColor = Color.FromArgb(255, 160, 160, 160);

    public WizardState State => WizardState.Current;

    public MainWindow()
    {
        InitializeComponent();
        Title = "TinyWin2 — 分层镜像精简";
        RootFrame.Navigate(typeof(SourcePage));
        UpdateSteps(1, canGoBack: false);
    }

    public void GoTo(int step)
    {
        _ = step switch
        {
            1 => RootFrame.Navigate(typeof(SourcePage)),
            2 => RootFrame.Navigate(typeof(ItemsPage)),
            3 => RootFrame.Navigate(typeof(ProgressPage)),
            4 => RootFrame.Navigate(typeof(ResultPage)),
            _ => false,
        };
        UpdateSteps(step, canGoBack: false);
    }

    private void UpdateSteps(int current, bool canGoBack)
    {
        SetStep(Step1, current switch { 1 => StepState.Active, > 1 => StepState.Done, _ => StepState.Idle });
        SetStep(Step2, current switch { 2 => StepState.Active, > 2 => StepState.Done, _ => StepState.Idle });
        SetStep(Step3, current switch { 3 => StepState.Active, > 3 => StepState.Done, _ => StepState.Idle });
        SetStep(Step4, current switch { 4 => StepState.Active, _ => StepState.Idle });
    }

    private static void SetStep(StackPanel panel, StepState state)
    {
        var dot = (Ellipse)panel.Children[0];
        var text = (TextBlock)panel.Children[1];
        (dot.Fill, text.Foreground) = state switch
        {
            StepState.Active => (new SolidColorBrush(ActiveColor), new SolidColorBrush(ActiveColor)),
            StepState.Done => (new SolidColorBrush(DoneColor), new SolidColorBrush(DoneColor)),
            _ => (new SolidColorBrush(IdleColor), new SolidColorBrush(IdleColor)),
        };
        text.FontWeight = state == StepState.Active
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private enum StepState
    {
        Idle,
        Active,
        Done,
    }
}
