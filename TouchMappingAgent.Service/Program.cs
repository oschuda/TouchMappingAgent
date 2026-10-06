using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TouchMappingAgent.Service.HealthMonitoring;
using TouchMappingAgent.Service.Hardware;
using TouchMappingAgent.Service.IPC;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Native;
using TouchMappingAgent.Service.Services;

namespace TouchMappingAgent.Service;

/// <summary>
/// Service entry point for Windows Service hosting (IEC 62443 / ISO 27001 compliant).
/// Configures dependency injection, logging, and Named Pipe server for IPC.
/// </summary>
class Program
{
    /// <summary>
    /// Directory for the rolling service log. Under ProgramData (not the install directory)
    /// so the SYSTEM service account can write it without loosening ACLs on Program Files.
    /// </summary>
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PadaLuma", "TouchMappingAgent", "logs");

    /// <summary>
    /// Main entry point for the Windows Service.
    /// </summary>
    static async Task Main(string[] args)
    {
        ConfigureSerilog();

        try
        {
            // Build and run the service host
            var host = Host.CreateDefaultBuilder(args)
                .UseSerilog()
                .UseWindowsService(options =>
                {
                    // MUST match the name the installer registers with the Service Control
                    // Manager ("sc.exe create TouchMappingAgent ..." in installer.nsi). A
                    // mismatch here means the SCM starts the process under one name but the
                    // process reports itself ready under a different name internally — the
                    // "started" signal never reaches the SCM for the name it's actually
                    // waiting on, and Windows gives up after 60s with error 1053
                    // ("The service did not respond to the start or control request in a
                    // timely fashion"). This was the actual root cause of the service never
                    // starting, not the duplicate host.RunAsync() call fixed earlier.
                    options.ServiceName = "TouchMappingAgent";
                })
                // =========================================================================
                // DI VALIDATION (the fix for the silent runtime collapse)
                // =========================================================================
                // ValidateOnBuild walks every registration at Build() time and throws if any
                // constructor dependency cannot be satisfied. Without it, an unresolvable
                // service is only discovered when something first asks for it — and here that
                // "something" was NamedPipeServer.GetService(...) inside a try/catch, which
                // swallowed the InvalidOperationException and answered every MapTouch /
                // CreateBackup / AdvancedRepair request with a generic failure while the
                // health check (GetTouchDevices, no DI needed) still reported the service as
                // healthy. Fail loudly at startup instead.
                // ValidateScopes catches scoped-into-singleton captive dependencies.
                .UseDefaultServiceProvider((context, options) =>
                {
                    options.ValidateOnBuild = true;
                    options.ValidateScopes = true;
                })
                .ConfigureServices((context, services) =>
                {
                    // =========================================================================
                    // LOGGING LAYER (ISO 27001 A.8.15: Audit Trail)
                    // =========================================================================
                    // Logging is provided by the host (Serilog behind Microsoft.Extensions.Logging).
                    // Services take ILogger<T> — there is deliberately no project-owned ILogger
                    // abstraction any more: the three near-identical copies that used to live in
                    // the Hardware/Integration/Services namespaces were what broke DI, since only
                    // one of the three was ever registered.

                    // =========================================================================
                    // SERVICE LAYER (Business Logic)
                    // =========================================================================
                    // DisplayRefreshService: Stateless, can be Transient
                    services.AddTransient<DisplayRefreshService>();

                    // BackupService: Handles registry I/O and file storage (CRA / ISO 27001)
                    // Singleton ensures ACL-protected C:\TouchBackup directory is initialized once
                    services.AddSingleton<BackupService>();

                    // AdvancedRepairService: Multi-phase recovery logic (IEC 62443 / NIS2)
                    // Singleton for consistent error handling and audit trail
                    services.AddSingleton<AdvancedRepairService>();

                    // MappingStore: hardware-anchored persistence of learned assignments.
                    // Singleton because it serialises its writes internally.
                    services.AddSingleton<MappingStore>();

                    // ReapplyCoordinator: decides which stored assignments need re-applying
                    // for the hardware configuration currently present. Must be a singleton —
                    // it holds the "already applied in this hardware generation" state that
                    // stops the client re-running tabcal.exe on every poll.
                    services.AddSingleton<ReapplyCoordinator>();

                    // ResilientHardwareWatcher: polls for digitizer hotplug and invalidates
                    // the coordinator's applied state so assignments are re-asserted after an
                    // extender power-cycle or driver reload. Registered as a hosted service so
                    // the host starts and stops it with the rest of the process.
                    services.AddSingleton<ResilientHardwareWatcher>();
                    services.AddHostedService(sp => sp.GetRequiredService<ResilientHardwareWatcher>());

                    // =========================================================================
                    // CONSOLE / SESSION WATCHDOG
                    // =========================================================================
                    // Detects a hung physical console (black screen after a power event,
                    // extender power-cycle, or graphics driver reset) and a hung RDP/other
                    // session (own client process stopped answering), and heals each via a
                    // guarded, session-specific recovery path. See ConsoleSessionWatchdog's own
                    // remarks for the full detection/safety design.
                    services.AddSingleton(TimeProvider.System);

                    // NativeWtsSessionProvider is wrapped by TimeBoundedWtsSessionProvider
                    // before anything else ever sees it — wtsapi32 calls are synchronous LRPC
                    // to termsrv.exe with no timeout of their own, and a wedged termsrv (the
                    // real incident this guards against) would otherwise block the watchdog's
                    // BackgroundService loop thread forever, silently. See
                    // TimeBoundedWtsSessionProvider's remarks.
                    services.AddSingleton<NativeWtsSessionProvider>();
                    services.AddSingleton<IWtsSessionProvider>(sp => new TimeBoundedWtsSessionProvider(
                        sp.GetRequiredService<NativeWtsSessionProvider>(),
                        sp.GetRequiredService<ILogger<TimeBoundedWtsSessionProvider>>()));

                    services.AddSingleton<ConsoleSessionResetService>();
                    services.AddSingleton<ISessionProcessService, SessionProcessService>();
                    services.AddSingleton<ConsoleDisplayHeartbeatState>();
                    services.AddSingleton<ConsoleSessionWatchdog>();
                    services.AddHostedService(sp => sp.GetRequiredService<ConsoleSessionWatchdog>());

                    // =========================================================================
                    // EDID MANAGEMENT (Evolved.EdidManager)
                    // =========================================================================
                    // Makes structurally identical monitors distinguishable at OS level by
                    // giving each a unique EDID serial. Independent of the touch mapping, which
                    // already anchors on the PnP device path — this is for the OTHER consumers
                    // on the machine that key on the EDID serial.
                    services.AddSingleton<Evolved.EdidManager.Registry.IEdidRegistryStore,
                        Evolved.EdidManager.Registry.EdidRegistryStore>();
                    services.AddSingleton<Evolved.EdidManager.Pnp.IPnpDeviceControl,
                        Evolved.EdidManager.Pnp.PnpDeviceControl>();
                    services.AddSingleton<Evolved.EdidManager.IElevationCheck,
                        Evolved.EdidManager.WindowsElevationCheck>();
                    services.AddSingleton<Evolved.EdidManager.Templates.IEdidTemplateStore,
                        Evolved.EdidManager.Templates.EdidTemplateStore>();
                    services.AddSingleton<Evolved.EdidManager.EdidManagerService>();

                    // =========================================================================
                    // INTEGRATION LAYER (Request Handling & Compliance)
                    // =========================================================================
                    // ComplianceRequestHandler: Central dispatcher for all IPC requests
                    // Depends on BackupService, AdvancedRepairService for handler methods
                    services.AddSingleton<ComplianceRequestHandler>();

                    // =========================================================================
                    // IPC LAYER (Named Pipe Communication)
                    // =========================================================================
                    // NamedPipeServer: Listens for client connections and routes requests
                    // Depends on IServiceProvider for dynamic handler resolution
                    services.AddSingleton<NamedPipeServer>();
                })
                .Build();

            // =========================================================================
            // SERVICE LIFECYCLE MANAGEMENT
            // =========================================================================
            await RunServiceAsync(host);
        }
        catch (Exception ex)
        {
            // Covers DI validation failures from Build() as well as unexpected startup errors.
            // Serilog is already configured at this point, so this reaches the log file even
            // though the host never came up.
            Log.Fatal(ex, "TouchMappingAgent service terminated unexpectedly during startup");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Configures the static Serilog logger that the host's ILogger&lt;T&gt; instances write
    /// through. Writes a rolling file under ProgramData so the service actually produces
    /// diagnosable output in a Release build — the previous SimpleLogger routed everything
    /// to Debug.WriteLine, which is compiled out entirely in Release.
    /// </summary>
    private static void ConfigureSerilog()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .Enrich.FromLogContext()
                .WriteTo.Console()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "service-.log"),
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true)
                .CreateLogger();
        }
        catch (Exception ex)
        {
            // Logging setup must never prevent the service from starting. Fall back to
            // console-only so the process still runs (and says why it has no file log).
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .CreateLogger();

            Log.Warning(ex, "Could not initialise file logging in {LogDirectory}; console only", LogDirectory);
        }
    }

    /// <summary>
    /// Runs the service with proper startup and shutdown handling.
    /// </summary>
    private static async Task RunServiceAsync(IHost host)
    {
        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        try
        {
            // Resolve dependencies
            var namedPipeServer = host.Services.GetRequiredService<NamedPipeServer>();
            var appLifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            logger.LogInformation("TouchMappingAgent Service starting");

            // Start the Named Pipe server in the background
            //
            // MUST be linked to appLifetime.ApplicationStopping, not a bare standalone
            // CancellationTokenSource. When Windows issues a stop (e.g. "net stop
            // TouchMappingAgent" / SCM stop), .UseWindowsService()'s WindowsServiceLifetime
            // triggers IHostApplicationLifetime.ApplicationStopping, which makes hostTask
            // below complete cleanly — but a standalone, never-cancelled CTS left pipeTask
            // (namedPipeServer.StartAsync) blocked forever inside
            // pipeServer.WaitForConnectionAsync(), waiting on a token nothing ever cancels.
            // "await Task.WhenAll(hostTask, pipeTask)" further down then hangs forever
            // waiting for a pipeTask that will never finish, so RunServiceAsync() — and the
            // whole process — never actually exits after a stop request. The process stays
            // alive holding its own .exe file locked, which is exactly what surfaced as
            // "Error opening file for writing: ...TouchMappingAgent.Service.exe" in the
            // installer: "net stop"/"sc.exe delete" ran, but the old process was still very
            // much alive and had the file open.
            using (var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(appLifetime.ApplicationStopping))
            {
                logger.LogInformation("Named Pipe server listening on {PipeName}", @"\\?\pipe\TouchMappingAgent");

                // W-1 fix: await both tasks; if either faults, cancel the other and propagate the exception.
                // This ensures the service does not silently run without a working Pipe server.
                //
                // IMPORTANT: host.RunAsync()/namedPipeServer.StartAsync() must each be started exactly
                // once. An earlier version of this fix left the original fire-and-forget calls in place
                // ("_ = host.RunAsync(cts.Token);" etc.) *in addition to* these awaited ones, so
                // host.RunAsync() ended up being invoked twice on the same IHost. Under
                // .UseWindowsService(), a second concurrent call races the WindowsServiceLifetime's
                // SCM dispatch registration and reliably causes the Service Control Manager to report
                // "service did not respond in a timely fashion" (Win32 error 1053) after its 60s
                // timeout — the service never reaches the Running state, so the WPF client can never
                // connect over the Named Pipe (this is what breaks "Monitore laden" and every other
                // IPC command). Do not reintroduce the duplicate calls.
                var hostTask = host.RunAsync(cts.Token);
                var pipeTask = namedPipeServer.StartAsync(cts.Token);

                // Wait for either task to complete (normal shutdown) or fault (error)
                var firstCompleted = await Task.WhenAny(hostTask, pipeTask);

                // If the first-completed task faulted, cancel and re-throw
                if (firstCompleted.IsFaulted)
                {
                    await cts.CancelAsync();
                    await Task.WhenAll(
                        hostTask.ContinueWith(_ => { }),
                        pipeTask.ContinueWith(_ => { }));
                    await firstCompleted; // re-throw the faulting exception
                }

                // Otherwise wait for both to finish cleanly
                await Task.WhenAll(hostTask, pipeTask);
            }

            logger.LogInformation("TouchMappingAgent Service stopped");
        }
        catch (OperationCanceledException)
        {
            // Expected when service is shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error in service lifecycle");
            throw;
        }
    }
}

