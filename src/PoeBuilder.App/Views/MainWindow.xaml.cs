using System.ComponentModel;
using System.Windows;
using PoeBuilder.App.ViewModels;

namespace PoeBuilder.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _closeApproved;
    private bool _closePending;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, Math.Max(640, area.Width - 24));
            MinHeight = Math.Min(MinHeight, Math.Max(480, area.Height - 24));
            Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 24));
            Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 24));
        };
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            Loaded += async (_, _) => await _viewModel.InitializeAsync();
            Closing += OnClosing;
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        try
        {
            if (await _viewModel.PrepareCloseAsync())
            {
                _closeApproved = true;
                // Defer the second Close until the original Closing event has unwound.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        finally { _closePending = false; }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ResetTreeClick(object sender, RoutedEventArgs e) => TreeSurface.Reset();
    private void ZoomInClick(object sender, RoutedEventArgs e) => TreeSurface.AdjustZoom(1.12);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => TreeSurface.AdjustZoom(1 / 1.12);
}
