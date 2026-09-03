using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace RhythmHub;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();

        UnhandledException += (sender, e) =>
        {
            System.IO.File.WriteAllText("crash.txt", $"[WinUI] {e.Message}\n{e.Exception}");
            e.Handled = false; // Let it crash properly
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            System.IO.File.WriteAllText("crash2.txt", $"[AppDomain] {e.ExceptionObject}");
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Console.WriteLine($"[TaskScheduler UnobservedException] {e.Exception}");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
