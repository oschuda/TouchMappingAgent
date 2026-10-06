namespace Evolved.EdidManager.Pnp;

/// <summary>
/// Converts between the two forms Windows uses for the same monitor.
///
/// The display enumeration APIs hand out a device INTERFACE path:
///   \\?\DISPLAY#CHR8910#5&amp;2c72b841&amp;0&amp;UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}
///
/// The registry and CfgMgr32 want the device INSTANCE id:
///   DISPLAY\CHR8910\5&amp;2c72b841&amp;0&amp;UID250116
///
/// They carry the same information in different punctuation, and mixing them up produces a
/// registry path that simply does not exist — which looks identical to "this monitor has no
/// EDID". Hence a dedicated, tested conversion rather than an inline string replace.
/// </summary>
public static class PnpDeviceInstanceId
{
    private const string InterfacePrefix = @"\\?\";

    /// <summary>
    /// Converts a device interface path to a device instance id.
    /// Returns null when the input is not an interface path.
    /// </summary>
    public static string? FromDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return null;

        var value = devicePath.Trim();

        if (!value.StartsWith(InterfacePrefix, StringComparison.Ordinal))
            return null;

        value = value[InterfacePrefix.Length..];

        // Strip the trailing interface class GUID: "...#{e6f07b5f-...}".
        int guidStart = value.LastIndexOf('#');
        if (guidStart > 0 && value.EndsWith('}'))
            value = value[..guidStart];

        // The remaining '#' separators are '\' in instance-id form. The separators are exactly
        // the ones between enumerator, hardware id and instance id — the instance id itself
        // uses '&', never '#'.
        value = value.Replace('#', '\\');

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// True when the string looks like a device instance id: at least
    /// "ENUMERATOR\HARDWARE-ID\INSTANCE", no interface prefix, no NUL or control characters.
    /// </summary>
    public static bool IsValidInstanceId(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (instanceId.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        if (instanceId.Contains('\0') || instanceId.Any(char.IsControl))
            return false;

        // Guard the registry path this becomes: a relative segment would escape the Enum key.
        if (instanceId.Contains(".."))
            return false;

        var segments = instanceId.Split('\\');
        return segments.Length >= 3 && segments.All(s => !string.IsNullOrWhiteSpace(s));
    }

    /// <summary>
    /// Registry subpath of a monitor's Device Parameters key, relative to
    /// HKLM\SYSTEM\CurrentControlSet\Enum.
    /// </summary>
    public static string DeviceParametersSubPath(string instanceId) =>
        $@"{instanceId}\Device Parameters";
}
