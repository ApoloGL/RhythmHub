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
            Console.WriteLine($"[WinUI UnhandledException] {e.Message}\n{e.Exception}");
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            Console.WriteLine($"[AppDomain UnhandledException] {e.ExceptionObject}");
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
