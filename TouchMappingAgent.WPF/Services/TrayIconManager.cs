using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.ViewModels;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Manages system tray icon (NotifyIcon) for TouchMappingAgent.
/// Provides quick access to configuration and service status monitoring.
/// (IEC 62443: Admin interface security, Windows Forms integration for tray)
/// </summary>
public class TrayIconManager : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private ContextMenuStrip? _contextMenu;
    private Views.EdidManagerWindow? _edidWindow;
    private Views.SetupWizardWindow? _wizardWindow;
    private Views.HelpWindow? _helpWindow;
    private Views.AboutWindow? _aboutWindow;
    private Icon? _activeIcon;
    private Icon? _inactiveIcon;
    private bool _isShuttingDown;
    private readonly MainWindow _mainWindow;
    private readonly MonitorMappingViewModel _viewModel;
    private readonly ILogger<TrayIconManager> _logger;
    private readonly IAgentWindowFactory _windowFactory;
    private readonly ILocalizer _localizer;
    private const string TrayIconText = "TouchMappingAgent";

    /// <summary>Initializes a new instance of <see cref="TrayIconManager"/>.</summary>
    /// <param name="mainWindow">The main WPF window to show/hide.</param>
    /// <param name="viewModel">ViewModel providing binding context for the main window.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="windowFactory">Creates the secondary windows on demand.</param>
    /// <param name="localizer">Supplies the menu text and the language picker.</param>
    public TrayIconManager(
        MainWindow mainWindow,
        MonitorMappingViewModel viewModel,
        ILogger<TrayIconManager> logger,
        IAgentWindowFactory windowFactory,
        ILocalizer localizer)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>
    /// Initializes and displays the system tray icon.
    /// Must be called on the UI thread.
    /// </summary>
    public void Initialize()
    {
        try
        {
            _notifyIcon = new NotifyIcon
            {
                Visible = true,
                Text = TrayIconText,
                Icon = GetDefaultIcon(),
                ContextMenuStrip = CreateContextMenu()
            };

            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();
            _notifyIcon.Click += (s, e) => HandleIconClick(e);

            // Fire and forget with proper error handling
            _ = UpdateServiceStatusIconAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrayIconManager.Initialize failed");
        }
    }

    /// <summary>
    /// Creates the context menu for the tray icon with service control options.
    /// (MVVM: Commands delegated to ViewModel)
    /// </summary>
    private ContextMenuStrip CreateContextMenu()
    {
        _contextMenu = new ContextMenuStrip { ShowItemToolTips = true };

        var openConfig = MenuItem(LocalizationKeys.Tray_OpenConfig, LocalizationKeys.Tip_OpenConfig);
        openConfig.Click += (s, e) => ShowMainWindow();
        _contextMenu.Items.Add(openConfig);

        var applyNow = MenuItem(LocalizationKeys.Tray_ApplyNow, LocalizationKeys.Tip_ApplyNow);
        applyNow.Click += async (s, e) => await ApplyMappingsNowAsync();
        _contextMenu.Items.Add(applyNow);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var wizard = MenuItem(LocalizationKeys.Tray_Wizard, LocalizationKeys.Tip_Wizard);
        wizard.Click += (s, e) => ShowSetupWizard();
        _contextMenu.Items.Add(wizard);

        var edidManager = MenuItem(LocalizationKeys.Tray_EdidManager, LocalizationKeys.Tip_EdidManager);
        edidManager.Click += (s, e) => ShowEdidManager();
        _contextMenu.Items.Add(edidManager);

        var help = MenuItem(LocalizationKeys.Tray_Help, LocalizationKeys.Tip_Help);
        help.Click += (s, e) => ShowHelp();
        _contextMenu.Items.Add(help);

        _contextMenu.Items.Add(BuildDiagnoseMenu());

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(BuildLanguageMenu());

        _contextMenu.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_About]);
        about.Click += (s, e) => ShowAboutDialog();
        _contextMenu.Items.Add(about);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exit = MenuItem(LocalizationKeys.Tray_Exit, LocalizationKeys.Tip_Exit);
        exit.Click += (s, e) => ExitApplication();
        _contextMenu.Items.Add(exit);

        return _contextMenu;
    }

    private ToolStripMenuItem MenuItem(string textKey, string tipKey) =>
        new(_localizer[textKey]) { ToolTipText = _localizer[tipKey] };

    /// <summary>
    /// Builds the "Diagnose" submenu: things a service technician needs, not the operator.
    ///
    /// The former "reset console session" entry is gone on purpose: it logged the operator's own
    /// session off — which the Start menu does just as well — and after the watchdog had forced
    /// unwanted sign-outs in the field, a one-click logoff in the tray was a risk without a use.
    /// </summary>
    private ToolStripMenuItem BuildDiagnoseMenu()
    {
        var menu = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_DiagnoseMenu]);
        menu.DropDown.ShowItemToolTips = true;

        var exportLog = MenuItem(LocalizationKeys.Tray_ExportLog, LocalizationKeys.Tip_ExportLog);
        exportLog.Click += async (s, e) => await ExportDiagnosticsAsync();
        menu.DropDownItems.Add(exportLog);

        var checkStatus = MenuItem(LocalizationKeys.Tray_CheckStatus, LocalizationKeys.Tip_CheckStatus);
        checkStatus.Click += async (s, e) => await CheckServiceStatusAsync(showResult: true);
        menu.DropDownItems.Add(checkStatus);

        return menu;
    }

    /// <summary>
    /// "Apply mapping now": the service rewrites Windows' touch routing and restarts the
    /// digitizers. The result is shown as a balloon, because with a silent start there is no
    /// window in which a status line could appear.
    /// </summary>
    private async Task ApplyMappingsNowAsync()
    {
        try
        {
            await _viewModel.ApplyMappingsNowCommand.ExecuteAsync(null);
            ShowBalloon(_viewModel.StatusMessage ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply-now from the tray failed");
        }
    }

    private void ShowBalloon(string text)
    {
        if (_notifyIcon == null || string.IsNullOrWhiteSpace(text))
            return;

        _notifyIcon.BalloonTipTitle = TrayIconText;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(5000);
    }

    /// <summary>
    /// Builds the language submenu from the languages the build actually ships.
    ///
    /// It lives in the tray menu rather than a settings window because that is the one piece of
    /// UI reachable in every start mode: with --silent there is no window at all, and a technician
    /// arriving at a station running in a language they do not read needs to be able to switch
    /// before opening anything else.
    /// </summary>
    private ToolStripMenuItem BuildLanguageMenu()
    {
        var menu = new ToolStripMenuItem(_localizer[LocalizationKeys.LanguageMenuHeader])
        {
            ToolTipText = _localizer[LocalizationKeys.Tip_Language]
        };

        foreach (var culture in _localizer.AvailableCultures)
        {
            // The language's own name, not its name in the current UI language: someone who
            // cannot read the current language must still recognise their own entry.
            var item = new ToolStripMenuItem(culture.NativeName)
            {
                Checked = string.Equals(
                    culture.TwoLetterISOLanguageName,
                    _localizer.CurrentCulture.TwoLetterISOLanguageName,
                    StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false,
                Tag = culture
            };

            item.Click += (_, _) =>
            {
                _localizer.SetCulture(culture);

                // The menu itself is WinForms and is not bound to LocalizationSource, so its own
                // captions do not follow the switch. Rebuild it rather than leaving the previous
                // language's labels next to newly-translated windows.
                if (_notifyIcon != null)
                    _notifyIcon.ContextMenuStrip = CreateContextMenu();
            };

            menu.DropDownItems.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// Brings the main window up because another launch of the agent asked for it — the
    /// single-instance hand-off. Public because App wires it to that event.
    /// </summary>
    public void ShowMainWindowFromExternalRequest() => ShowMainWindow();

    /// <summary>
    /// Displays or brings MainWindow to foreground.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            // Show() rather than only setting Visibility: the window is hidden via Hide() when
            // the operator clicks "X", and after a silent start it was never shown at all — in
            // both cases a hidden window has to be re-shown, not merely made visible.
            _mainWindow.Show();
            _mainWindow.Visibility = Visibility.Visible;

            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;

            _mainWindow.Activate();
            _mainWindow.Focus();
        });
    }

    /// <summary>
    /// Checks service status via Named Pipe (async, non-blocking).
    /// Updates tray icon color/tooltip based on status.
    /// (IEC 62443: Asynchronous to prevent UI freezing, DoS protection)
    /// </summary>
    private async Task CheckServiceStatusAsync(bool showResult = false)
    {
        string status;
        try
        {
            if (_notifyIcon == null) return;

            _notifyIcon.Text = _localizer[LocalizationKeys.Tray_CheckingStatus];

            // Asynchronous service status check. CheckServiceStatus() never throws (see its
            // doc comment), so the outcome is read from IsServiceReachable rather than from
            // which task in WhenAny completed first — comparing task references here would
            // treat "failed fast" the same as "succeeded fast", which is wrong either way.
            var statusCheckTask = _viewModel.CheckServiceStatusCommand.ExecuteAsync(null);
            await Task.WhenAny(statusCheckTask, Task.Delay(5000));

            if (statusCheckTask.IsCompleted && _viewModel.IsServiceReachable)
            {
                status = _localizer[LocalizationKeys.Tray_StatusReachable];
                _notifyIcon.Icon = GetActiveIcon();
            }
            else
            {
                // "Not reachable", not "stopped": a missing answer does not tell which it is.
                status = _localizer[LocalizationKeys.Tray_StatusUnreachable];
                _notifyIcon.Icon = GetInactiveIcon();
            }
        }
        catch (Exception ex)
        {
            status = _localizer[LocalizationKeys.Tray_StatusUnknown];
            _logger.LogWarning(ex, "Tray service status check failed");
        }

        if (_notifyIcon == null)
            return;

        // NotifyIcon.Text is limited to 127 characters.
        var tooltip = $"{TrayIconText} - {status}";
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        if (showResult)
            ShowBalloon(status);
    }

    /// <summary>
    /// Updates the tray icon based on current service status.
    /// Called on startup and periodically.
    /// Async implementation with proper exception handling (W-5 fix).
    /// </summary>
    private async Task UpdateServiceStatusIconAsync()
    {
        try
        {
            await CheckServiceStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray service status icon update failed");
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = $"{TrayIconText} - Service unavailable";
            }
        }
    }

    /// <summary>
    /// Handles single-click on tray icon (context menu display on right-click is automatic).
    /// </summary>
    private void HandleIconClick(EventArgs e)
    {
        if (e is MouseEventArgs me && me.Button == MouseButtons.Left)
        {
            ShowMainWindow();
        }
    }

    private Icon GetDefaultIcon() => TrayIconFactory.LoadProductIcon();

    /// <summary>
    /// Gets the "service reachable" icon: the product icon with a green status dot.
    /// </summary>
    private Icon GetActiveIcon() =>
        _activeIcon ??= BuildBadgedIcon(TrayIconFactory.ReachableBadge);

    /// <summary>
    /// Gets the "service unreachable" icon: the product icon with an amber status dot.
    /// </summary>
    private Icon GetInactiveIcon() =>
        _inactiveIcon ??= BuildBadgedIcon(TrayIconFactory.UnreachableBadge);

    /// <summary>
    /// Builds a badged icon, degrading to the plain product icon if anything goes wrong —
    /// a cosmetic failure must never cost the tray icon entirely.
    /// </summary>
    private Icon BuildBadgedIcon(Color badgeColor)
    {
        try
        {
            return TrayIconFactory.BuildBadgedIcon(badgeColor);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build the badged tray icon; using the plain product icon");
            return TrayIconFactory.LoadProductIcon();
        }
    }

    /// <summary>
    /// Closes the application — but only after the operator has confirmed they understand
    /// what stops working. Answering "Nein" aborts the shutdown entirely.
    /// </summary>
    private void ExitApplication()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var answer = MessageBox.Show(
                _localizer[LocalizationKeys.Tray_ExitWarning],
                _localizer[LocalizationKeys.Tray_ExitConfirmTitle],
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                _logger.LogInformation("Shutdown cancelled by the operator at the exit warning");
                return;
            }

            _logger.LogWarning(
                "Operator confirmed shutdown of the touch mapping agent. Stored assignments will " +
                "NOT be re-applied after the next restart or DisplayPort event until the agent " +
                "is started again.");

            ShutdownApplication();
        });
    }

    /// <summary>
    /// Performs the actual shutdown. Split out from <see cref="ExitApplication"/> so the
    /// confirmation cannot be bypassed, and so shutting down really does close the window:
    /// MainWindow's Closing handler cancels a plain Close() to keep the agent resident.
    /// </summary>
    private void ShutdownApplication()
    {
        _isShuttingDown = true;

        if (_notifyIcon != null)
            _notifyIcon.Visible = false;

        _mainWindow?.Close();
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Opens the EDID &amp; display manager, reusing the existing window when it is already up.
    /// </summary>
    private void ShowEdidManager()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (_edidWindow is { IsLoaded: true })
                {
                    _edidWindow.Activate();
                    return;
                }

                _edidWindow = _windowFactory.CreateEdidManagerWindow();
                _edidWindow.Closed += (_, _) => _edidWindow = null;
                _edidWindow.Show();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the EDID manager");
                MessageBox.Show(
                    _localizer[LocalizationKeys.Tray_EdidManagerOpenFailed],
                    _localizer[LocalizationKeys.Tray_Caption], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    /// <summary>
    /// Opens the guided commissioning wizard, reusing an existing window when one is up.
    /// </summary>
    public void ShowSetupWizard()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (_wizardWindow is { IsLoaded: true })
                {
                    _wizardWindow.Activate();
                    return;
                }

                _wizardWindow = _windowFactory.CreateSetupWizardWindow();
                _wizardWindow.Closed += (_, _) => _wizardWindow = null;
                _wizardWindow.Show();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the setup wizard");
                MessageBox.Show(
                    _localizer[LocalizationKeys.Tray_WizardOpenFailed],
                    _localizer[LocalizationKeys.Tray_Caption], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    /// <summary>Opens the operator handbook.</summary>
    private void ShowHelp()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            try
            {
                if (_helpWindow is { IsLoaded: true })
                {
                    _helpWindow.Activate();
                    return;
                }

                _helpWindow = new Views.HelpWindow();
                _helpWindow.Closed += (_, _) => _helpWindow = null;
                _helpWindow.Show();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not open the help window");
            }
        });
    }

    /// <summary>
    /// Writes a diagnostics report the operator can attach to a support case.
    /// </summary>
    private async Task ExportDiagnosticsAsync()
    {
        string? targetPath = null;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = _localizer[LocalizationKeys.Tray_ExportDialogTitle],
                FileName = DiagnosticsExporter.SuggestedFileName,
                DefaultExt = ".log",
                Filter = _localizer[LocalizationKeys.Tray_ExportDialogFilter],
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            if (dialog.ShowDialog() == true)
                targetPath = dialog.FileName;
        });

        if (string.IsNullOrWhiteSpace(targetPath))
            return;

        var exporter = _windowFactory.CreateDiagnosticsExporter();
        bool ok = await exporter.ExportAsync(targetPath!);

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (ok)
            {
                MessageBox.Show(
                    string.Format(_localizer[LocalizationKeys.Tray_ExportSucceeded], targetPath),
                    _localizer[LocalizationKeys.Tray_Caption], MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(
                    _localizer[LocalizationKeys.Tray_ExportFailed],
                    _localizer[LocalizationKeys.Tray_Caption], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        });
    }

    /// <summary>
    /// Shows the About window with the product banner, copyright and developer information.
    /// </summary>
    private void ShowAboutDialog()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_aboutWindow is { IsLoaded: true })
            {
                _aboutWindow.Activate();
                return;
            }

            _aboutWindow = new Views.AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
        });
    }

    /// <summary>
    /// Handles the main window's Closing event: the "X" HIDES the window instead of ending the
    /// process, so the agent stays resident and keeps re-applying assignments. Only the tray
    /// menu's "Beenden" — which shows the exit warning first — really exits.
    /// </summary>
    public void HideToTray(CancelEventArgs e)
    {
        if (_mainWindow == null)
            return;

        // During a confirmed shutdown the window must genuinely close, otherwise the process
        // would never terminate.
        if (_isShuttingDown)
            return;

        e.Cancel = true;
        Application.Current.Dispatcher.Invoke(() => _mainWindow.Hide());

        _logger.LogInformation("Main window hidden to tray; agent stays resident");
    }

    /// <summary>
    /// Disposes resources (NotifyIcon, ContextMenu).
    /// </summary>
    public void Dispose()
    {
        _contextMenu?.Dispose();
        _notifyIcon?.Dispose();

        // Badged icons are built from an HICON and own it after cloning — leaking them leaks
        // a GDI handle per status change.
        _activeIcon?.Dispose();
        _inactiveIcon?.Dispose();

        GC.SuppressFinalize(this);
    }
}



