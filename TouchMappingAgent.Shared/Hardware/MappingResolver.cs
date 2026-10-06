using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Shared.Hardware;

/// <summary>
/// Turns persisted, hardware-anchored mappings back into something that can actually be
/// applied: it matches stored anchors against the hardware present right now and yields the
/// current runtime identifiers (GDI device name, HID interface path) that tabcal.exe needs.
///
/// Pure logic over the two enumerators' output — no Win32, no registry — so it is fully
/// testable and shared between the service (which decides what to queue) and the client
/// (which executes it).
/// </summary>
public static class MappingResolver
{
    /// <summary>
    /// Resolves one stored mapping against present hardware.
    /// Returns null when the digitizer is absent, ambiguous, or no monitor matches.
    /// </summary>
    public static ResolvedMapping? Resolve(
        TouchMapping mapping,
        IReadOnlyList<HidDeviceInfo> presentDigitizers,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        if (mapping == null || !mapping.IsUsable)
            return null;

        var touchDevice = FindUniqueTouchDevice(mapping.TouchHardwareKey, presentDigitizers);
        if (touchDevice == null)
            return null;

        var (monitor, quality) = FindMonitor(mapping, presentMonitors);
        if (monitor == null || quality == MappingMatchQuality.None)
            return null;

        return new ResolvedMapping(mapping, touchDevice, monitor, quality);
    }

    /// <summary>
    /// Resolves every stored mapping, dropping the ones that cannot be applied right now.
    /// </summary>
    public static List<ResolvedMapping> ResolveAll(
        IEnumerable<TouchMapping> mappings,
        IReadOnlyList<HidDeviceInfo> presentDigitizers,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        var resolved = new List<ResolvedMapping>();

        foreach (var mapping in mappings)
        {
            var match = Resolve(mapping, presentDigitizers, presentMonitors);
            if (match != null)
                resolved.Add(match);
        }

        return resolved;
    }

    /// <summary>
    /// Finds the one present digitizer carrying this hardware anchor.
    ///
    /// Returns null when TWO OR MORE devices share the anchor. That is not paranoia: a USB
    /// parent instance ending in a serial number rather than a port path (e.g.
    /// "USB\VID_0408&amp;PID_3008\0000" — observed on real hardware) is identical for every
    /// unit of that model. Two such digitizers on one machine collide, and picking either one
    /// would silently route touches to the wrong screen — exactly the failure this whole
    /// model exists to prevent. Better to report "must be re-learned" than to guess.
    /// </summary>
    public static HidDeviceInfo? FindUniqueTouchDevice(
        string touchHardwareKey,
        IReadOnlyList<HidDeviceInfo> presentDigitizers)
    {
        var key = HidDeviceInfo.NormalizeInstanceId(touchHardwareKey);
        if (key.Length == 0)
            return null;

        HidDeviceInfo? found = null;

        foreach (var device in presentDigitizers)
        {
            if (device.HardwareKey != key)
                continue;

            if (found != null)
                return null; // ambiguous — two devices claim the same anchor

            found = device;
        }

        return found;
    }

    /// <summary>
    /// Reports touch hardware anchors that more than one present digitizer claims, or that no
    /// digitizer provides at all. These are the cases where automatic assignment is impossible
    /// and the operator has to re-learn by touching the screen.
    /// </summary>
    public static List<string> FindAmbiguousTouchKeys(IReadOnlyList<HidDeviceInfo> presentDigitizers)
    {
        var duplicates = presentDigitizers
            .Where(d => d.HasHardwareIdentity)
            .GroupBy(d => d.HardwareKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        // Devices with no anchor at all are equally unusable, and there may be several.
        int anchorless = presentDigitizers.Count(d => !d.HasHardwareIdentity);
        for (int i = 0; i < anchorless; i++)
            duplicates.Add(string.Empty);

        return duplicates;
    }

    /// <summary>
    /// Matches a stored mapping's monitor anchors against present monitors, strongest
    /// criterion first.
    ///
    /// 1. Device path — same panel on the same connector. Survives reboots.
    /// 2. Connector label plus target id — the cable is on the same port but the panel
    ///    reports differently (e.g. an extender re-emulated its EDID).
    /// 3. Desktop bounds — last resort; the arrangement still looks the way it did at learn
    ///    time even though the connector changed.
    ///
    /// Each criterion must match EXACTLY ONE monitor. On the target installation both panels
    /// are 1920x1080, so bounds are only discriminating because the two occupy different
    /// desktop positions; if a layout ever put them on top of each other, this correctly
    /// refuses to guess.
    /// </summary>
    public static (MonitorInfo? Monitor, MappingMatchQuality Quality) FindMonitor(
        TouchMapping mapping,
        IReadOnlyList<MonitorInfo> presentMonitors)
    {
        var byPath = MatchSingle(presentMonitors, m =>
            !string.IsNullOrWhiteSpace(mapping.MonitorHardwareKey) &&
            m.HardwareKey == MonitorInfo.NormalizeDevicePath(mapping.MonitorHardwareKey));

        if (byPath != null)
            return (byPath, MappingMatchQuality.Exact);

        var byConnector = MatchSingle(presentMonitors, m =>
            !string.IsNullOrWhiteSpace(mapping.MonitorConnectorLabel) &&
            string.Equals(m.ConnectorLabel, mapping.MonitorConnectorLabel, StringComparison.OrdinalIgnoreCase) &&
            m.TargetId == mapping.MonitorTargetId);

        if (byConnector != null)
            return (byConnector, MappingMatchQuality.Connector);

        var byBounds = MatchSingle(presentMonitors, m =>
            !string.IsNullOrWhiteSpace(mapping.MonitorBoundsKey) &&
            m.BoundsKey == mapping.MonitorBoundsKey);

        if (byBounds != null)
            return (byBounds, MappingMatchQuality.BoundsOnly);

        return (null, MappingMatchQuality.None);
    }

    /// <summary>
    /// Returns the single monitor satisfying the predicate, or null when zero or more than one
    /// do. Ambiguity must never resolve to "pick the first" here.
    /// </summary>
    private static MonitorInfo? MatchSingle(
        IReadOnlyList<MonitorInfo> monitors,
        Func<MonitorInfo, bool> predicate)
    {
        MonitorInfo? found = null;

        foreach (var monitor in monitors)
        {
            if (!predicate(monitor))
                continue;

            if (found != null)
                return null;

            found = monitor;
        }

        return found;
    }

    /// <summary>
    /// Builds the tabcal.exe command line for a resolved mapping.
    ///
    /// NOTE FOR FIELD VERIFICATION: this argument shape (LinCal / DisplayID= / DeviceKind= /
    /// DevicePath= / NoValidate) is inherited from the existing implementation and has NOT
    /// been verified against "tabcal.exe /?" on the target machine. It is centralised here so
    /// there is exactly one place to correct once that check is done.
    /// </summary>
    public static string BuildTabcalArguments(string monitorGdiName, string touchDevicePath) =>
        $"LinCal DisplayID={monitorGdiName} DeviceKind=touch DevicePath=\"{touchDevicePath}\" NoValidate";
}
