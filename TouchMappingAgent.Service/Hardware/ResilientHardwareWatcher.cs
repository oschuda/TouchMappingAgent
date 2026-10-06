using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Hardware;

/// <summary>
/// Watches the set of connected touch digitizers and tells the
/// <see cref="ReapplyCoordinator"/> when it changes, so stored assignments get re-asserted
/// after a USB hotplug — the extenders being power-cycled, a cable reseated, a driver reload.
///
/// POLLING, NOT EVENTS, AND WHY: the event-driven route (RegisterDeviceNotification against a
/// service control handle) needs a window or a HandlerEx registration and delivers nothing at
/// all for the display side from Session 0. Polling SetupAPI is cheap (one enumeration every
/// few seconds), needs no window, and cannot miss a change that persists — which is the only
/// kind that matters here, since a device that appears and vanishes between two polls did not
/// leave a mapping to repair.
///
/// SCOPE: digitizers only. Monitor changes are invisible from Session 0 (EnumDisplayDevices
/// returns an empty list there), so the interactive client owns that half: it re-polls the
/// service whenever Windows raises a display-settings change, and the coordinator's hardware
/// generation covers the monitor side from the list the client supplies.
///
/// Implements BOTH <see cref="IDisposable"/> and <see cref="IAsyncDisposable"/> on purpose.
/// Microsoft.Extensions.DependencyInjection throws
/// "type only implements IAsyncDisposable. Use DisposeAsync to dispose the container."
/// when a resolved singleton is async-only-disposable and the container is disposed
/// synchronously — which is what happens on any synchronous ServiceProvider.Dispose() path.
/// </summary>
public class ResilientHardwareWatcher : BackgroundService, IDisposable, IAsyncDisposable
{
    private const int MaxRetries = 5;
    private const int InitialBackoffMs = 500;
    private const int MaxBackoffMs = 30000;
    private const int PollingIntervalMs = 4000;

    private readonly ILogger<ResilientHardwareWatcher> _logger;
    private readonly ReapplyCoordinator _coordinator;

    private string _lastSignature = string.Empty;
    private int _retryCount;
    private bool _disposed;

    /// <summary>Initializes a new instance of <see cref="ResilientHardwareWatcher"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="coordinator">Coordinator notified when the digitizer set changes.</param>
    public ResilientHardwareWatcher(
        ILogger<ResilientHardwareWatcher> logger,
        ReapplyCoordinator coordinator)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _retryCount = 0;
    }

    /// <summary>
    /// Hosted-service entry point. Runs for the lifetime of the service.
    /// MVO 2023/1230: MUST NOT throw — a hardware read failure may never take the service down.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Hardware watcher started (polling every {PollingIntervalMs} ms)", PollingIntervalMs);

        while (!stoppingToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                PollOnce();
                _retryCount = 0;

                await Task.Delay(PollingIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (UnauthorizedAccessException ex)
            {
                // Permanent: the service identity cannot enumerate devices at all. Retrying
                // cannot fix that, and doing so would just fill the log.
                _logger.LogError(ex, "Insufficient permissions to enumerate HID devices; watcher stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Hardware watcher encountered an error");

                if (!await BackOffAsync(stoppingToken))
                    break;
            }
        }

        _logger.LogInformation("Hardware watcher stopped");
    }

    /// <summary>
    /// One enumeration pass. Compares a signature of the present digitizers against the last
    /// one and invalidates the coordinator's applied state on any difference.
    /// </summary>
    internal void PollOnce()
    {
        var devices = TouchDigitizerEnumerator.EnumerateDigitizers();
        var signature = BuildSignature(devices);

        if (signature == _lastSignature)
            return;

        bool isFirstPass = _lastSignature.Length == 0;
        _lastSignature = signature;

        if (isFirstPass)
        {
            _logger.LogInformation(
                "Hardware watcher baseline: {DeviceCount} digitizer(s) present", devices.Count);
            return;
        }

        _logger.LogInformation(
            "Digitizer configuration changed: {DeviceCount} device(s) now present. " +
            "Stored assignments will be re-applied.",
            devices.Count);

        _coordinator.InvalidateAppliedState("digitizer hotplug detected");
    }

    /// <summary>
    /// Signature over the digitizers' stable anchors plus their current interface paths. The
    /// interface path is included deliberately: a driver reload can keep the device set
    /// identical while re-issuing the paths tabcal.exe was calibrated against, which is
    /// exactly a case that needs re-application.
    /// </summary>
    internal static string BuildSignature(IReadOnlyList<HidDeviceInfo> devices) =>
        string.Join(";", devices
            .Select(d => $"{d.HardwareKey}|{HidDeviceInfo.NormalizeDevicePath(d.DevicePath)}")
            .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// Exponential backoff after a failed pass. Returns false once the retry budget is spent,
    /// which ends the watcher rather than looping forever on a permanent fault.
    /// </summary>
    private async Task<bool> BackOffAsync(CancellationToken cancellationToken)
    {
        if (_retryCount >= MaxRetries)
        {
            _logger.LogError(
                "Hardware watcher exhausted {MaxRetries} retries and is stopping. " +
                "Stored assignments will only be re-applied on the next service start.",
                MaxRetries);
            return false;
        }

        _retryCount++;
        var backoffMs = CalculateBackoff(_retryCount);

        _logger.LogInformation(
            "Retrying hardware watch in {BackoffMs} ms (attempt {Attempt}/{MaxAttempts})",
            backoffMs, _retryCount, MaxRetries);

        try
        {
            await Task.Delay(backoffMs, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Calculates exponential backoff, capped.
    /// </summary>
    internal static int CalculateBackoff(int retryCount)
    {
        var backoff = (int)(InitialBackoffMs * Math.Pow(2, retryCount - 1));
        return Math.Min(backoff, MaxBackoffMs);
    }

    /// <summary>
    /// Disposes of the watcher gracefully.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Synchronous disposal. Required by the DI container's synchronous disposal path; the
    /// watcher holds no unmanaged handles, so signalling the loop to stop is all there is to do.
    /// </summary>
    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _logger.LogInformation("Hardware watcher disposing");

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
