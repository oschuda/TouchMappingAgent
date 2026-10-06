namespace TouchMappingAgent.Shared.Models;

/// <summary>
/// A persisted touch-to-monitor assignment, keyed on hardware anchors rather than on the
/// volatile identifiers Windows hands out at enumeration time.
///
/// The pairing is <see cref="TouchHardwareKey"/> (the digitizer's USB parent instance — its
/// physical port) against <see cref="MonitorHardwareKey"/> (the monitor's PnP device path,
/// which encodes the graphics connector). Neither side uses "\\.\DISPLAYn" or a HID interface
/// path as a key: both are re-issued per boot and were the reason a learned assignment could
/// silently swap sides after a restart or a DisplayPort event.
///
/// <see cref="MonitorBoundsKey"/>, <see cref="MonitorConnectorLabel"/> and
/// <see cref="MonitorTargetId"/> are secondary criteria used by
/// <c>MappingResolver</c> when the primary key no longer matches — for example after a cable
/// was moved to a different connector.
/// </summary>
public record TouchMapping(
    string TouchHardwareKey,
    string MonitorHardwareKey,
    string MonitorConnectorLabel,
    uint MonitorTargetId,
    string MonitorBoundsKey,
    string TouchProductName = "",
    string MonitorFriendlyName = "",
    string LastKnownTouchDevicePath = "",
    string LastKnownMonitorDeviceId = "",
    string LearnedUtc = "",
    string LastAppliedUtc = "")
{
    /// <summary>
    /// Registry value names used to persist this record. Kept as constants so the store and
    /// its tests cannot drift apart.
    /// </summary>
    public static class ValueNames
    {
        /// <summary>Monitor hardware anchor (PnP device path).</summary>
        public const string MonitorHardwareKey = "MonitorHardwareKey";
        /// <summary>Graphics connector label, e.g. "DP-2".</summary>
        public const string MonitorConnectorLabel = "MonitorConnectorLabel";
        /// <summary>CCD target id of the connector.</summary>
        public const string MonitorTargetId = "MonitorTargetId";
        /// <summary>Desktop bounds at learn time ("x,y,width,height").</summary>
        public const string MonitorBoundsKey = "MonitorBoundsKey";
        /// <summary>Digitizer product name (diagnostics only).</summary>
        public const string TouchProductName = "TouchProductName";
        /// <summary>Monitor friendly name (diagnostics only).</summary>
        public const string MonitorFriendlyName = "MonitorFriendlyName";
        /// <summary>Last observed HID interface path (diagnostics / fast path).</summary>
        public const string LastKnownTouchDevicePath = "LastKnownTouchDevicePath";
        /// <summary>Last observed GDI device name (diagnostics / fast path).</summary>
        public const string LastKnownMonitorDeviceId = "LastKnownMonitorDeviceId";
        /// <summary>UTC timestamp of the learn step.</summary>
        public const string LearnedUtc = "LearnedUtc";
        /// <summary>UTC timestamp of the last successful re-application.</summary>
        public const string LastAppliedUtc = "LastAppliedUtc";
    }

    /// <summary>True when both sides carry an anchor and the record can be resolved at all.</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(TouchHardwareKey) &&
        (!string.IsNullOrWhiteSpace(MonitorHardwareKey) ||
         !string.IsNullOrWhiteSpace(MonitorConnectorLabel) ||
         !string.IsNullOrWhiteSpace(MonitorBoundsKey));
}

/// <summary>
/// A mapping that has been resolved against the hardware currently present, so it can actually
/// be applied: the stored anchors have been turned back into the runtime identifiers
/// tabcal.exe needs.
/// </summary>
public record ResolvedMapping(
    TouchMapping Mapping,
    HidDeviceInfo TouchDevice,
    MonitorInfo Monitor,
    MappingMatchQuality MatchQuality);

/// <summary>
/// How confidently a stored mapping was matched back to present hardware. Anything below
/// <see cref="Exact"/> is worth surfacing to the operator, because it means the physical setup
/// changed since the assignment was learned.
/// </summary>
public enum MappingMatchQuality
{
    /// <summary>No monitor could be matched; the mapping cannot be applied.</summary>
    None = 0,

    /// <summary>Matched only on desktop bounds — the connector or cable changed.</summary>
    BoundsOnly = 1,

    /// <summary>Matched on connector label and target id, but not on the device path.</summary>
    Connector = 2,

    /// <summary>Matched on the monitor's PnP device path. Same panel, same connector.</summary>
    Exact = 3
}
