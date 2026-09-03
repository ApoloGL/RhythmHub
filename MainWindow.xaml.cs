using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RhythmHub.ViewModels;
using RhythmHub.Managers;

namespace RhythmHub;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private AppWindow? _appWindow;

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        this.InitializeComponent();

        // 1. Extend content into title bar and set custom drag area
        this.ExtendsContentIntoTitleBar = true;
        this.SetTitleBar(AppTitleBar);

        // 2. Configure AppWindow caption buttons & taskbar icon
        SetupTitleBarAndIcon();
    }

    private void SetupTitleBarAndIcon()
    {
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowId wndId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(wndId);

        if (_appWindow != null)
        {
            // Set Taskbar & Window Icon
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogo.ico");
            if (File.Exists(iconPath))
            {
                _appWindow.SetIcon(iconPath);
            }

            // Customize TitleBar Caption Buttons colors to match dark theme
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                var titleBar = _appWindow.TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 45, 45, 56); // #2D2D38
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 60, 60, 75);
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
            }
        }
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        await CheckDependenciesAsync();
    }

    private async Task CheckDependenciesAsync()
    {
        await Task.Delay(500);

        bool isInstalled = await DependencyInstaller.IsViGEmBusInstalledAsync();
        if (!isInstalled)
        {
            var dialog = new ContentDialog
            {
                Title = "Optional Driver: ViGEmBus",
                Content = "RhythmHub uses the ViGEmBus driver to emulate virtual Xbox 360 controllers for Xbox One guitars.\n\nNote: ViGEmBus is ONLY required for Xbox One guitar dongles. If you only use Wii or PS3 guitars, you can safely skip this installation.",
                PrimaryButtonText = "Install Driver",
                CloseButtonText = "Skip (Wii/PS3 Only)",
                XamlRoot = this.Content.XamlRoot
            };
            
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string? installerPath = DependencyInstaller.GetViGEmInstallerPath();
                if (!string.IsNullOrEmpty(installerPath))
                {
                    DependencyInstaller.InstallViGEmBus(installerPath);
                }
                else
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Installer Missing",
                        Content = "Could not find the ViGEmBus installer package in the prerequisites folder.",
                        CloseButtonText = "OK",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                }
            }
        }

        await ViewModel.InitializeAsync();
    }
}
