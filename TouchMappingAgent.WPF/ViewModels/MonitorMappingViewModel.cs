using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Services;

namespace TouchMappingAgent.WPF.ViewModels;

/// <summary>
/// ViewModel for managing monitor and touch device mappings.
/// </summary>
public partial class MonitorMappingViewModel : ObservableObject
{
    private readonly INamedPipeClient _pipeClient;
    private readonly ILogger<MonitorMappingViewModel> _logger;
    private readonly ILocalizer _localizer;
    private readonly ITouchAlignmentService _alignment;

    /// <summary>Real monitors enumerated by the service (Win32 EnumDisplayDevices).</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    /// <summary>
    /// Gets or sets the currently selected monitor ID.
    /// </summary>
    [ObservableProperty]
    private string? selectedMonitorId;

    /// <summary>
    /// Gets or sets the currently selected monitor.
    ///
    /// The learn flow binds to the whole record, not just the id: it needs the connector
    /// label, the PnP device path and the desktop bounds — the anchors that make the
    /// assignment survive a reboot. Selecting by id alone would force a lookup back through
    /// the volatile "\\.\DISPLAYn" name, which is the identifier this phase set out to stop
    /// depending on.
    /// </summary>
    [ObservableProperty]
    private MonitorInfo? selectedMonitor;

    /// <summary>
    /// Gets or sets the status message displayed to the user.
    /// </summary>
    [ObservableProperty]
    private string? statusMessage;

    /// <summary>
    /// Gets or sets a user-facing instruction shown while a physical touch confirmation
    /// is required during calibration (mirrors the native Windows Tablet-PC setup wizard).
    /// </summary>
    [ObservableProperty]
    private string? instructionMessage;

    /// <summary>
    /// Gets or sets the loading state indicator.
    /// </summary>
    [ObservableProperty]
    private bool isLoading;

    /// <summary>
    /// Gets whether the last <see cref="CheckServiceStatus"/> call successfully reached the
    /// background service. Read by <see cref="TouchMappingAgent.WPF.Services.TrayIconManager"/>
    /// after awaiting the command instead of relying on the command's Task faulting/succeeding,
    /// since <see cref="CheckServiceStatus"/> intentionally never lets an IPC failure propagate
    /// as an unhandled exception (see its doc comment for why).
    /// </summary>
    [ObservableProperty]
    private bool isServiceReachable;

    /// <summary>
    /// Full diagnostic log (timestamped, unsanitized exception details) shown in the
    /// diagnostics window (TouchMappingAgent.WPF.Views.LogWindow) so IPC
    /// failures can be copied verbatim instead of retyped from a screenshot. Deliberately
    /// separate from <see cref="StatusMessage"/>, which stays a short, sanitized (OWASP)
    /// string for the main window — this is an opt-in diagnostics surface for the person
    /// running the app, not something shown to an untrusted remote party.
    /// </summary>
    [ObservableProperty]
    private string logText = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="MonitorMappingViewModel"/> class.
    /// </summary>
    /// <param name="pipeClient">The IPC client for service communication.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Renders status text in the operator's language.</param>
    /// <param name="alignment">Identifies and saves a touch-to-monitor mapping.</param>
    public MonitorMappingViewModel(
        INamedPipeClient pipeClient,
        ILogger<MonitorMappingViewModel> logger,
        ILocalizer localizer,
        ITouchAlignmentService alignment)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _alignment = alignment ?? throw new ArgumentNullException(nameof(alignment));
        StatusMessage = _localizer[LocalizationKeys.Main_Ready];
    }

    /// <summary>
    /// Command: Loads available monitors.
    ///
    /// Deliberately NOT an IPC call to the service. Confirmed empirically that
    /// DisplayEnumerator.EnumerateMonitors() returns an empty list when run from the SYSTEM/
    /// Session-0 service process, even on a machine with real monitors attached — the same
    /// Session-0 visibility gap already known from tabcal.exe. Reading monitor info needs no
    /// elevated privilege, so this runs directly in the interactive client process instead,
    /// which always has the correct view of the real desktop.
    /// </summary>
    [RelayCommand]
    private Task LoadMonitors()
    {
        try
        {
            IsLoading = true;
            StatusMessage = _localizer[LocalizationKeys.Main_LoadingMonitors];

            var monitors = DisplayEnumerator.EnumerateMonitors();

            Monitors.Clear();
            foreach (var monitor in monitors)
                Monitors.Add(monitor);

            StatusMessage = string.Format(_localizer[LocalizationKeys.Main_MonitorsLoaded], monitors.Count);
            AppendLog(monitors.Count == 0
                ? "EnumerateMonitors: returned 0 monitors."
                : $"EnumerateMonitors: {monitors.Count} monitor(s): " +
                  string.Join(", ", monitors.Select(m => $"{m.DeviceId} ({m.DisplayName})")));
        }
        catch (Exception ex)
        {
            // OWASP: Only show generic message to user
            StatusMessage = _localizer[LocalizationKeys.Main_LoadMonitorsFailed];

            // Log full details internally
            LogError(ex, "EnumerateMonitors failed");
        }
        finally
        {
            IsLoading = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Command: runs the one-time learn step for the currently selected monitor.
    ///
    /// This is the step that supplies the information no API can: with two structurally
    /// identical panels behind extenders (same emulated EDID, same serial) and two identical
    /// digitizers reporting no location path, nothing in software can deduce which digitizer
    /// is wired to which screen. Covering one screen and asking the operator to touch it makes
    /// that association observable exactly once, after which it is anchored to the digitizer's
    /// USB port and the monitor's graphics connector and survives reboots.
    /// </summary>
    [RelayCommand]
    private async Task LearnSelectedMonitor()
    {
        var monitor = SelectedMonitor;
        if (monitor == null)
        {
            StatusMessage = _localizer[LocalizationKeys.Main_SelectMonitorFirst];
            return;
        }

        try
        {
            IsLoading = true;
            InstructionMessage = string.Format(
                _localizer[LocalizationKeys.Main_TouchTargetScreen],
                monitor.DisplayName, monitor.ConnectorLabel);
            StatusMessage = _localizer[LocalizationKeys.Main_LearningInProgress];

            var identify = await _alignment.IdentifyAsync(monitor);

            switch (identify.Outcome)
            {
                case TouchIdentifyOutcome.Cancelled:
                    StatusMessage = _localizer[LocalizationKeys.Main_LearnAborted];
                    AppendLog($"Learn cancelled for {monitor.DisplayLabel}.");
                    return;

                case TouchIdentifyOutcome.DeviceUnmatched:
                    StatusMessage = _localizer[LocalizationKeys.Main_TouchDeviceUnmatched];
                    AppendLog($"Learn: {identify.DiagnosticDetail}.");
                    return;

                case TouchIdentifyOutcome.UnstableAnchor:
                    StatusMessage = _localizer[LocalizationKeys.Main_UnstableHardwareAnchor];
                    AppendLog($"Learn: {identify.DiagnosticDetail}.");
                    return;
            }

            var touchDevice = identify.Device!;
            AppendLog(
                $"Learn: {monitor.DisplayLabel} <- {touchDevice.DisplayLabel} " +
                $"(anchor {touchDevice.HardwareKey})");

            var saved = await _alignment.SaveAsync(monitor, touchDevice);

            if (saved.ExceptionDetail != null)
                AppendLog($"SaveMapping failed: {saved.ExceptionDetail}");

            if (!saved.Success)
            {
                StatusMessage = _localizer[LocalizationKeys.Main_MappingRejected];
                if (saved.Response != null)
                    AppendLog($"SaveMapping rejected by service: {saved.Response.ErrorMessage}");
                return;
            }

            var response = saved.Response!;
            if (!response.RequiresLocalExecution || string.IsNullOrEmpty(response.LocalExecutablePath))
            {
                StatusMessage = _localizer[LocalizationKeys.Main_MappingSaved];
                return;
            }

            var (executionSuccess, exitCode) = await ExecuteLocalTabcalAsync(
                response.LocalExecutablePath, response.LocalArguments);

            await ConfirmLocalMappingAsync(monitor.DeviceId, touchDevice.DevicePath, executionSuccess, exitCode);

            StatusMessage = executionSuccess
                ? string.Format(
                    _localizer[LocalizationKeys.Main_MappingSavedAndActivated],
                    monitor.DisplayName, monitor.ConnectorLabel)
                : _localizer[LocalizationKeys.Main_CalibrationFailed];
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer[LocalizationKeys.Main_LearnFailed];
            LogError(ex, "LearnSelectedMonitor failed for {MonitorLabel}", monitor.DisplayLabel);
        }
        finally
        {
            InstructionMessage = null;
            IsLoading = false;
        }
    }

    /// <summary>
    /// Runs tabcal.exe locally, in this process's interactive session, so it can display
    /// the calibration UI and receive the user's physical touch confirmation. This MUST
    /// run client-side: the background service runs in Session 0 and cannot do either.
    /// </summary>
    private static async Task<(bool Success, int? ExitCode)> ExecuteLocalTabcalAsync(
        string executablePath, string? arguments)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = false,
                CreateNoWindow = false, // must be visible: the user has to physically touch the screen
            };

            using var process = Process.Start(processInfo);
            if (process == null)
                return (false, null);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                return (false, null);
            }

            return (process.ExitCode == 0, process.ExitCode);
        }
        catch (Exception)
        {
            return (false, null);
        }
    }

    /// <summary>
    /// Reports the outcome of a locally-executed tabcal.exe run back to the service so it
    /// can be recorded in the ISO 27001 A.8.15 audit trail. Best-effort: a failure here must
    /// not hide the mapping result from the user, so exceptions are only logged.
    /// </summary>
    private async Task ConfirmLocalMappingAsync(string monitorId, string deviceId, bool executionSuccess, int? exitCode)
    {
        try
        {
            var request = new ConfirmLocalMappingRequest(monitorId, deviceId, executionSuccess, exitCode);
            await _pipeClient.SendAsync<ConfirmLocalMappingResponse>(request);
        }
        catch (Exception ex)
        {
            LogError(ex, "ConfirmLocalMapping failed for MonitorId={MonitorId}, DeviceId={DeviceId}",
                monitorId, deviceId);
        }
    }

    /// <summary>
    /// Command: Creates a secure backup of the current touch configurations (MVO 2023/1230 Resilience).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteBackup))]
    private async Task CreateTouchBackup()
    {
        try
        {
            IsLoading = true;
            StatusMessage = _localizer[LocalizationKeys.Main_CreatingBackup];

            var request = new CreateBackupRequest();
            var response = await _pipeClient.SendAsync<CreateBackupResponse>(request);

            StatusMessage = response.Success
                ? _localizer[LocalizationKeys.Main_BackupCreated]
                : _localizer[LocalizationKeys.Main_BackupFailed];
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer[LocalizationKeys.Main_BackupError];
            LogError(ex, "CreateTouchBackup failed");
        }
        finally
        {
            IsLoading = false;
            CreateTouchBackupCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Command: Executes the advanced repair sequence in the service (IEC 62443 / NIS2 compliance).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteRepair))]
    private async Task AdvancedRepair()
    {
        try
        {
            IsLoading = true;
            StatusMessage = _localizer[LocalizationKeys.Main_RunningAdvancedRepair];

            var request = new AdvancedRepairRequest();
            // The server-side sequence can run RDP-process shutdown (up to 5s) plus
            // tabcal.exe ClearCal (up to 30s) before it replies, well past the default
            // 10s pipe read timeout used for quick requests. 45s covers that worst case.
            var response = await _pipeClient.SendAsync<AdvancedRepairResponse>(request, TimeSpan.FromSeconds(45));

            StatusMessage = response.Success
                ? _localizer[LocalizationKeys.Main_AdvancedRepairSucceeded]
                : _localizer[LocalizationKeys.Main_AdvancedRepairIssues];
        }
        catch (Exception ex)
        {
            StatusMessage = _localizer[LocalizationKeys.Main_AdvancedRepairIpcFailed];
            LogError(ex, "AdvancedRepair IPC command failed");
        }
        finally
        {
            IsLoading = false;
            AdvancedRepairCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Command: manual operator override for the service's console session watchdog
    /// ("Diagnose &gt; Konsolen-Sitzung zurücksetzen" in the tray menu). Runs the exact same
    /// guarded WTSLogoffSession the watchdog would run automatically after detecting a hung
    /// console (black screen after a power event/extender cycle/driver reset), immediately —
    /// bypassing the watchdog's boot hysteresis and debounce timers, but not its underlying
    /// safety check, which still refuses to touch anything but the physical console session.
    /// The confirmation dialog lives in TrayIconManager, right before this command is invoked —
    /// this method itself performs no further confirmation.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExecuteRepair))]
    private async Task ForceConsoleSessionReset()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Konsolen-Sitzung wird zurückgesetzt...";

            var response = await _pipeClient.SendAsync<ForceConsoleSessionResetResponse>(
                new ForceConsoleSessionResetRequest());

            StatusMessage = response.Success
                ? "Konsolen-Sitzung erfolgreich zurückgesetzt."
                : $"Konsolen-Sitzung konnte nicht zurückgesetzt werden: {response.Detail}";
            AppendLog($"ForceConsoleSessionReset: {(response.Success ? "success" : "failed")} ({response.Detail})");
        }
        catch (Exception ex)
        {
            StatusMessage = "Zurücksetzen der Konsolen-Sitzung fehlgeschlagen (Dienst nicht erreichbar).";
            LogError(ex, "ForceConsoleSessionReset IPC command failed");
        }
        finally
        {
            IsLoading = false;
            ForceConsoleSessionResetCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Command: Checks if the background Windows Service is actively responding.
    /// Used by TrayIconManager to update service status indicator.
    /// (IEC 62443: Async health check, non-blocking, DoS-protected)
    ///
    /// IMPORTANT: This must never let an IPC failure escape as an unhandled exception. This
    /// command is invoked both from a UI button (via ICommand.Execute, e.g. from XAML) and via
    /// ExecuteAsync from TrayIconManager. CommunityToolkit.Mvvm's AsyncRelayCommand reports any
    /// exception thrown by a fire-and-forget ICommand.Execute() invocation back onto the WPF
    /// Dispatcher so it isn't silently swallowed — but since App.xaml.cs has no
    /// DispatcherUnhandledException handler, that rethrow terminates the entire process. A
    /// "the background service happens to be offline" condition must degrade to a status
    /// message, not crash the whole application — so failures are recorded via
    /// <see cref="IsServiceReachable"/> instead of being rethrown.
    /// </summary>
    [RelayCommand]
    public async Task CheckServiceStatus()
    {
        try
        {
            // Simple health check via GetTouchDevices request
            var request = new GetTouchDevicesRequest();
            await _pipeClient.SendAsync<GetTouchDevicesResponse>(request);

            // If we get here, service is responding
            IsServiceReachable = true;
            StatusMessage = _localizer[LocalizationKeys.Main_ServiceActive];
            AppendLog("CheckServiceStatus: service reachable.");
        }
        catch (Exception ex)
        {
            // Service is not responding or unreachable
            IsServiceReachable = false;
            StatusMessage = _localizer[LocalizationKeys.Main_ServiceNotResponding];
            LogError(ex, "CheckServiceStatus failed - service may be offline");
        }
    }

    /// <summary>
    /// Command: copies the full diagnostic log to the clipboard (bound to the "Kopieren"
    /// button in <see cref="TouchMappingAgent.WPF.Views.LogWindow"/>).
    /// </summary>
    [RelayCommand]
    private void CopyLog()
    {
        try
        {
            System.Windows.Clipboard.SetText(string.IsNullOrEmpty(LogText) ? "(leer)" : LogText);
        }
        catch (Exception ex)
        {
            // Clipboard access can transiently fail (e.g. another process holding it) —
            // not critical enough to disrupt the user, just note it in the log itself.
            AppendLog($"CopyLog failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Command: clears the diagnostic log.</summary>
    [RelayCommand]
    private void ClearLog() => LogText = string.Empty;

    // --- Validation Rules for Buttons (CanExecute) ---
    private bool CanExecuteBackup() => !IsLoading;
    private bool CanExecuteRepair() => !IsLoading;

    /// <summary>
    /// Appends a timestamped line to the diagnostic log shown in the LogWindow.
    /// Always safe to call from any thread — marshals onto the UI thread since LogText is a
    /// bound WPF property.
    /// </summary>
    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == false)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogText = LogText.Length == 0 ? line : LogText + Environment.NewLine + line;
    }

    /// <summary>
    /// Records that the background agent re-applied an assignment without operator
    /// involvement, so the diagnostics log shows what happened while the window was closed.
    /// </summary>
    public void ReportAutomaticReapply(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        StatusMessage = message;
        AppendLog($"AUTO-REAPPLY :: {message}");
    }

    /// <summary>
    /// Records an unhandled UI-thread exception caught by the application-level
    /// <see cref="System.Windows.Threading.DispatcherUnhandledExceptionEventHandler"/> so it
    /// also shows up in the in-app diagnostics log the operator can copy from.
    /// </summary>
    public void ReportUnhandledException(Exception ex)
    {
        if (ex == null)
            return;

        StatusMessage = _localizer[LocalizationKeys.Main_UnhandledExceptionReported];
        AppendLog($"UNHANDLED :: {DescribeException(ex)}");
    }

    /// <summary>
    /// Logs an error's full, unsanitized detail (type, message, and inner exception chain) to
    /// the diagnostic log — deliberately more verbose than the OWASP-sanitized StatusMessage
    /// shown elsewhere, since this is what the person running the app needs to copy out when
    /// diagnosing an IPC failure (e.g. distinguishing "service not running" from "insufficient
    /// permissions" from "response timeout", which all previously collapsed into the same
    /// generic status text).
    ///
    /// <paramref name="messageTemplate"/> is a structured-logging template ("{MonitorId}"),
    /// forwarded verbatim to ILogger and rendered positionally for the in-app log by
    /// <see cref="RenderTemplate"/>. It must NOT go through string.Format: that throws
    /// FormatException on named placeholders, and since every call site here sits inside a
    /// catch block, the throw escaped the handler and took the whole application down.
    /// </summary>
    private void LogError(Exception ex, string messageTemplate, params object?[] args)
    {
        _logger.LogError(ex, messageTemplate, args);

        var message = RenderTemplate(messageTemplate, args);
        AppendLog($"{message} :: {DescribeException(ex)}");
    }

    /// <summary>
    /// Renders a structured-logging template by replacing each "{Name}" placeholder with the
    /// positionally-matching argument — the same substitution Microsoft.Extensions.Logging
    /// performs. Deliberately total: unmatched placeholders and surplus arguments are left
    /// as-is rather than throwing, because this runs on error paths.
    /// </summary>
    internal static string RenderTemplate(string messageTemplate, params object?[] args)
    {
        if (string.IsNullOrEmpty(messageTemplate) || args.Length == 0)
            return messageTemplate;

        var result = new StringBuilder(messageTemplate.Length + 32);
        int argIndex = 0;

        for (int i = 0; i < messageTemplate.Length; i++)
        {
            char c = messageTemplate[i];

            // "{{" and "}}" are literal braces in both Serilog and MEL templates.
            if ((c == '{' || c == '}') && i + 1 < messageTemplate.Length && messageTemplate[i + 1] == c)
            {
                result.Append(c);
                i++;
                continue;
            }

            if (c != '{')
            {
                result.Append(c);
                continue;
            }

            int close = messageTemplate.IndexOf('}', i + 1);
            if (close < 0)
            {
                // Unterminated placeholder: emit the rest verbatim.
                result.Append(messageTemplate, i, messageTemplate.Length - i);
                break;
            }

            result.Append(argIndex < args.Length ? args[argIndex]?.ToString() ?? "(null)" : "(missing)");
            argIndex++;
            i = close;
        }

        return result.ToString();
    }

    /// <summary>Flattens an exception and its inner-exception chain into one diagnostic line.</summary>
    private static string DescribeException(Exception ex)
    {
        var detail = $"{ex.GetType().Name}: {ex.Message}";
        var inner = ex.InnerException;
        while (inner != null)
        {
            detail += $" -> {inner.GetType().Name}: {inner.Message}";
            inner = inner.InnerException;
        }

        return detail;
    }
}
