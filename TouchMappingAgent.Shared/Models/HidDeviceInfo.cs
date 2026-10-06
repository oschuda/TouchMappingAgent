namespace TouchMappingAgent.Shared.Models;

/// <summary>
/// Represents a HID touch digitizer together with the hardware anchor needed to identify it
/// across reboots and driver reloads.
///
/// WHY <see cref="ParentInstanceId"/> IS THE ANCHOR: the two PM1715 digitizers on the target
/// installation share vendor id, product id, product name and contact count, and report NO
/// physical location path (the USB range extenders present virtual hub chipsets, so the
/// mainboard topology never reaches the OS). What does differ is the USB device instance each
/// one enumerates under — "USB\VID_14E1&amp;PID_3508\7&amp;1b9afb93&amp;0&amp;6" versus
/// "...\7&amp;235bf858&amp;0&amp;6". That instance is derived from the physical controller port,
/// so as long as the extender stays in its port, it is stable across reboots.
///
/// <see cref="DevicePath"/> is the HID interface path. It is what tabcal.exe and raw input
/// report, so it is the runtime handle — but it is resolved fresh each run and matched back to
/// the stored <see cref="ParentInstanceId"/>, never persisted as the key.
/// </summary>
public record HidDeviceInfo(
    string DevicePath,
    string ProductName,
    ushort VendorId,
    ushort ProductId,
    string InstanceId = "",
    string ParentInstanceId = "")
{
    /// <summary>
    /// Stable hardware identity of this digitizer, in priority order:
    ///
    /// 1. The USB parent device instance (the port anchor).
    /// 2. The HID instance id, when the parent could not be read. Weaker, but still tied to
    ///    the enumeration slot.
    /// 3. Empty — the device cannot be told apart from an identical sibling and must be
    ///    (re-)learned by physical touch.
    /// </summary>
    public string HardwareKey =>
        !string.IsNullOrWhiteSpace(ParentInstanceId) ? NormalizeInstanceId(ParentInstanceId)
        : !string.IsNullOrWhiteSpace(InstanceId) ? NormalizeInstanceId(InstanceId)
        : string.Empty;

    /// <summary>True when this digitizer carries an anchor usable for persistent mapping.</summary>
    public bool HasHardwareIdentity => !string.IsNullOrWhiteSpace(HardwareKey);

    /// <summary>
    /// Operator-facing label. Includes a short form of the hardware key because the product
    /// name alone ("PM1715") is identical on both digitizers.
    /// </summary>
    public string DisplayLabel
    {
        get
        {
            var anchor = HardwareKey;
            var shortAnchor = anchor.Length > 0
                ? anchor[(anchor.LastIndexOf('\\') + 1)..]
                : "ohne Anker";
            return $"{ProductName} [VID_{VendorId:X4}&PID_{ProductId:X4}] {shortAnchor}";
        }
    }

    /// <summary>
    /// Normalises a PnP device instance id for comparison. Windows is inconsistent about
    /// casing between SetupAPI and CfgMgr, so comparisons must be case-insensitive.
    /// </summary>
    public static string NormalizeInstanceId(string instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? string.Empty : instanceId.Trim().ToUpperInvariant();

    /// <summary>
    /// Normalises a HID device interface path for comparison. Raw input reports the same path
    /// SetupAPI does, but casing differs between the two APIs in practice.
    /// </summary>
    public static string NormalizeDevicePath(string devicePath) =>
        string.IsNullOrWhiteSpace(devicePath) ? string.Empty : devicePath.Trim().ToUpperInvariant();
}
