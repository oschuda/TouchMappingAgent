using System.Windows;
using TouchMappingAgent.WPF.ViewModels;

namespace TouchMappingAgent.WPF.Views;

/// <summary>
/// Guided commissioning wizard: rights check, EDID strategy, PnP verification, touch
/// assignment, calibration test and report export.
/// </summary>
public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardViewModel _viewModel;

    /// <summary>Initializes the window with its ViewModel.</summary>
    /// <param name="viewModel">ViewModel driving the wizard.</param>
    public SetupWizardWindow(SetupWizardViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        // The summary needs its verdict built when it is reached; doing it in the ViewModel's
        // step change would couple navigation to presentation, so the window asks for it.
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupWizardViewModel.CurrentStep) &&
                _viewModel.CurrentStep == SetupStep.Summary)
            {
                _viewModel.PrepareSummary();
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Run the pre-flight immediately: the operator opened the wizard to find out where they
        // stand, and making them press a button first only delays that.
        await _viewModel.RunPreFlightCommand.ExecuteAsync(null);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
