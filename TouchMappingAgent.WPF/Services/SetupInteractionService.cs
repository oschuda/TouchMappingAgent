using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Views;
using MessageBox = System.Windows.MessageBox;
// The project global-usings pull in System.Windows.Forms for the tray icon, which has its own
// file dialogs. The WPF ones are wanted here.
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// WPF implementation of the wizard's human-facing steps.
///
/// Everything here needs the UI thread, and everything here is what the tests replace with a
/// fake — which is the whole reason the interface exists.
/// </summary>
public sealed class SetupInteractionService : ISetupInteractionService
{
    private readonly ILogger<SetupInteractionService> _logger;
    private readonly ILocalizer _localizer;

    /// <summary>Initializes a new instance of <see cref="SetupInteractionService"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Supplies dialog titles and file filters.</param>
    public SetupInteractionService(ILogger<SetupInteractionService> logger, ILocalizer localizer)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <inheritdoc/>
    public async Task<string?> IdentifyTouchDeviceAsync(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        try
        {
            // Constructing and awaiting must both happen on the UI thread; the window's
            // completion source is signalled from its own window procedure.
            var task = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var window = new IdentifyWindow(monitor);
                window.Show();
                window.Activate();
                return window.TouchedDevicePathTask;
            });

            return await task;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Touch identification failed for {Connector}", monitor.ConnectorLabel);
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<bool> ConfirmAsync(string message, string title) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
                == MessageBoxResult.Yes).Task;

    /// <inheritdoc/>
    public Task ShowMessageAsync(string message, string title) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)).Task;

    /// <inheritdoc/>
    public Task<string?> PickTemplateFileAsync(string? initialDirectory) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = _localizer[LocalizationKeys.Interaction_PickTemplateTitle],
                Filter = _localizer[LocalizationKeys.Interaction_PickTemplateFilter],
                CheckFileExists = true
            };

            if (!string.IsNullOrWhiteSpace(initialDirectory))
                dialog.InitialDirectory = initialDirectory;

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }).Task;

    /// <inheritdoc/>
    public Task<string?> PickReportTargetAsync(string suggestedFileName) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = _localizer[LocalizationKeys.Interaction_SaveReportTitle],
                FileName = suggestedFileName,
                DefaultExt = ".json",
                Filter = _localizer[LocalizationKeys.Interaction_SaveReportFilter],
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }).Task;
}
