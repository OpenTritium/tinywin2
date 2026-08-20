using Microsoft.UI.Xaml;
using Windows.ApplicationModel.Core;

namespace TinyWin2.Gui;

public partial class App : Application
{
    public static Window? MainAppWindow { get; private set; }

    public App()
    {
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            WriteCrashLog("InitializeComponent", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainAppWindow = new MainWindow();
            MainAppWindow.Activate();
        }
        catch (Exception ex)
        {
            WriteCrashLog("OnLaunched", ex);
            throw;
        }
    }

    private static void WriteCrashLog(string where, Exception ex)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "tinywin2-gui-crash.log"),
                $"{where}: {ex}");
        }
        catch
        {
            // no-op: crash logging must never throw
        }
    }
}
