namespace TouchMappingAgent.Shared.Models;

/// <summary>
/// Represents a HID touch digitizer together with the hardware anchor needed to identify it
/// across reboots and driver reloads.
///
/// WHY <see cref="ParentLocationPath"/> IS THE ANCHOR: the two PM1715 digitizers on the target
/// installation share vendor id, product id, product name and contact count. The earlier model
/// keyed them on the USB parent's instance id ("USB\VID_14E1&amp;PID_3508\7&amp;1b9afb93&amp;0&amp;6")
/// on the assumption that it was derived from the controller port. It is not, behind the
/// installation's range extenders: both extender hubs (USB\VID_0000&amp;PID_0000) report the same
/// serial number, Windows gives the serial-based instance id to whichever enumerates first and a
/// port-derived one to the other, and the digitizer ids below inherit that. After an extender
/// power-cycle the same id can therefore name the OTHER touch screen — measured on the target
/// on 2026-10-06, where an assignment was applied to the wrong panel and reported as success.
///
/// The USB parent's location path ("PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)#USB(4)#USB(6)")
/// records one port number per hub level and does not depend on serial numbers or enumeration
/// order, so it identifies the physical side for as long as the cabling stays the same.
///
/// <see cref="DevicePath"/> is the HID interface path. It is what tabcal.exe and raw input
/// report, so it is the runtime handle — but it is resolved fresh each run and matched back to
/// the stored anchor, never persisted as the key.
/// </summary>
public record HidDeviceInfo(
    string DevicePath,
    string ProductName,
    ushort VendorId,
    ushort ProductId,
    string InstanceId = "",
    string ParentInstanceId = "",
    string ParentLocationPath = "")
{
    /// <summary>Enumerator segment of a <see cref="HardwareKey"/> built from a location path.</summary>
    public const string PortKeyPrefix = @"PORT\";

    /// <summary>
    /// Stable hardware identity of this digitizer, in priority order:
    ///
    /// 1. A port key built from the USB parent's location path:
    ///    "PORT\VID_14E1&amp;PID_3508\PCIROOT(0)/PCI(1400)/USBROOT(0)/USB(1)/USB(4)/USB(6)".
    ///    It keeps the ENUMERATOR\DEVICE\INSTANCE shape of an instance id; '#' becomes '/'
    ///    because the mapping store escapes '\' as '#' in registry subkey names.
    /// 2. The USB parent instance id, when no location path could be read. Not reliable
    ///    behind range extenders (see the type remarks), kept for hardware without one.
    /// 3. The HID instance id, when the parent could not be read at all.
    /// 4. Empty — the device cannot be told apart from an identical sibling and must be
    ///    (re-)learned by physical touch.
    /// </summary>
    public string HardwareKey =>
        !string.IsNullOrWhiteSpace(ParentLocationPath) ? BuildPortKey(VendorId, ProductId, ParentLocationPath)
        : !string.IsNullOrWhiteSpace(ParentInstanceId) ? NormalizeInstanceId(ParentInstanceId)
        : !string.IsNullOrWhiteSpace(InstanceId) ? NormalizeInstanceId(InstanceId)
        : string.Empty;

    /// <summary>
    /// The anchor the previous model would have produced (USB parent instance id). Only used to
    /// recognise mappings persisted before the switch to port keys.
    /// </summary>
    public string LegacyHardwareKey =>
        !string.IsNullOrWhiteSpace(ParentInstanceId) ? NormalizeInstanceId(ParentInstanceId)
        : !string.IsNullOrWhiteSpace(InstanceId) ? NormalizeInstanceId(InstanceId)
        : string.Empty;

    /// <summary>Builds the port key for <see cref="HardwareKey"/>.</summary>
    public static string BuildPortKey(ushort vendorId, ushort productId, string locationPath) =>
        $"{PortKeyPrefix}VID_{vendorId:X4}&PID_{productId:X4}\\{locationPath.Trim().Replace('#', '/')}"
            .ToUpperInvariant();

    /// <summary>True when the key is a port key rather than a (legacy) PnP instance id.</summary>
    public static bool IsPortKey(string hardwareKey) =>
        hardwareKey.StartsWith(PortKeyPrefix, StringComparison.OrdinalIgnoreCase);

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

            // "PCIROOT(0)/PCI(1400)/USBROOT(0)/USB(1)/USB(4)/USB(6)" -> "USB(1)/USB(4)/USB(6)":
            // the controller prefix is identical for both panels and only adds noise.
            int usbRoot = shortAnchor.IndexOf("USBROOT(", StringComparison.Ordinal);
            int afterRoot = usbRoot >= 0 ? shortAnchor.IndexOf('/', usbRoot) : -1;
            if (afterRoot >= 0 && afterRoot < shortAnchor.Length - 1)
                shortAnchor = shortAnchor[(afterRoot + 1)..];
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
