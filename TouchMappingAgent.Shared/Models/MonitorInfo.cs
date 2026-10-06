namespace TouchMappingAgent.Shared.Models;

/// <summary>
/// Represents a physical monitor together with the hardware anchors needed to identify it
/// across reboots.
///
/// WHY THIS IS NOT JUST A GDI NAME: on the target installation two structurally identical
/// DM7000 panels hang behind DisplayPort-to-HDMI range extenders. The extenders present a
/// generic emulated EDID, so both monitors report the SAME manufacturer code, product code,
/// serial number (880) and EDID block hash. They are indistinguishable by monitor identity
/// alone. The only thing that separates them is WHERE they are plugged in: the graphics
/// connector (DP-2 vs DP-3) and the resulting desktop bounds.
///
/// <see cref="DeviceId"/> (the "\\.\DISPLAYn" GDI name) is deliberately NOT an identity — it
/// is an ordinal Windows hands out at enumeration time and can move between reboots,
/// especially when the extenders complete their Hot-Plug-Detect handshake in a different
/// order. It is kept because tabcal.exe's DisplayID= switch needs it, but it is resolved
/// fresh on every run rather than persisted as a key.
/// </summary>
public record MonitorInfo(
    string DeviceId,
    string DisplayName,
    int Width,
    int Height,
    int RefreshRate,
    int X = 0,
    int Y = 0,
    string ConnectorLabel = "",
    string AdapterId = "",
    uint TargetId = 0,
    string DevicePath = "",
    bool IsPrimary = false)
{
    /// <summary>
    /// Stable hardware identity of this monitor, in priority order:
    ///
    /// 1. The monitor's PnP device interface path. This encodes the GPU's device instance and
    ///    the connector UID (".. &amp;UID250118 ..") and survives reboots, which is what makes
    ///    it a real anchor.
    /// 2. Connector label plus target id, when no device path is available.
    /// 3. Empty — caller must fall back to bounds matching.
    ///
    /// Deliberately does NOT include <see cref="AdapterId"/>: the adapter LUID is regenerated
    /// by Windows on every boot and would invalidate a stored mapping on the next restart.
    /// It is carried on the record for diagnostics only.
    /// </summary>
    public string HardwareKey =>
        !string.IsNullOrWhiteSpace(DevicePath) ? NormalizeDevicePath(DevicePath)
        : !string.IsNullOrWhiteSpace(ConnectorLabel) ? $"{ConnectorLabel}#{TargetId}"
        : string.Empty;

    /// <summary>True when this monitor carries an anchor usable for persistent mapping.</summary>
    public bool HasHardwareIdentity => !string.IsNullOrWhiteSpace(HardwareKey);

    /// <summary>Right edge of this monitor in virtual desktop coordinates (exclusive).</summary>
    public int Right => X + Width;

    /// <summary>Bottom edge of this monitor in virtual desktop coordinates (exclusive).</summary>
    public int Bottom => Y + Height;

    /// <summary>
    /// Bounds as a persistable string ("x,y,width,height"). Used as the secondary matching
    /// criterion when the device path changed (e.g. a cable moved to another connector).
    /// </summary>
    public string BoundsKey => $"{X},{Y},{Width},{Height}";

    /// <summary>
    /// Operator-facing label. Includes the connector and position because the friendly name
    /// alone ("DM7000") is identical on both panels and cannot be told apart in a dropdown.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var connector = string.IsNullOrWhiteSpace(ConnectorLabel) ? DeviceId : ConnectorLabel;
            var position = X == 0 && Y == 0 ? "primär/links" : $"x={X}";
            var primary = IsPrimary ? ", Hauptbildschirm" : string.Empty;
            return $"{DisplayName} [{connector}] {Width}x{Height} ({position}{primary})";
        }
    }

    /// <summary>
    /// Normalises a display device interface path for comparison: upper-cased and stripped of
    /// the trailing interface-class GUID, which is constant and only adds noise.
    /// </summary>
    public static string NormalizeDevicePath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return string.Empty;

        var trimmed = devicePath.Trim();
        int guidStart = trimmed.LastIndexOf('#');
        if (guidStart > 0 && trimmed.EndsWith('}'))
            trimmed = trimmed[..guidStart];

        return trimmed.ToUpperInvariant();
    }
}
