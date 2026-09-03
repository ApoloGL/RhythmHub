using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RhythmHub.ViewModels;
using RhythmHub.Managers;

namespace RhythmHub;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        this.InitializeComponent();
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
                Title = "Missing Dependency",
                Content = "RhythmHub requires the Nefarius ViGEmBus driver to emulate Xbox controllers. Click Install to proceed.",
                PrimaryButtonText = "Install",
                CloseButtonText = "Quit",
                XamlRoot = this.Content.XamlRoot
            };
            
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                string installerPath = Path.Combine(AppContext.BaseDirectory, "ViGEmBus_1.22.0_x64_x86_arm64.exe");
                if (File.Exists(installerPath))
                {
                    DependencyInstaller.InstallViGEmBus(installerPath);
                    bool isInstalledNow = await DependencyInstaller.IsViGEmBusInstalledAsync();
                    if (!isInstalledNow)
                    {
                        Application.Current.Exit();
                        return;
                    }
                }
                else
                {
                    var errorDialog = new ContentDialog
                    {
                        Title = "Installer Missing",
                        Content = $"Could not find the installer at {installerPath}",
                        CloseButtonText = "Quit",
                        XamlRoot = this.Content.XamlRoot
                    };
                    await errorDialog.ShowAsync();
                    Application.Current.Exit();
                    return;
                }
            }
            else
            {
                Application.Current.Exit();
                return;
            }
        }

        await ViewModel.InitializeAsync();
    }
}
