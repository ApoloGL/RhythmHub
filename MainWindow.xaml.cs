using Microsoft.UI.Xaml;
using RhythmHub.ViewModels;

namespace RhythmHub;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel();
        this.InitializeComponent();
    }
}
