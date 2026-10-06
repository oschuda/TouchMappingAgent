using Evolved.EdidManager.Edid;
using Evolved.EdidManager.Pnp;
using Evolved.EdidManager.Registry;
using Microsoft.Extensions.Logging;

namespace Evolved.EdidManager;

/// <summary>
/// Makes structurally identical monitors distinguishable at OS level by giving each a unique
/// EDID serial number.
///
/// WHY THIS EXISTS: on the target installation two DM7000 panels sit behind range extenders
/// that emulate a generic EDID. Both report manufacturer CHR, product 35088 and serial 880 —
/// byte for byte the same identity. Anything that identifies a display by its EDID therefore
/// cannot tell them apart. This module reads each panel's real EDID, clones it in memory with
/// a distinct serial, and installs the clone as a Windows EDID override.
///
/// WHAT IT DOES NOT DO: TouchMappingAgent's own touch-to-monitor mapping does not depend on
/// this. That already anchors on the monitor's PnP device path and the digitizer's USB parent
/// instance, both of which are unique without touching the EDID. This module is for the
/// OTHER consumers on the machine — survey tools, HMI software, vendor control panels — that
/// key on the EDID serial and currently see two identical displays.
///
/// Nothing is hard-coded per panel: every byte written is derived from what the hardware
/// reported at run time.
/// </summary>
public sealed class EdidManagerService : IEdidManagerService
{
    private readonly ILogger<EdidManagerService> _logger;
    private readonly IEdidRegistryStore _store;
    private readonly IPnpDeviceControl _pnp;
    private readonly IElevationCheck _elevation;

    /// <summary>Initializes a new instance of <see cref="EdidManagerService"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="store">Registry access for reading EDIDs and managing overrides.</param>
    /// <param name="pnp">Device-node control used to re-init the monitor after a write.</param>
    /// <param name="elevation">Privilege check for the HKLM writes.</param>
    public EdidManagerService(
        ILogger<EdidManagerService> logger,
        IEdidRegistryStore store,
        IPnpDeviceControl pnp,
        IElevationCheck elevation)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _pnp = pnp ?? throw new ArgumentNullException(nameof(pnp));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
    }

    /// <summary>
    /// Reads the EDID of every monitor that reports one. Prefers an installed override, so the
    /// result reflects what Windows currently presents rather than what the panel sent.
    /// </summary>
    public IReadOnlyList<MonitorEdid> ReadAllMonitors()
    {
        var monitors = new List<MonitorEdid>();

        foreach (var instanceId in _store.EnumerateMonitorInstanceIds())
        {
            var overrideBytes = _store.ReadOverride(instanceId);
            var hardwareBytes = _store.ReadHardwareEdid(instanceId);
            var bytes = overrideBytes ?? hardwareBytes;

            if (bytes == null)
                continue;

            if (!EdidBlock.TryFromBytes(bytes, out var block) || block == null)
            {
                _logger.LogWarning(
                    "Monitor {InstanceId} reports {Length} bytes that are not a valid EDID; skipping",
                    instanceId, bytes.Length);
                continue;
            }

            if (!block.IsChecksumValid)
            {
                _logger.LogWarning(
                    "Monitor {InstanceId} reports an EDID with an invalid checksum ({Edid}); " +
                    "leaving it untouched rather than propagating corrupt data",
                    instanceId, block.ToString());
                continue;
            }

            monitors.Add(new MonitorEdid(instanceId, block, overrideBytes != null));
        }

        _logger.LogInformation("Read {MonitorCount} monitor EDID(s)", monitors.Count);
        return monitors;
    }

    /// <summary>
    /// Reports which monitors currently share an EDID identity, without changing anything.
    /// Safe to call unelevated.
    /// </summary>
    public IReadOnlyList<CollisionPlanEntry> Analyze()
    {
        var monitors = ReadAllMonitors();
        var plan = EdidCollisionDetector.BuildPlan(monitors);

        // Reported but never acted on — see FindNumericOnlyCollisions for why rewriting these
        // would do more harm than good.
        foreach (var group in EdidCollisionDetector.FindNumericOnlyCollisions(monitors))
        {
            _logger.LogInformation(
                "{Count} monitors share the numeric serial {Serial} but differ in their 0xFF " +
                "text descriptor, so they remain distinguishable: {InstanceIds}. Not modified.",
                group.Count, group[0].Edid.SerialNumber,
                string.Join(", ", group.Select(m => m.PnpInstanceId)));
        }

        if (plan.Count == 0)
        {
            _logger.LogInformation("No EDID serial collisions among {MonitorCount} monitor(s)", monitors.Count);
            return plan;
        }

        foreach (var entry in plan)
        {
            _logger.LogInformation(
                "Collision: {InstanceId} reports {Identity} (1 of {GroupSize}); " +
                "would become serial {NewSerial} / '{NewSerialText}'",
                entry.Monitor.PnpInstanceId, entry.Monitor.Edid.IdentityKey, entry.GroupSize,
                entry.NewSerialNumber, entry.NewSerialText);
        }

        return plan;
    }

    /// <summary>
    /// Resolves all EDID serial collisions: mutates each colliding monitor's EDID in memory,
    /// installs it as an override and re-enumerates the device node.
    /// </summary>
    /// <param name="dryRun">When true, everything is computed and logged but nothing is written.</param>
    public EdidOperationResult ResolveCollisions(bool dryRun = false)
    {
        var plan = Analyze();

        if (plan.Count == 0)
            return EdidOperationResult.NothingToDo(EdidMessage.Of(EdidMessageKeys.NoCollisions));

        if (dryRun)
        {
            return new EdidOperationResult(
                Success: true,
                Changed: 0,
                Failed: 0,
                Message: EdidMessage.Of(EdidMessageKeys.DryRun, plan.Count));
        }

        if (!_elevation.IsElevated)
        {
            _logger.LogError(
                "Administrative rights required: EDID overrides live under " +
                "HKLM\\SYSTEM\\CurrentControlSet\\Enum");
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.ElevationRequired));
        }

        int changed = 0;
        int failed = 0;

        foreach (var entry in plan)
        {
            if (ApplyEntry(entry))
                changed++;
            else
                failed++;
        }

        _logger.LogInformation(
            "EDID de-collision finished: {Changed} changed, {Failed} failed", changed, failed);

        var summary = failed > 0
            ? EdidMessage.Of(EdidMessageKeys.CollisionsResolvedWithFailures, changed, failed)
            : EdidMessage.Of(EdidMessageKeys.CollisionsResolved, changed);

        return new EdidOperationResult(failed == 0, changed, failed, summary);
    }

    /// <summary>
    /// Installs a template EDID on one port, stamped with the given serial.
    ///
    /// The case this exists for: a KVM or HDBaseT range extender that does not pass DDC
    /// through, or answers with a generic dummy block. There is then no hardware EDID worth
    /// cloning, so a known-good block for the panel actually behind the extender is
    /// substituted. Unlike the collision path this does NOT require an existing EDID — the
    /// port may report nothing at all.
    /// </summary>
    public EdidOperationResult ApplyCustomTemplate(
        string pnpDeviceInstanceId,
        byte[] templateEdid,
        string customSerial)
    {
        ArgumentNullException.ThrowIfNull(templateEdid);

        var instanceId = NormalizeInstanceId(pnpDeviceInstanceId);
        if (instanceId == null)
            return EdidOperationResult.Failure(
                EdidMessage.Of(EdidMessageKeys.InvalidPnpId, pnpDeviceInstanceId));

        // Validate BEFORE the elevation check so an operator can find out a template is broken
        // without having to elevate first.
        if (templateEdid.Length == 0 || templateEdid.Length % EdidBlock.BlockSize != 0)
        {
            return EdidOperationResult.Failure(EdidMessage.Of(
                EdidMessageKeys.TemplateWrongSize, templateEdid.Length, EdidBlock.BlockSize));
        }

        if (!EdidBlock.TryFromBytes(templateEdid, out var template) || template == null)
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.TemplateBadHeader));

        if (!template.IsChecksumValid)
        {
            byte stored = templateEdid[EdidBlock.ChecksumOffset];
            byte expected = EdidBlock.ComputeChecksum(templateEdid);

            _logger.LogError(
                "Template has an invalid checksum (stored 0x{Stored:X2}, expected 0x{Expected:X2}); " +
                "a graphics driver discards such an EDID and the port would stay dark",
                stored, expected);

            return EdidOperationResult.Failure(EdidMessage.Of(
                EdidMessageKeys.TemplateBadChecksum,
                stored.ToString("X2"),
                expected.ToString("X2")));
        }

        if (!_elevation.IsElevated)
        {
            _logger.LogError(
                "Administrative rights required: EDID overrides live under " +
                "HKLM\\SYSTEM\\CurrentControlSet\\Enum");
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.ElevationRequired));
        }

        var (serialNumber, serialText) = DeriveSerial(customSerial, template);

        // allowDescriptorCreation: a template substituted for a dead extender port is a block
        // we are choosing wholesale, so filling an unused descriptor slot with the serial text
        // costs nothing and makes the port identifiable to tools that read the text.
        var mutation = EdidMutator.WithSerialNumber(
            template, serialNumber, serialText, allowDescriptorCreation: true);

        if (!mutation.Edid.IsChecksumValid)
        {
            _logger.LogError(
                "Refusing to install the template on {InstanceId}: checksum verification failed " +
                "after stamping the serial", instanceId);
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.StampChecksumFailed));
        }

        var hadHardwareEdid = _store.ReadHardwareEdid(instanceId) != null;
        _logger.LogInformation(
            "Applying template to {InstanceId} ({Template}); the port {HardwareState} a hardware EDID",
            instanceId, template.ToString(),
            hadHardwareEdid ? "reports" : "reports NO");

        if (!_store.WriteOverride(instanceId, mutation.Edid.ToArray()))
            return EdidOperationResult.Failure(
                EdidMessage.Of(EdidMessageKeys.OverrideWriteFailed, instanceId));

        _logger.LogInformation(
            "Template installed on {InstanceId}: {Manufacturer} {ProductCode:X4}, serial {Serial} " +
            "('{SerialText}'), {BlockCount} block(s)",
            instanceId, mutation.Edid.ManufacturerCode, mutation.Edid.ProductCode,
            mutation.Edid.SerialNumber, mutation.Edid.SerialText ?? "-", mutation.Edid.BlockCount);

        bool reenumerated = _pnp.ReenumerateDevice(instanceId);

        return new EdidOperationResult(
            Success: true,
            Changed: 1,
            Failed: 0,
            Message: EdidMessage.Of(
                reenumerated
                    ? EdidMessageKeys.TemplateApplied
                    : EdidMessageKeys.TemplateAppliedNoReenumerate,
                instanceId, mutation.Edid.SerialNumber));
    }

    /// <summary>
    /// Turns the operator's serial string into the numeric and textual forms an EDID needs.
    ///
    /// A purely numeric value that fits in 32 bits is used directly, so "1001" really becomes
    /// serial 1001. Anything else ("880-A") cannot be a numeric serial, so a stable value is
    /// derived from the string instead — stable meaning the same string always yields the same
    /// number, across runs and machines, which is what lets a re-application reproduce the
    /// previous result.
    /// </summary>
    internal static (uint SerialNumber, string SerialText) DeriveSerial(string? customSerial, EdidBlock template)
    {
        var text = string.IsNullOrWhiteSpace(customSerial)
            ? template.SerialNumber.ToString()
            : customSerial.Trim();

        if (uint.TryParse(text, out uint numeric) && numeric != 0)
            return (numeric, text);

        // FNV-1a over the string, folded into the generated range so a derived serial is
        // recognisable as such and cannot masquerade as a small factory serial.
        unchecked
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= prime;
            }

            uint derived = EdidCollisionDetector.GeneratedSerialBase + (hash % 10_000_000);
            return (derived, text);
        }
    }

    /// <summary>
    /// Removes the EDID override from one monitor and re-enumerates it, restoring the
    /// serial the hardware actually reports.
    /// </summary>
    /// <param name="pnpId">
    /// Device instance id ("DISPLAY\CHR8910\5&amp;...") or device interface path
    /// ("\\?\DISPLAY#CHR8910#5&amp;...#{guid}"); both are accepted.
    /// </param>
    public EdidOperationResult RestoreDefaults(string pnpId)
    {
        var instanceId = NormalizeInstanceId(pnpId);
        if (instanceId == null)
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.InvalidPnpId, pnpId));

        if (!_elevation.IsElevated)
        {
            _logger.LogError("Administrative rights required to remove an EDID override");
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.ElevationRequired));
        }

        if (!_store.RemoveOverride(instanceId))
        {
            _logger.LogInformation("No EDID override installed for {InstanceId}", instanceId);
            return EdidOperationResult.NothingToDo(
                EdidMessage.Of(EdidMessageKeys.NoOverridePresent, instanceId));
        }

        // Best effort: the override is gone from the registry either way, so a failed
        // re-enumeration delays the effect until the next topology change rather than
        // invalidating the operation.
        bool reenumerated = _pnp.ReenumerateDevice(instanceId);

        return new EdidOperationResult(
            Success: true,
            Changed: 1,
            Failed: 0,
            Message: EdidMessage.Of(
                reenumerated
                    ? EdidMessageKeys.OverrideRemoved
                    : EdidMessageKeys.OverrideRemovedNoReenumerate,
                instanceId));
    }

    /// <summary>
    /// Removes every EDID override this machine carries.
    /// </summary>
    public EdidOperationResult RestoreAllDefaults()
    {
        if (!_elevation.IsElevated)
            return EdidOperationResult.Failure(EdidMessage.Of(EdidMessageKeys.ElevationRequired));

        int removed = 0;
        int failed = 0;

        foreach (var instanceId in _store.EnumerateMonitorInstanceIds())
        {
            if (_store.ReadOverride(instanceId) == null)
                continue;

            var result = RestoreDefaults(instanceId);
            if (result.Success)
                removed++;
            else
                failed++;
        }

        return new EdidOperationResult(
            failed == 0, removed, failed,
            removed == 0
                ? EdidMessage.Of(EdidMessageKeys.NoOverridesStored)
                : EdidMessage.Of(EdidMessageKeys.OverridesRemoved, removed));
    }

    private bool ApplyEntry(CollisionPlanEntry entry)
    {
        var instanceId = entry.Monitor.PnpInstanceId;

        // Always mutate the HARDWARE EDID, never an already-installed override. Mutating an
        // override would compound suffixes across runs ("880-A" becoming "880-A-A") and drift
        // further from the panel's real identity with every invocation.
        var hardwareBytes = _store.ReadHardwareEdid(instanceId);
        if (hardwareBytes == null)
        {
            _logger.LogWarning("No hardware EDID for {InstanceId}; skipping", instanceId);
            return false;
        }

        if (!EdidBlock.TryFromBytes(hardwareBytes, out var hardware) || hardware == null)
        {
            _logger.LogWarning("Hardware EDID for {InstanceId} is not parseable; skipping", instanceId);
            return false;
        }

        var mutation = EdidMutator.WithSerialNumber(hardware, entry.NewSerialNumber, entry.NewSerialText);

        if (!mutation.SerialTextApplied)
        {
            _logger.LogInformation(
                "Monitor {InstanceId} has no 0xFF serial-text descriptor; only the numeric " +
                "serial was changed", instanceId);
        }

        // Belt and braces: an EDID with a bad checksum is rejected by the driver, and the
        // usual symptom is a dark port rather than a graceful fallback.
        if (!mutation.Edid.IsChecksumValid)
        {
            _logger.LogError(
                "Refusing to install a mutated EDID for {InstanceId}: checksum verification failed",
                instanceId);
            return false;
        }

        if (!_store.WriteOverride(instanceId, mutation.Edid.ToArray()))
            return false;

        _logger.LogInformation(
            "Monitor {InstanceId}: serial {OldSerial} -> {NewSerial} ('{NewSerialText}')",
            instanceId, hardware.SerialNumber, mutation.Edid.SerialNumber, mutation.Edid.SerialText ?? "-");

        if (!_pnp.ReenumerateDevice(instanceId))
        {
            _logger.LogWarning(
                "EDID override for {InstanceId} is installed but the device node could not be " +
                "re-enumerated; it takes effect at the next topology change or reboot",
                instanceId);
        }

        return true;
    }

    private static string? NormalizeInstanceId(string? pnpId)
    {
        if (string.IsNullOrWhiteSpace(pnpId))
            return null;

        var fromPath = PnpDeviceInstanceId.FromDevicePath(pnpId);
        if (fromPath != null)
            return PnpDeviceInstanceId.IsValidInstanceId(fromPath) ? fromPath : null;

        return PnpDeviceInstanceId.IsValidInstanceId(pnpId) ? pnpId.Trim() : null;
    }
}

/// <summary>Outcome of an EDID management operation.</summary>
/// <param name="Success">False when at least one monitor could not be processed.</param>
/// <param name="Changed">How many monitors were modified.</param>
/// <param name="Failed">How many monitors failed.</param>
/// <param name="Message">
/// Operator-facing summary as a key plus parameters. Not finished text: the module cannot know
/// the operator's language, and in the TouchMappingAgent host it runs in Session 0 under a
/// culture unrelated to the logged-in user's.
/// </param>
public sealed record EdidOperationResult(bool Success, int Changed, int Failed, EdidMessage Message)
{
    /// <summary>Nothing needed doing — a success, not a failure.</summary>
    public static EdidOperationResult NothingToDo(EdidMessage message) => new(true, 0, 0, message);

    /// <summary>The operation could not be carried out.</summary>
    public static EdidOperationResult Failure(EdidMessage message) => new(false, 0, 0, message);
}
