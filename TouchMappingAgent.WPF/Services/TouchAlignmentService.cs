using Microsoft.Extensions.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.WPF.Services;

/// <summary>The real <see cref="ITouchAlignmentService"/>.</summary>
public sealed class TouchAlignmentService : ITouchAlignmentService
{
    private readonly ISetupInteractionService _interaction;
    private readonly INamedPipeClient _pipeClient;
    private readonly ILogger<TouchAlignmentService> _logger;

    /// <summary>Initializes a new instance of <see cref="TouchAlignmentService"/>.</summary>
    public TouchAlignmentService(
        ISetupInteractionService interaction,
        INamedPipeClient pipeClient,
        ILogger<TouchAlignmentService> logger)
    {
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<TouchIdentifyResult> IdentifyAsync(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        // Deliberately no try/catch here: both callers already wrap their whole learn action
        // (identify through save) in one outer catch, and that is where an unexpected failure
        // — e.g. Raw Input registration itself throwing — has always surfaced as a single,
        // consistent "learning failed" message. Duplicating that here would just re-catch the
        // same exception a second time for no behavioural difference.
        var touchedDevicePath = await _interaction.IdentifyTouchDeviceAsync(monitor);

        if (string.IsNullOrEmpty(touchedDevicePath))
        {
            _logger.LogInformation("Identify cancelled for {MonitorLabel}", monitor.DisplayLabel);
            return TouchIdentifyResult.Cancelled();
        }

        var digitizers = TouchDigitizerEnumerator.EnumerateDigitizers();
        var device = TouchDigitizerEnumerator.FindByDevicePath(digitizers, touchedDevicePath!);

        if (device == null)
        {
            var detail = $"raw input reported '{touchedDevicePath}' but no enumerated digitizer " +
                         "matches that path";
            _logger.LogWarning("Identify: {Detail}", detail);
            return TouchIdentifyResult.DeviceUnmatched(detail);
        }

        if (!device.HasHardwareIdentity)
        {
            var detail = $"{device.ProductName} has neither a parent instance id nor an instance " +
                         "id; a persistent mapping is impossible for this device";
            _logger.LogWarning("Identify: {Detail}", detail);
            return TouchIdentifyResult.UnstableAnchor(device, detail);
        }

        _logger.LogInformation(
            "Identify: {MonitorLabel} <- {DeviceLabel} (anchor {Anchor})",
            monitor.DisplayLabel, device.DisplayLabel, device.HardwareKey);
        return TouchIdentifyResult.Success(device);
    }

    /// <inheritdoc/>
    public async Task<TouchSaveResult> SaveAsync(MonitorInfo monitor, HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(device);

        var request = new MapTouchRequest(
            MonitorId: monitor.DeviceId,
            DeviceId: device.DevicePath,
            TouchHardwareKey: device.HardwareKey,
            MonitorHardwareKey: monitor.DevicePath,
            MonitorConnectorLabel: monitor.ConnectorLabel,
            MonitorTargetId: monitor.TargetId,
            MonitorBoundsKey: monitor.BoundsKey,
            TouchProductName: device.ProductName,
            MonitorFriendlyName: monitor.DisplayName);

        try
        {
            var response = await _pipeClient.SendAsync<MapTouchResponse>(request);
            return TouchSaveResult.FromResponse(response);
        }
        catch (Exception ex)
        {
            // Caught here, not left to the caller: a caller looping over several monitors (the
            // wizard) must not let one monitor's IPC hiccup abort learning for the rest of them.
            // The single-monitor caller (the main window) behaves identically either way, since
            // it always stops after its one save regardless of how the failure was reported.
            _logger.LogWarning(ex, "Could not save the assignment for {Connector}", monitor.ConnectorLabel);
            return TouchSaveResult.Faulted($"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
