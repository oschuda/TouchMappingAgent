using System.Windows;
using System.Windows.Controls;
using TouchMappingAgent.WPF.ViewModels;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Small diagnostics window showing the full, unsanitized IPC error log with a one-click
/// copy button, so error details can be pasted directly instead of retyped from a screenshot.
/// </summary>
public partial class LogWindow : Window
{
    /// <summary>Initializes a new instance of <see cref="LogWindow"/> bound to the given ViewModel.</summary>
    /// <param name="viewModel">The shared ViewModel instance whose log this window displays.</param>
    public LogWindow(MonitorMappingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void LogTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }
}
