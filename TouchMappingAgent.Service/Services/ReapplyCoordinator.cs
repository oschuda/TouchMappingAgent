using System.Globalization;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// Decides which stored assignments need to be re-applied and tracks which ones already have
/// been, for the hardware configuration currently present.
///
/// THE PROBLEM THIS SOLVES: Windows does not keep a digitizer bound to a specific monitor
/// across reboots or DisplayPort events when the panels are structurally identical. On the
/// target installation both DM7000s present the same emulated EDID down to the serial number,
/// so after a restart the OS has no criterion to tell left from right and touches can land on
/// the wrong screen. The stored, hardware-anchored mapping is the missing criterion — but
/// something has to notice the configuration changed and push the assignment back in.
///
/// HOW THE WORK GETS DONE: not here. A Windows Service runs in the non-interactive Session 0
/// and can neither run tabcal.exe's calibration nor see the desktop. This class only decides;
/// the interactive client polls <see cref="GetPending"/> and executes.
///
/// GENERATION TRACKING: "already applied" is scoped to a hardware generation — a hash over the
/// present digitizers and monitors. Any change (a reboot, an extender power-cycle, a monitor
/// re-appearing) produces a new generation, which invalidates the applied set and re-queues
/// every resolvable mapping. That is what makes recovery automatic rather than manual.
/// </summary>
public class ReapplyCoordinator
{
    private readonly ILogger<ReapplyCoordinator> _logger;
    private readonly MappingStore _store;

    private readonly object _stateLock = new();
    private readonly HashSet<string> _appliedInCurrentGeneration = new(StringComparer.Ordinal);
    private string _currentGeneration = string.Empty;

    /// <summary>Initializes a new instance of <see cref="ReapplyCoordinator"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="store">Persistent mapping store.</param>
    public ReapplyCoordinator(ILogger<ReapplyCoordinator> logger, MappingStore store)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Computes the work outstanding for the hardware currently present.
    /// </summary>
    /// <param name="presentDigitizers">Digitizers enumerated by the service.</param>
    /// <param name="presentMonitors">Monitors as reported by the interactive client.</param>
    public IReadOnlyList<PendingReapply> GetPending(
        IReadOnlyList<HidDeviceInfo> presentDigitizers,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        ArgumentNullException.ThrowIfNull(presentDigitizers);
        ArgumentNullException.ThrowIfNull(presentMonitors);

        var generation = ComputeGeneration(presentDigitizers, presentMonitors);
        RollGenerationIfChanged(generation);

        var mappings = _store.GetAll();
        if (mappings.Count == 0)
            return Array.Empty<PendingReapply>();

        var pending = new List<PendingReapply>();

        foreach (var mapping in mappings)
        {
            lock (_stateLock)
            {
                if (_appliedInCurrentGeneration.Contains(mapping.TouchHardwareKey))
                    continue;
            }

            var resolved = MappingResolver.Resolve(mapping, presentDigitizers, presentMonitors);
            if (resolved == null)
            {
                LogUnresolvable(mapping, presentDigitizers);
                continue;
            }

            pending.Add(new PendingReapply(
                TouchHardwareKey: mapping.TouchHardwareKey,
                MonitorHardwareKey: mapping.MonitorHardwareKey,
                MonitorConnectorLabel: resolved.Monitor.ConnectorLabel,
                TouchDevicePath: resolved.TouchDevice.DevicePath,
                MonitorDeviceId: resolved.Monitor.DeviceId,
                MonitorFriendlyName: resolved.Monitor.DisplayName,
                LocalExecutablePath: WindowsToolPaths.TabcalPath,
                LocalArguments: MappingResolver.BuildTabcalArguments(
                    resolved.Monitor.DeviceId, resolved.TouchDevice.DevicePath),
                MatchQuality: resolved.MatchQuality,
                Reason: DescribeReason(resolved)));
        }

        if (pending.Count > 0)
        {
            _logger.LogInformation(
                "{PendingCount} assignment(s) queued for re-application in hardware generation {Generation}",
                pending.Count, _currentGeneration);
        }

        return pending;
    }

    /// <summary>
    /// Records the outcome of a re-application the client performed. A success marks the
    /// mapping done for this hardware generation so it is not queued again; a failure leaves
    /// it queued so the next poll retries.
    /// </summary>
    public void ReportResult(string touchHardwareKey, bool success, int? exitCode, string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(touchHardwareKey))
            return;

        if (success)
        {
            lock (_stateLock)
            {
                _appliedInCurrentGeneration.Add(touchHardwareKey);
            }

            _store.MarkApplied(touchHardwareKey, DateTime.UtcNow);

            _logger.LogInformation(
                "Assignment {TouchKey} re-applied successfully in generation {Generation}",
                touchHardwareKey, _currentGeneration);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"REAPPLY:{touchHardwareKey}",
                success: true);
            return;
        }

        _logger.LogWarning(
            "Re-application of {TouchKey} failed (exit code {ExitCode}): {ErrorMessage}. " +
            "It stays queued and will be retried on the next poll.",
            touchHardwareKey,
            exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            errorMessage ?? "no detail");

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.SaveMapping,
            $"REAPPLY:{touchHardwareKey}",
            success: false,
            errorMessage: errorMessage ?? "Re-application failed");
    }

    /// <summary>
    /// Forces every mapping back into the queue — used when the hardware watcher observes a
    /// change the generation hash alone might not capture, and after a fresh learn.
    /// </summary>
    public void InvalidateAppliedState(string reason)
    {
        lock (_stateLock)
        {
            if (_appliedInCurrentGeneration.Count == 0)
                return;

            _appliedInCurrentGeneration.Clear();
        }

        _logger.LogInformation("Re-application state invalidated: {Reason}", reason);
    }

    /// <summary>
    /// Summarises hardware health for the tray icon and operator alarms.
    /// </summary>
    public GetHardwareStatusResponse DescribeStatus(
        IReadOnlyList<HidDeviceInfo> presentDigitizers,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        var mappings = _store.GetAll();
        var resolved = MappingResolver.ResolveAll(mappings, presentDigitizers, presentMonitors);
        var ambiguous = MappingResolver.FindAmbiguousTouchKeys(presentDigitizers);

        // Keys, not sentences: this runs as LocalSystem in Session 0, whose culture is unrelated
        // to the language the operator logged in with. The client formats these in the interactive
        // session, where the operator's language is actually known.
        var findings = new List<LocalizableText>();

        if (mappings.Count == 0)
        {
            findings.Add(LocalizableText.Of(MessageKeys.Status_NoMappingsStored));
        }

        if (resolved.Count < mappings.Count)
        {
            findings.Add(LocalizableText.Of(
                MessageKeys.Status_MappingsUnresolvable, mappings.Count - resolved.Count));
        }

        if (ambiguous.Count > 0)
        {
            findings.Add(LocalizableText.Of(
                MessageKeys.Status_AmbiguousDigitizers, ambiguous.Count));
        }

        foreach (var match in resolved.Where(r => r.MatchQuality < MappingMatchQuality.Exact))
        {
            findings.Add(LocalizableText.Of(
                MessageKeys.Status_MappingDegraded,
                match.Monitor.DisplayName,
                match.Monitor.ConnectorLabel,
                match.MatchQuality));
        }

        return new GetHardwareStatusResponse(
            DigitizerCount: presentDigitizers.Count,
            MappingCount: mappings.Count,
            ResolvedMappingCount: resolved.Count,
            AmbiguousTouchDeviceCount: ambiguous.Count,
            Findings: findings);
    }

    private void RollGenerationIfChanged(string generation)
    {
        bool rolled = false;
        string previous;

        lock (_stateLock)
        {
            previous = _currentGeneration;
            if (_currentGeneration == generation)
                return;

            _currentGeneration = generation;
            rolled = _appliedInCurrentGeneration.Count > 0 || previous.Length > 0;
            _appliedInCurrentGeneration.Clear();
        }

        if (rolled)
        {
            _logger.LogInformation(
                "Hardware configuration changed ({PreviousGeneration} -> {Generation}); " +
                "all stored assignments are queued for re-application",
                previous.Length == 0 ? "(none)" : previous, generation);
        }
    }

    private void LogUnresolvable(TouchMapping mapping, IReadOnlyList<HidDeviceInfo> presentDigitizers)
    {
        var touchDevice = MappingResolver.FindUniqueTouchDevice(mapping.TouchHardwareKey, presentDigitizers);

        if (touchDevice == null)
        {
            _logger.LogWarning(
                "Stored assignment {TouchKey} cannot be applied: its digitizer is absent or " +
                "shares its hardware anchor with another device",
                mapping.TouchHardwareKey);
            return;
        }

        _logger.LogWarning(
            "Stored assignment {TouchKey} cannot be applied: no unique monitor matches " +
            "{MonitorConnector} / {MonitorBounds}",
            mapping.TouchHardwareKey, mapping.MonitorConnectorLabel, mapping.MonitorBoundsKey);
    }

    private static LocalizableText DescribeReason(ResolvedMapping resolved) =>
        LocalizableText.Of(resolved.MatchQuality switch
        {
            MappingMatchQuality.Exact => MessageKeys.Reapply_ReasonExact,
            MappingMatchQuality.Connector => MessageKeys.Reapply_ReasonConnector,
            MappingMatchQuality.BoundsOnly => MessageKeys.Reapply_ReasonBoundsOnly,
            _ => MessageKeys.Reapply_ReasonStored
        });

    /// <summary>
    /// Builds a stable fingerprint of the present hardware. Deliberately includes the monitor
    /// bounds: moving a panel in the display arrangement changes which physical screen sits
    /// where, so the assignment has to be re-asserted even though the device set is unchanged.
    /// </summary>
    internal static string ComputeGeneration(
        IReadOnlyList<HidDeviceInfo> digitizers,
        IReadOnlyList<MonitorInfo> monitors)
    {
        var parts = digitizers
            .Select(d => $"T:{d.HardwareKey}|{HidDeviceInfo.NormalizeDevicePath(d.DevicePath)}")
            .Concat(monitors.Select(m => $"M:{m.HardwareKey}|{m.ConnectorLabel}|{m.BoundsKey}|{m.DeviceId}"))
            .OrderBy(s => s, StringComparer.Ordinal);

        var joined = string.Join(";", parts);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined));

        return Convert.ToHexString(hash)[..16];
    }
}
