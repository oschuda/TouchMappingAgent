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
/// HOW THE WORK GETS DONE: here, through <see cref="IWindowsTouchMapApplier"/>, which writes
/// Windows' touch routing table and restarts the digitizer. The service still depends on the
/// interactive client for one thing: Session 0 cannot see the desktop, so the client's poll of
/// <see cref="GetPending"/> supplies the monitor list — and is therefore also the trigger.
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
    private readonly IWindowsTouchMapApplier _applier;

    private readonly object _stateLock = new();
    private readonly HashSet<string> _appliedInCurrentGeneration = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedUnresolvableInCurrentGeneration = new(StringComparer.Ordinal);
    private string _currentGeneration = string.Empty;

    /// <summary>Initializes a new instance of <see cref="ReapplyCoordinator"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="store">Persistent mapping store.</param>
    /// <param name="applier">Writes Windows' touch routing for a resolved assignment.</param>
    public ReapplyCoordinator(ILogger<ReapplyCoordinator> logger, MappingStore store, IWindowsTouchMapApplier applier)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
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

            // Applied right here, by the service: it writes Windows' routing table and restarts
            // the digitizer (see WindowsTouchMapApplier). Nothing is handed to the client to run
            // with tabcal.exe any more — on a machine with two touch screens tabcal refuses to
            // work at all and only ever produced a modal error dialog.
            var outcome = _applier.Apply(resolved.TouchDevice, resolved.Monitor);
            if (outcome == TouchMapApplyOutcome.Failed)
            {
                // Stays unapplied, so the next poll retries; logged once per generation.
                lock (_stateLock)
                {
                    if (!_reportedUnresolvableInCurrentGeneration.Add(mapping.TouchHardwareKey))
                        continue;
                }

                _logger.LogWarning(
                    "Could not apply assignment {TouchKey} -> {MonitorConnector}; will retry on the next poll",
                    mapping.TouchHardwareKey, resolved.Monitor.ConnectorLabel);
                continue;
            }

            lock (_stateLock)
            {
                _appliedInCurrentGeneration.Add(mapping.TouchHardwareKey);
            }

            if (outcome == TouchMapApplyOutcome.Updated)
            {
                _store.MarkApplied(mapping.TouchHardwareKey, DateTime.UtcNow);
                _logger.LogInformation(
                    "Assignment {TouchKey} applied: {TouchPath} -> {MonitorConnector} (match {MatchQuality}, generation {Generation})",
                    mapping.TouchHardwareKey, resolved.TouchDevice.DevicePath, resolved.Monitor.ConnectorLabel,
                    resolved.MatchQuality, _currentGeneration);
            }
        }

        // Kept for the contract: clients still read this list, and an empty one means
        // "nothing for you to do".
        return Array.Empty<PendingReapply>();
    }

    /// <summary>
    /// Operator-requested "apply now": applies EVERY stored assignment immediately and restarts
    /// the digitizers even where Windows' table already reads correctly — the case this exists
    /// for is "the table is right but touch still lands on the wrong screen".
    /// </summary>
    public ApplyMappingsNowResponse ApplyNow(
        IReadOnlyList<HidDeviceInfo> presentDigitizers,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        ArgumentNullException.ThrowIfNull(presentDigitizers);
        ArgumentNullException.ThrowIfNull(presentMonitors);

        RollGenerationIfChanged(ComputeGeneration(presentDigitizers, presentMonitors));

        var mappings = _store.GetAll();
        int applied = 0, unresolvable = 0, failed = 0;

        foreach (var mapping in mappings)
        {
            var resolved = MappingResolver.Resolve(mapping, presentDigitizers, presentMonitors);
            if (resolved == null)
            {
                unresolvable++;
                LogUnresolvable(mapping, presentDigitizers);
                continue;
            }

            if (_applier.Apply(resolved.TouchDevice, resolved.Monitor, forceRestart: true) == TouchMapApplyOutcome.Failed)
            {
                failed++;
                continue;
            }

            applied++;
            lock (_stateLock)
            {
                _appliedInCurrentGeneration.Add(mapping.TouchHardwareKey);
            }

            _store.MarkApplied(mapping.TouchHardwareKey, DateTime.UtcNow);
        }

        _logger.LogInformation(
            "Apply-now requested by the operator: {Applied} applied, {Unresolvable} unresolvable, {Failed} failed " +
            "(of {Total} stored)",
            applied, unresolvable, failed, mappings.Count);

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.SaveMapping,
            "APPLY_NOW",
            success: failed == 0 && unresolvable == 0,
            errorMessage: failed == 0 && unresolvable == 0 ? null : $"{unresolvable} unresolvable, {failed} failed");

        return new ApplyMappingsNowResponse(mappings.Count, applied, unresolvable, failed);
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
            _reportedUnresolvableInCurrentGeneration.Clear();
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
        // Once per mapping and hardware generation: the client polls every 15s, and the same
        // warning used to be written thousands of times a day while nothing changed.
        lock (_stateLock)
        {
            if (!_reportedUnresolvableInCurrentGeneration.Add(mapping.TouchHardwareKey))
                return;
        }

        if (!HidDeviceInfo.IsPortKey(mapping.TouchHardwareKey) &&
            presentDigitizers.Any(d => HidDeviceInfo.IsPortKey(d.HardwareKey)))
        {
            // Not resolved through the legacy id on purpose: behind the range extenders that id
            // can name either physical touch screen, so there is no way to tell which one the
            // operator meant when it was learned.
            _logger.LogWarning(
                "Stored assignment {TouchKey} uses the former USB instance anchor, which is not stable " +
                "behind the range extenders. It is not applied; re-learn this screen once to replace it " +
                "with a port anchor.",
                mapping.TouchHardwareKey);
            return;
        }

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
