using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Services;
using TouchMappingAgent.WPF.ViewModels;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace TouchMappingAgent.WPF;

/// <summary>
/// WPF Application entry point for TouchMappingAgent.
/// Initializes the main application window, system tray icon, and view models.
/// (IEC 62443: Admin interface security, tray-based monitoring)
/// </summary>
public partial class App : Application
{
    private TrayIconManager? _trayIconManager;
    private ServiceProvider? _serviceProvider;
    private ILogger<App>? _logger;
    private ReapplyAgent? _reapplyAgent;
    private SingleInstanceGuard? _singleInstance;
    private static bool _serilogConfigured;

    /// <summary>
    /// Command-line switches that start the agent headless, for the Windows autostart entry.
    /// Both spellings are accepted because both are in circulation in deployment scripts.
    /// </summary>
    private static readonly string[] SilentSwitches = { "--silent", "--background", "-silent", "-background", "/silent", "/background" };

    /// <summary>
    /// True when this process was started to run resident in the background: no main window,
    /// tray icon only, re-application agent running.
    /// </summary>
    public static bool IsSilentStart { get; private set; }

    /// <summary>
    /// True when the process was started to open the commissioning wizard directly
    /// (installer finish page, or the wizard's own elevated relaunch).
    /// </summary>
    public static bool IsWizardStart { get; private set; }

    /// <summary>
    /// Directory for the rolling client log — deliberately the same tree the service logs to,
    /// so a support case can be reconstructed from one folder.
    /// </summary>
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PadaLuma", "TouchMappingAgent", "logs");

    /// <summary>Initializes a new instance of the <see cref="App"/> class.</summary>
    public App()
    {
        // REQUIRED since App.xaml gained a resource dictionary: without it the merged
        // EvolvedDesignUI styles and the value converters are never loaded, and every window
        // that references them throws at construction with "resource not found". It was
        // correctly omitted while App.xaml was empty; adding resources changes that.
        InitializeComponent();
    }

    /// <summary>
    /// Handles application startup event.
    /// Initializes DI container, creates ViewModel, sets up tray icon, and configures UI behavior.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Step 0: Last-resort exception handlers. These must be installed BEFORE anything
        // else can throw. CommunityToolkit's AsyncRelayCommand rethrows exceptions from
        // fire-and-forget ICommand.Execute() invocations onto the Dispatcher; without a
        // handler here that rethrow terminates the whole process. A background service being
        // offline must degrade to a status message, never to a crash.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        IsWizardStart = e.Args.Any(arg =>
            string.Equals(arg, ViewModels.SetupWizardViewModel.WizardSwitch, StringComparison.OrdinalIgnoreCase));

        // --wizard wins: the operator (or the installer) asked for a window, so a stray
        // --silent from the autostart entry must not suppress it.
        IsSilentStart = !IsWizardStart && e.Args.Any(arg =>
            SilentSwitches.Contains(arg, StringComparer.OrdinalIgnoreCase));

        // The window must not appear on its own in either mode: in silent mode there is no UI
        // at all, and in interactive mode the tray manager decides when to show it. WPF would
        // otherwise shut the process down as soon as the last window closes, which for a
        // resident agent is exactly wrong.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Logging comes up BEFORE anything that can fail. The single-instance guard below has a
        // fallback path — if the mutex cannot be created it assumes primary and continues,
        // because refusing to start would leave the machine with no agent at all — and that
        // fallback must not be silent. With a NullLogger it would be exactly the kind of
        // invisible degradation this codebase has been cleaned of.
        ConfigureSerilog();

        // Exactly one agent per session. A second launch — the installer's finish-page
        // checkbox, a desktop shortcut next to the autostart entry — hands its intent to the
        // running one and exits, rather than adding a second tray icon and a second process
        // racing the first to run tabcal.exe.
        _singleInstance = new SingleInstanceGuard(
            new Serilog.Extensions.Logging.SerilogLoggerFactory(Log.Logger, dispose: false)
                .CreateLogger<SingleInstanceGuard>());

        if (!_singleInstance.TryClaim())
        {
            SingleInstanceGuard.SignalExistingInstance(IsWizardStart);
            Shutdown(0);
            return;
        }

        try
        {
            // Step 1: Setup Dependency Injection (K-6 fix)
            var services = new ServiceCollection();
            ConfigureLogging(services);
            services.AddSingleton<INamedPipeClient, NamedPipeClientImpl>();
            services.AddSingleton<MonitorMappingViewModel>();
            services.AddSingleton<MainWindow>();
            services.AddSingleton<TrayIconManager>();
            services.AddSingleton<ReapplyAgent>();
            services.AddSingleton<IAgentWindowFactory, AgentWindowFactory>();
            services.AddSingleton<DiagnosticsExporter>();
            services.AddSingleton<IElevationService, ElevationService>();
            services.AddSingleton<ISetupInteractionService, SetupInteractionService>();

            // The one place "identify which digitizer is on this monitor, then save it" is
            // implemented. Both the main window's "Bildschirm anlernen" and the setup wizard's
            // touch-assignment step call this instead of each carrying their own copy — see the
            // interface doc comment for what stays a caller-level decision (running tabcal
            // immediately, and the wizard's own duplicate-anchor check) and why.
            services.AddSingleton<ITouchAlignmentService, TouchAlignmentService>();

            // Singleton: the language is one process-wide setting, and LocalizationSource — which
            // every localised XAML binding points at — subscribes to exactly one instance's
            // CultureChanged. A transient here would leave open windows bound to a localiser
            // nobody switches.
            services.AddSingleton<ILocalizer, Localizer>();

            // The ONLY place tabcal.exe is started from. Behind an interface so a unit test can
            // never launch the real tool: before this seam existed, a test that passed the real
            // C:\Windows\System32\tabcal.exe with placeholder arguments put tabcal's modal
            // "Ungültige Befehlszeilensyntax" dialog on the developer's desktop on every run.
            services.AddSingleton<ITabcalRunner, TabcalRunner>();

            // Transient: both windows are re-created each time they are opened, so a stale
            // snapshot from a previous session cannot be shown. For the wizard that also means
            // a second run starts from a clean state machine rather than a half-finished one.
            services.AddTransient<EdidManagerViewModel>();
            services.AddTransient<SetupWizardViewModel>();

            // Same validation contract as the service host: an unresolvable registration
            // must fail here, visibly, not silently at first use.
            _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });

            _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
            _logger.LogInformation(
                "TouchMappingAgent client starting ({StartMode})",
                IsSilentStart ? "silent/background" : "interactive");

            // Before any window is constructed: XAML markup cannot reach the DI container, so
            // every {loc:Loc} binding goes through this singleton instead. Attaching after a
            // window was built would leave that window showing "[Key]" placeholders until the
            // next language switch.
            LocalizationSource.Instance.Attach(_serviceProvider.GetRequiredService<ILocalizer>());

            // Step 2: Get main window and create ViewModel
            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            var viewModel = _serviceProvider.GetRequiredService<MonitorMappingViewModel>();

            // Step 3: Set DataContext (was missing, causing all commands to fail)
            mainWindow.DataContext = viewModel;

            // Step 4: Initialize system tray icon with properly bound ViewModel
            _trayIconManager = _serviceProvider.GetRequiredService<TrayIconManager>();
            _trayIconManager.Initialize();

            // Step 5: Hook window closing. The "X" hides the window; only the tray menu's
            // "Beenden" (which warns first) really ends the process.
            mainWindow.Closing += (s, args) => _trayIconManager?.HideToTray(args);

            // Set main window so it's used by Application
            MainWindow = mainWindow;

            // Step 6: The window is created but NEVER shown here. Show() is what puts a WPF
            // window on screen — leaving it unshown is what makes the silent start actually
            // silent, rather than flashing a window and then hiding it.
            if (!IsSilentStart)
            {
                mainWindow.Show();
                mainWindow.Activate();
            }

            // Step 7: Start the resident half — polls the service for assignments that need
            // re-applying and runs tabcal.exe for them in this (interactive) session. Runs in
            // both modes: an operator with the window open still wants the automatic recovery.
            _reapplyAgent = _serviceProvider.GetRequiredService<ReapplyAgent>();
            _reapplyAgent.StatusChanged += OnReapplyStatusChanged;
            _reapplyAgent.Start();

            // Step 8: --wizard opens the commissioning assistant straight away. This is how the
            // installer's finish page launches it, and how the wizard comes back after asking
            // for elevation — it relaunches itself with this same flag.
            if (IsWizardStart)
            {
                mainWindow.Hide();
                _trayIconManager.ShowSetupWizard();
            }

            // Step 9: react to later launches handing their intent over.
            _singleInstance.ShowWizardRequested += () =>
                Dispatcher.Invoke(() => _trayIconManager?.ShowSetupWizard());
            _singleInstance.ShowMainWindowRequested += () =>
                Dispatcher.Invoke(() => _trayIconManager?.ShowMainWindowFromExternalRequest());
            _singleInstance.StartListening();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "App.OnStartup failed");
            Log.Fatal(ex, "App.OnStartup failed");

            MessageBox.Show(
                $"Failed to start TouchMappingAgent:\n{ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// Registers the Microsoft.Extensions.Logging pipeline (Serilog-backed) used by every
    /// component in the client.
    /// </summary>
    /// <summary>
    /// Brings up the static Serilog logger. Split out from <see cref="ConfigureLogging"/> and
    /// called first thing in OnStartup, so anything that runs before the DI container exists —
    /// notably the single-instance guard — can still report a failure.
    /// </summary>
    private static void ConfigureSerilog()
    {
        // Called twice by design — once before the single-instance guard, once from
        // ConfigureLogging — so it has to be idempotent. An explicit flag rather than probing
        // Log.Logger's type: Serilog's default is a SilentLogger and type-sniffing for it is
        // both obscure and version-fragile.
        if (_serilogConfigured)
            return;

        _serilogConfigured = true;

        try
        {
            Directory.CreateDirectory(LogDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "client-.log"),
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
        }
        catch (Exception)
        {
            // The client typically runs as a normal (non-elevated) user and may not be able
            // to write under ProgramData. That must not stop the UI from starting — the
            // in-app diagnostics log (LogWindow) still works either way.
            Log.Logger = Serilog.Core.Logger.None;
        }
    }

    /// <summary>
    /// Registers the Microsoft.Extensions.Logging pipeline (Serilog-backed) used by every
    /// component in the client.
    /// </summary>
    private static void ConfigureLogging(IServiceCollection services)
    {
        ConfigureSerilog();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
        });
    }

    /// <summary>
    /// Catches exceptions that reach the WPF Dispatcher (including AsyncRelayCommand
    /// rethrows), logs them in full, tells the user in plain language, and keeps the
    /// application alive.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // keep the app running — set before anything below can fail

        try
        {
            _logger?.LogError(e.Exception, "Unhandled exception on the UI dispatcher");
            Log.Error(e.Exception, "Unhandled exception on the UI dispatcher");

            // Mirror it into the in-app diagnostics log so the operator can copy it out
            // without going to the file system.
            var viewModel = _serviceProvider?.GetService<MonitorMappingViewModel>();
            viewModel?.ReportUnhandledException(e.Exception);

            var localizer = _serviceProvider?.GetService<ILocalizer>();
            MessageBox.Show(
                localizer == null
                    ? $"Unexpected error: {e.Exception.Message}"
                    : string.Format(
                        localizer[LocalizationKeys.App_UnhandledExceptionBody], e.Exception.Message),
                localizer?[LocalizationKeys.App_UnhandledExceptionCaption] ?? "TouchMappingAgent",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // Never let the handler itself take the process down.
        }
    }

    /// <summary>
    /// Catches exceptions from non-UI threads. These cannot be marked handled — the CLR
    /// tears the process down regardless — so the only job here is to leave a record of why.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            _logger?.LogCritical(ex, "Unhandled exception on a background thread (terminating={IsTerminating})", e.IsTerminating);
            Log.Fatal(ex, "Unhandled exception on a background thread (terminating={IsTerminating})", e.IsTerminating);
            Log.CloseAndFlush();
        }
        catch
        {
            // Nothing left to do at this point.
        }
    }

    /// <summary>
    /// Catches faulted Tasks nobody awaited, so a swallowed IPC failure still leaves a trace.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _logger?.LogError(e.Exception, "Unobserved task exception");
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        }
        catch
        {
            // Never escalate from inside the handler.
        }
    }

    /// <summary>
    /// Surfaces automatic re-application results in the in-app diagnostics log, so an operator
    /// opening the window later can see that recovery happened while nobody was watching.
    /// </summary>
    private void OnReapplyStatusChanged(string message)
    {
        try
        {
            var viewModel = _serviceProvider?.GetService<MonitorMappingViewModel>();
            viewModel?.ReportAutomaticReapply(message);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not surface re-application status in the UI");
        }
    }

    /// <summary>
    /// Handles application shutdown event.
    /// Cleans up system tray and service resources.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("TouchMappingAgent client exiting with code {ExitCode}", e.ApplicationExitCode);

        if (_reapplyAgent != null)
            _reapplyAgent.StatusChanged -= OnReapplyStatusChanged;

        _reapplyAgent?.Dispose();
        _singleInstance?.Dispose();
        _trayIconManager?.Dispose();
        _serviceProvider?.Dispose();
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}



