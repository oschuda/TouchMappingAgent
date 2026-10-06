using System.Windows;
using TouchMappingAgent.WPF.ViewModels;
using TouchMappingAgent.WPF.Views;

namespace TouchMappingAgent.WPF;

/// <summary>
/// Main application window for TouchMappingAgent WPF client.
/// </summary>
public partial class MainWindow : Window
{
    private LogWindow? _logWindow;

    /// <summary>Initializes a new instance of <see cref="MainWindow"/>.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Automatically loads the real monitor list as soon as the window is shown, so the user
    /// doesn't have to click "Laden" first.
    /// </summary>
    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorMappingViewModel viewModel)
            await viewModel.LoadMonitorsCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Opens the diagnostics log window (or brings the existing one to front), sharing this
    /// window's ViewModel so the log reflects everything logged since app start.
    /// </summary>
    private void ShowLogWindow_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorMappingViewModel viewModel)
            return;

        if (_logWindow == null || !_logWindow.IsLoaded)
        {
            _logWindow = new LogWindow(viewModel) { Owner = this };
            _logWindow.Show();
        }
        else
        {
            _logWindow.Activate();
        }
    }
}
