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
        _contextMenu = new ContextMenuStrip();

        var openConfig = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_OpenConfig]);
        openConfig.Click += (s, e) => ShowMainWindow();
        _contextMenu.Items.Add(openConfig);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var checkStatus = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_CheckStatus]);
        checkStatus.Click += async (s, e) =>
        {
            await CheckServiceStatusAsync();
        };
        _contextMenu.Items.Add(checkStatus);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var wizard = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_Wizard]);
        wizard.Click += (s, e) => ShowSetupWizard();
        _contextMenu.Items.Add(wizard);

        var edidManager = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_EdidManager]);
        edidManager.Click += (s, e) => ShowEdidManager();
        _contextMenu.Items.Add(edidManager);

        var help = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_Help]);
        help.Click += (s, e) => ShowHelp();
        _contextMenu.Items.Add(help);

        var exportLog = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_ExportLog]);
        exportLog.Click += async (s, e) => await ExportDiagnosticsAsync();
        _contextMenu.Items.Add(exportLog);

        _contextMenu.Items.Add(BuildDiagnoseMenu());

        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add(BuildLanguageMenu());

        _contextMenu.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_About]);
        about.Click += (s, e) => ShowAboutDialog();
        _contextMenu.Items.Add(about);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem(_localizer[LocalizationKeys.Tray_Exit]);
        exit.Click += (s, e) => ExitApplication();
        _contextMenu.Items.Add(exit);

        return _contextMenu;
    }

    /// <summary>
    /// Builds the "Diagnose" submenu: manual overrides for automatic recovery mechanisms that
    /// an operator may need to trigger by hand in the field. Currently holds just the console
    /// session watchdog's manual reset.
    ///
    /// NOT routed through <see cref="ILocalizer"/> like the rest of this menu: this is an
    /// operator/service-technician diagnostic entry, not part of the core localized UX, and
    /// wiring one more string through all shipped languages' .resx files was judged not worth
    /// it for a single admin-only menu entry. Revisit if this submenu grows.
    /// </summary>
    private ToolStripMenuItem BuildDiagnoseMenu()
    {
        var menu = new ToolStripMenuItem("Diagnose");

        var resetConsoleSession = new ToolStripMenuItem("Konsolen-Sitzung zurücksetzen");
        resetConsoleSession.Click += async (s, e) => await ForceConsoleSessionResetAsync();
        menu.DropDownItems.Add(resetConsoleSession);

        return menu;
    }

    /// <summary>
    /// Confirms with the operator, then invokes the manual console-session reset override.
    /// The confirmation lives here rather than in the ViewModel command because it is a tray-UI
    /// concern (this is the only entry point for it) and because WTSLogoffSession is a
    /// destructive, unconfirmable-once-fired action — an accidental menu click must not log the
    /// operator's own console off without a chance to back out first.
    /// </summary>
    private async Task ForceConsoleSessionResetAsync()
    {
        var confirmed = MessageBox.Show(
            "Dies meldet die aktuelle Konsolen-Sitzung sofort ab, um einen hängenden Bildschirm " +
            "(z. B. nach einem Stromausfall oder Grafiktreiber-Reset) zu beheben. Alle nicht " +
            "gespeicherten Daten in anderen offenen Programmen dieser Sitzung gehen verloren. " +
            "Fortfahren?",
            "Konsolen-Sitzung zurücksetzen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

        if (!confirmed)
            return;

        try
        {
            await _viewModel.ForceConsoleSessionResetCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual console session reset failed");
        }
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
        var menu = new ToolStripMenuItem(_localizer[LocalizationKeys.LanguageMenuHeader]);

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
    private async Task CheckServiceStatusAsync()
    {
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
                _notifyIcon.Text = $"{TrayIconText} - Dienst aktiv ✓";
                _notifyIcon.Icon = GetActiveIcon();
            }
            else
            {
                _notifyIcon.Text = $"{TrayIconText} - Dienst gestoppt ⚠";
                _notifyIcon.Icon = GetInactiveIcon();
            }
        }
        catch (Exception ex)
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Text = $"{TrayIconText} - Status unbekannt";
            }
            _logger.LogWarning(ex, "Tray service status check failed");
        }
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



