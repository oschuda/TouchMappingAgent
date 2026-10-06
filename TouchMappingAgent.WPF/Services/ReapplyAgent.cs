using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// The resident half of the agent: asks the service what needs re-applying and runs
/// tabcal.exe for it, headless, in the interactive session.
///
/// WHY THIS LIVES IN THE CLIENT AND NOT THE SERVICE: a Windows Service runs in Session 0. It
/// can neither execute tabcal.exe's calibration nor enumerate the desktop's monitors — running
/// EnumDisplayDevices there returns an empty list even with monitors attached. So the service
/// owns the stored assignments and the decision, and this class — which does run in the user's
/// session — supplies the monitor list and performs the work.
///
/// WHY POLLING: the service is the pipe SERVER and cannot call into the client. The poll is
/// cheap (one request every few seconds, answered from an in-memory decision), and it is also
/// driven immediately by display-change events, which is the case that actually matters: an
/// extender power-cycle or a resolution change is exactly when an assignment needs
/// re-asserting.
/// </summary>
public sealed class ReapplyAgent : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Heartbeat cadence, on its own loop. It must not share the poll loop: a poll can block for
    /// 60s per pending item inside tabcal.exe, and a heartbeat that stalls with it reads as a
    /// hung session to the service's watchdog.
    /// </summary>
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DisplayChangeSettleDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TabcalTimeout = TimeSpan.FromSeconds(60);

    private readonly INamedPipeClient _pipeClient;
    private readonly ILogger<ReapplyAgent> _logger;
    private readonly Func<List<HidDeviceInfo>> _enumerateDigitizers;
    private readonly ILocalizer _localizer;
    private readonly ITabcalRunner _tabcal;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollGate = new(1, 1);

    private Task? _loop;
    private Task? _heartbeatLoop;
    private bool _disposed;

    /// <summary>Raised when an assignment was re-applied, for status display.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Initializes a new instance of <see cref="ReapplyAgent"/>.</summary>
    /// <param name="pipeClient">IPC client for talking to the service.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Renders status text in the operator's language.</param>
    /// <param name="tabcal">Runs tabcal.exe. Injected so no test can start the real tool.</param>
    public ReapplyAgent(
        INamedPipeClient pipeClient,
        ILogger<ReapplyAgent> logger,
        ILocalizer localizer,
        ITabcalRunner tabcal)
        : this(pipeClient, logger, localizer, tabcal, TouchDigitizerEnumerator.EnumerateDigitizers)
    {
    }

    /// <summary>
    /// Test seam: lets a test supply the attached-digitizer list in place of the real SetupAPI
    /// enumeration, which depends on what happens to be plugged into the build machine.
    /// </summary>
    internal ReapplyAgent(
        INamedPipeClient pipeClient,
        ILogger<ReapplyAgent> logger,
        ILocalizer localizer,
        ITabcalRunner tabcal,
        Func<List<HidDeviceInfo>> enumerateDigitizers)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _tabcal = tabcal ?? throw new ArgumentNullException(nameof(tabcal));
        _enumerateDigitizers = enumerateDigitizers
            ?? throw new ArgumentNullException(nameof(enumerateDigitizers));
    }

    /// <summary>
    /// Starts the polling loop and subscribes to display-change events.
    /// </summary>
    public void Start()
    {
        if (_loop != null)
            return;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _heartbeatLoop = Task.Run(() => RunHeartbeatAsync(_cts.Token));
        _loop = Task.Run(() => RunAsync(_cts.Token));

        _logger.LogInformation(
            "Re-application agent started (poll every {PollSeconds}s, plus on display change)",
            PollInterval.TotalSeconds);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(cancellationToken);
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // The service being down is the normal case during a reboot, not an error
                // worth escalating — log at debug volume and keep the loop alive.
                _logger.LogDebug(ex, "Re-application poll failed; will retry");

                try
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("Re-application agent stopped");
    }

    private async Task RunHeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendHeartbeatAsync(DisplayEnumerator.EnumerateMonitors().Count);

            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Console session watchdog heartbeat: the service runs in Session 0 and cannot see the
    /// desktop at all, so it relies on THIS client's own (correct) monitor count to tell a hung
    /// DWM apart from a Session-0 blind spot. Sent even when the count is 0, since "0" is exactly
    /// the signal the watchdog needs — see ConsoleDisplayHeartbeatState's remarks. Best-effort.
    /// </summary>
    internal async Task SendHeartbeatAsync(int monitorCount)
    {
        try
        {
            await _pipeClient.SendAsync<ConsoleDisplayHeartbeatResponse>(
                new ConsoleDisplayHeartbeatRequest(
                    monitorCount,
                    System.Diagnostics.Process.GetCurrentProcess().SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Console display heartbeat failed; will retry");
        }
    }

    /// <summary>
    /// One poll: ask the service what is pending for the monitors currently attached, then
    /// apply each item and report the outcome back.
    /// </summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        // Serialised: a display-change event and the timer can fire together, and running two
        // tabcal.exe invocations for the same assignment concurrently is pointless at best.
        if (!await _pollGate.WaitAsync(TimeSpan.Zero, cancellationToken))
            return;

        try
        {
            var monitors = DisplayEnumerator.EnumerateMonitors();

            // The heartbeat is sent from its own loop (RunHeartbeatAsync), not from here.

            if (monitors.Count == 0)
            {
                _logger.LogWarning(
                    "No monitors enumerated; skipping re-application. If the screens are dark, " +
                    "check the range extenders' power and the DisplayPort handshake.");
                return;
            }

            var response = await _pipeClient.SendAsync<GetPendingReapplyResponse>(
                new GetPendingReapplyRequest(monitors));

            if (response.Pending == null || response.Pending.Count == 0)
                return;

            _logger.LogInformation(
                "Service reports {PendingCount} assignment(s) to re-apply", response.Pending.Count);

            foreach (var item in response.Pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyOneAsync(item, cancellationToken);
            }
        }
        finally
        {
            _pollGate.Release();
        }
    }

    private async Task ApplyOneAsync(PendingReapply item, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Re-applying assignment for {MonitorFriendlyName} [{MonitorConnector}] " +
            "(match quality {MatchQuality}): {Reason}",
            item.MonitorFriendlyName, item.MonitorConnectorLabel, item.MatchQuality, item.Reason);

        // tabcal.exe exits with 0 even when it did nothing at all — measured: with no digitizer
        // attached it parses the command line, finds no device matching DevicePath= and returns 0
        // without touching the calibration. Its exit code therefore cannot distinguish "calibrated"
        // from "silently skipped". Without this guard a reboot with an unplugged or not-yet-
        // enumerated digitizer would be reported and audited as a successful re-application while
        // touch input stayed on the wrong screen — the exact failure this agent exists to prevent.
        //
        // So verify the precondition ourselves: the digitizer the assignment names must actually be
        // present before we believe a zero exit code.
        var (present, presenceError) = IsTouchDevicePresent(item.TouchDevicePath);
        if (!present)
        {
            await ReportOutcomeAsync(item, success: false, exitCode: null,
                error: _localizer.Format(presenceError), cancellationToken);
            return;
        }

        var (success, exitCode, error) = await _tabcal.RunAsync(
            item.LocalExecutablePath, item.LocalArguments, cancellationToken);

        await ReportOutcomeAsync(item, success, exitCode, error, cancellationToken);
    }

    /// <summary>
    /// Checks whether the digitizer an assignment names is currently enumerable.
    /// </summary>
    /// <remarks>
    /// Enumeration failure is treated as "not present" rather than assumed-present: reporting a
    /// re-application we could not verify is worse than reporting one failure too many, because a
    /// false success is silently wrong while a false failure is retried on the next poll.
    /// </remarks>
    internal (bool Present, LocalizableText? Error) IsTouchDevicePresent(string touchDevicePath)
    {
        if (string.IsNullOrWhiteSpace(touchDevicePath))
            return (false, LocalizableText.Of(MessageKeys.Reapply_NoDevicePath));

        List<HidDeviceInfo> digitizers;
        try
        {
            digitizers = _enumerateDigitizers();
        }
        catch (Exception ex)
        {
            return (false, LocalizableText.Of(
                MessageKeys.Reapply_EnumerationFailed, $"{ex.GetType().Name}: {ex.Message}"));
        }

        var present = digitizers.Any(d =>
            string.Equals(d.DevicePath, touchDevicePath, StringComparison.OrdinalIgnoreCase));

        return present
            ? (true, null)
            : (false, LocalizableText.Of(MessageKeys.Reapply_DigitizerAbsent));
    }

    private async Task ReportOutcomeAsync(
        PendingReapply item,
        bool success,
        int? exitCode,
        string? error,
        CancellationToken cancellationToken)
    {
        try
        {
            await _pipeClient.SendAsync<ReportReapplyResultResponse>(
                new ReportReapplyResultRequest(item.TouchHardwareKey, success, exitCode, error));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not report re-application result for {TouchKey}", item.TouchHardwareKey);
        }

        if (success)
        {
            StatusChanged?.Invoke(_localizer.Format(LocalizableText.Of(
                MessageKeys.Reapply_Succeeded,
                item.MonitorFriendlyName, item.MonitorConnectorLabel)));
        }
        else
        {
            _logger.LogWarning(
                "Re-application failed for {MonitorConnector}: {ErrorMessage}",
                item.MonitorConnectorLabel, error ?? "no detail");

            StatusChanged?.Invoke(_localizer.Format(LocalizableText.Of(
                MessageKeys.Reapply_Failed, item.MonitorFriendlyName)));
        }
    }

    /// <summary>
    /// A display change is the primary trigger: it is exactly what happens when the extenders
    /// re-establish their DisplayPort handshake. Settles briefly first, because Windows raises
    /// several of these in a row while the topology stabilises.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _logger.LogInformation("Display configuration changed; scheduling re-application check");

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DisplayChangeSettleDelay, _cts.Token);
                await PollOnceAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Re-application after display change failed");
            }
        });
    }

    /// <summary>
    /// Stops the loop and unsubscribes from system events.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        try
        {
            _cts.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cancelling the re-application loop threw");
        }

        _cts.Dispose();
        _pollGate.Dispose();
    }
}
