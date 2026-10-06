using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Validation;

/// <summary>
/// Validates mapping configurations against IEC 62443 constraints.
/// Implements input validation for CRA (Cyber Resilience Act) compliance.
///
/// PHASE 2 CHANGE — what is a key and what is merely a runtime handle:
///
/// The GDI device name ("\\.\DISPLAY3") is still validated, because tabcal.exe's DisplayID=
/// switch consumes it, but it is no longer the identity of anything. The identity is the
/// hardware anchor pair — the digitizer's USB parent instance against the monitor's PnP
/// device path / connector. The old validator required "\\.\DISPLAYn" as THE identifier,
/// which meant a stable key could not even be submitted for storage.
/// </summary>
public static class MappingValidator
{
    private const int MaxIdentifierLength = 512;

    // W-8 fix: HashSet<T> is not thread-safe; NamedPipeServer handles clients in parallel.
    // Use a lock to guard concurrent Add + Contains operations.
    private static readonly object _vendorIdLock = new();
    private static readonly HashSet<ushort> AllowedVendorIds = new()
    {
        0x0461, // Primax Electronics
        0x0A5C, // Broadcom
        0x0B05, // ASUSTeK Computer
        0x0E8F, // GreenAsia Inc.
        0x1038, // Corsair Gaming
        0x14E1, // Data Modul / PM1715 — the digitizers on the target installation
        // Add approved manufacturers only
    };

    /// <summary>
    /// Validates a MapTouchRequest for compliance and security constraints.
    /// </summary>
    /// <param name="request">The mapping request to validate.</param>
    /// <exception cref="ArgumentException">If validation fails.</exception>
    public static void ValidateMapRequest(MapTouchRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        // Runtime handles: needed to build the tabcal.exe command for THIS run.
        ValidateMonitorId(request.MonitorId, nameof(request.MonitorId));
        ValidateDevicePath(request.DeviceId, nameof(request.DeviceId));

        // Hardware anchors: what actually gets persisted. Optional on the request so a
        // caller can still drive a one-shot calibration without storing it, but validated
        // whenever present, and required as a pair when storage is intended.
        ValidateOptionalInstanceId(request.TouchHardwareKey, nameof(request.TouchHardwareKey));
        ValidateOptionalDevicePath(request.MonitorHardwareKey, nameof(request.MonitorHardwareKey));
        ValidateOptionalConnectorLabel(request.MonitorConnectorLabel, nameof(request.MonitorConnectorLabel));
        ValidateOptionalBoundsKey(request.MonitorBoundsKey, nameof(request.MonitorBoundsKey));

        if (RequestCarriesAnchors(request) && !HasUsableAnchorPair(request))
        {
            throw new ArgumentException(
                "A persistable mapping needs a touch hardware anchor and at least one monitor " +
                "anchor (device path, connector label, or bounds).",
                nameof(request));
        }
    }

    /// <summary>
    /// True when the caller supplied any hardware anchor at all, i.e. intends the mapping to
    /// be persisted rather than applied once.
    /// </summary>
    public static bool RequestCarriesAnchors(MapTouchRequest request) =>
        !string.IsNullOrWhiteSpace(request.TouchHardwareKey) ||
        !string.IsNullOrWhiteSpace(request.MonitorHardwareKey) ||
        !string.IsNullOrWhiteSpace(request.MonitorConnectorLabel) ||
        !string.IsNullOrWhiteSpace(request.MonitorBoundsKey);

    /// <summary>
    /// True when the anchors present are sufficient to store and later resolve the mapping.
    /// </summary>
    public static bool HasUsableAnchorPair(MapTouchRequest request) =>
        !string.IsNullOrWhiteSpace(request.TouchHardwareKey) &&
        (!string.IsNullOrWhiteSpace(request.MonitorHardwareKey) ||
         !string.IsNullOrWhiteSpace(request.MonitorConnectorLabel) ||
         !string.IsNullOrWhiteSpace(request.MonitorBoundsKey));

    /// <summary>
    /// Validates a persisted mapping before it is written to or read back from the store.
    /// </summary>
    /// <exception cref="ArgumentException">If the mapping cannot be resolved later.</exception>
    public static void ValidateMapping(TouchMapping mapping)
    {
        if (mapping == null)
            throw new ArgumentNullException(nameof(mapping));

        ValidateInstanceId(mapping.TouchHardwareKey, nameof(mapping.TouchHardwareKey));
        ValidateOptionalDevicePath(mapping.MonitorHardwareKey, nameof(mapping.MonitorHardwareKey));
        ValidateOptionalConnectorLabel(mapping.MonitorConnectorLabel, nameof(mapping.MonitorConnectorLabel));
        ValidateOptionalBoundsKey(mapping.MonitorBoundsKey, nameof(mapping.MonitorBoundsKey));

        if (!mapping.IsUsable)
        {
            throw new ArgumentException(
                "Mapping carries no monitor anchor and could never be resolved.", nameof(mapping));
        }
    }

    /// <summary>
    /// Validates a HID device against the VID/PID whitelist.
    /// IEC 62443-4-2: Only whitelisted devices are permitted.
    ///
    /// NOTE: not currently invoked on any production path — see the review finding about this
    /// being effectively inactive. It is kept (and the target hardware's vendor id added) so
    /// that wiring it up later does not immediately lock out the installation's own digitizers.
    /// </summary>
    /// <param name="device">The device to validate.</param>
    /// <exception cref="ArgumentNullException">If device is null.</exception>
    /// <exception cref="UnauthorizedAccessException">If device VID not in whitelist.</exception>
    public static void ValidateHidDevice(HidDeviceInfo device)
    {
        if (device == null)
            throw new ArgumentNullException(nameof(device));

        bool allowed;
        lock (_vendorIdLock)
        {
            allowed = AllowedVendorIds.Contains(device.VendorId);
        }
        if (!allowed)
        {
            throw new UnauthorizedAccessException(
                $"Vendor ID {device.VendorId:X4} is not in the approved whitelist. " +
                $"Device: {device.ProductName}");
        }
    }

    /// <summary>
    /// Validates a monitor information object for constraints.
    /// </summary>
    /// <param name="monitor">The monitor to validate.</param>
    /// <exception cref="ArgumentNullException">If monitor is null.</exception>
    /// <exception cref="ArgumentException">If monitor constraints violated.</exception>
    public static void ValidateMonitor(MonitorInfo monitor)
    {
        if (monitor == null)
            throw new ArgumentNullException(nameof(monitor));

        ValidateMonitorId(monitor.DeviceId, nameof(monitor.DeviceId));

        if (string.IsNullOrWhiteSpace(monitor.DisplayName))
            throw new ArgumentException("DisplayName cannot be empty.", nameof(monitor.DisplayName));

        // Display dimensions must be positive and reasonable
        if (monitor.Width <= 0 || monitor.Height <= 0)
            throw new ArgumentException("Monitor dimensions must be positive.", nameof(monitor));

        if (monitor.Width > 7680 || monitor.Height > 4320)
            throw new ArgumentException("Monitor dimensions exceed maximum (7680x4320).", nameof(monitor));

        // Refresh rate must be between 30Hz and 240Hz
        if (monitor.RefreshRate < 30 || monitor.RefreshRate > 240)
            throw new ArgumentException(
                $"Refresh rate {monitor.RefreshRate}Hz outside valid range (30-240Hz).",
                nameof(monitor));
    }

    /// <summary>
    /// Validates a monitor identifier. Real Windows monitor identifiers are GDI device
    /// names in the form "\\.\DISPLAY1" — these legitimately contain backslashes, so a
    /// blanket "no backslash" rule (as a naive path-traversal guard would apply) would
    /// reject every real monitor and make mapping permanently impossible. Traversal is
    /// instead prevented by requiring an exact "\\.\DISPLAY&lt;digits&gt;" shape.
    ///
    /// Still enforced because tabcal.exe consumes this value — but it is a runtime handle,
    /// not the persisted identity.
    /// </summary>
    private static void ValidateMonitorId(string? monitorId, string paramName)
    {
        RejectUnsafeText(monitorId, paramName);

        const string prefix = @"\\.\DISPLAY";
        if (!monitorId!.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"{paramName} must be a valid GDI display device name (e.g. '\\\\.\\DISPLAY1').", paramName);

        var suffix = monitorId[prefix.Length..];
        if (suffix.Length == 0 || !suffix.All(char.IsAsciiDigit))
            throw new ArgumentException($"{paramName} must end with a numeric display index.", paramName);
    }

    /// <summary>
    /// Validates a device interface path. Real paths look like
    /// "\\?\HID#VID_046D&amp;PID_C52B#7&amp;abc123&amp;0&amp;0000#{4d1e55b2-...}" or
    /// "\\?\DISPLAY#CHR8910#5&amp;2c72b841&amp;0&amp;UID250118#{e6f07b5f-...}" — these
    /// legitimately contain backslashes and "&amp;", so path-traversal is prevented by
    /// requiring the expected "\\?\" device-interface prefix, rejecting ".." sequences,
    /// and restricting the remainder to the character set Windows actually emits.
    /// </summary>
    private static void ValidateDevicePath(string? devicePath, string paramName)
    {
        RejectUnsafeText(devicePath, paramName);

        if (devicePath!.Contains(".."))
            throw new ArgumentException($"{paramName} contains invalid path traversal sequence.", paramName);

        const string expectedPrefix = @"\\?\";
        if (!devicePath.StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"{paramName} must be a valid device interface path starting with '\\\\?\\'.", paramName);

        if (!devicePath.Skip(expectedPrefix.Length).All(IsValidDevicePathChar))
            throw new ArgumentException(
                $"{paramName} contains characters not valid in a device interface path.", paramName);
    }

    private static void ValidateOptionalDevicePath(string? devicePath, string paramName)
    {
        if (!string.IsNullOrWhiteSpace(devicePath))
            ValidateDevicePath(devicePath, paramName);
    }

    /// <summary>
    /// Validates the touch-side anchor: a port key
    /// ("PORT\VID_14E1&amp;PID_3508\PCIROOT(0)/PCI(1400)/USBROOT(0)/USB(1)/USB(4)/USB(6)") or, for
    /// hardware without a location path, a PnP device instance id, e.g.
    /// "USB\VID_14E1&amp;PID_3508\7&amp;1b9afb93&amp;0&amp;6". Unlike a device interface path
    /// this has no "\\?\" prefix and no interface GUID: it is an enumerator-qualified
    /// instance path with exactly the shape "ENUMERATOR\DEVICE-ID\INSTANCE-ID".
    /// </summary>
    private static void ValidateInstanceId(string? instanceId, string paramName)
    {
        RejectUnsafeText(instanceId, paramName);

        if (instanceId!.Contains(".."))
            throw new ArgumentException($"{paramName} contains invalid path traversal sequence.", paramName);

        if (instanceId.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException(
                $"{paramName} must be a PnP instance id (e.g. 'USB\\VID_14E1&PID_3508\\7&1b9afb93&0&6'), " +
                "not a device interface path.", paramName);

        var segments = instanceId.Split('\\');
        if (segments.Length < 2 || segments.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                $"{paramName} must have the form 'ENUMERATOR\\DEVICE\\INSTANCE'.", paramName);

        if (!instanceId.All(IsValidInstanceIdChar))
            throw new ArgumentException(
                $"{paramName} contains characters not valid in a PnP instance id.", paramName);
    }

    private static void ValidateOptionalInstanceId(string? instanceId, string paramName)
    {
        if (!string.IsNullOrWhiteSpace(instanceId))
            ValidateInstanceId(instanceId, paramName);
    }

    /// <summary>
    /// Validates a connector label such as "DP-2" or "HDMI-1": letters/digits, one hyphen,
    /// trailing instance number.
    /// </summary>
    private static void ValidateOptionalConnectorLabel(string? connectorLabel, string paramName)
    {
        if (string.IsNullOrWhiteSpace(connectorLabel))
            return;

        RejectUnsafeText(connectorLabel, paramName);

        int separator = connectorLabel!.LastIndexOf('-');
        if (separator <= 0 || separator == connectorLabel.Length - 1)
            throw new ArgumentException(
                $"{paramName} must have the form '<CONNECTOR>-<INSTANCE>' (e.g. 'DP-2').", paramName);

        var prefix = connectorLabel[..separator];
        var instance = connectorLabel[(separator + 1)..];

        if (!prefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            throw new ArgumentException($"{paramName} has an invalid connector prefix.", paramName);

        if (!instance.All(char.IsAsciiDigit))
            throw new ArgumentException($"{paramName} must end with a numeric connector instance.", paramName);
    }

    /// <summary>
    /// Validates a bounds key "x,y,width,height". x and y may be negative — a monitor placed
    /// to the left of the primary has a negative origin, which is exactly the case on a
    /// two-panel desktop and must not be rejected.
    /// </summary>
    private static void ValidateOptionalBoundsKey(string? boundsKey, string paramName)
    {
        if (string.IsNullOrWhiteSpace(boundsKey))
            return;

        RejectUnsafeText(boundsKey, paramName);

        var parts = boundsKey!.Split(',');
        if (parts.Length != 4)
            throw new ArgumentException(
                $"{paramName} must have the form 'x,y,width,height'.", paramName);

        if (!int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _))
            throw new ArgumentException($"{paramName} has a non-numeric origin.", paramName);

        if (!int.TryParse(parts[2], out int width) || !int.TryParse(parts[3], out int height))
            throw new ArgumentException($"{paramName} has a non-numeric size.", paramName);

        if (width <= 0 || height <= 0)
            throw new ArgumentException($"{paramName} must describe a positive area.", paramName);
    }

    /// <summary>
    /// Shared guard: non-empty, length-bounded, free of NUL and control characters.
    /// </summary>
    private static void RejectUnsafeText(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} cannot be empty.", paramName);

        if (value.Length > MaxIdentifierLength)
            throw new ArgumentException(
                $"{paramName} exceeds maximum length of {MaxIdentifierLength}.", paramName);

        if (value.Contains('\0') || value.Any(char.IsControl))
            throw new ArgumentException($"{paramName} contains invalid control characters.", paramName);
    }

    private static bool IsValidDevicePathChar(char c) =>
        char.IsLetterOrDigit(c) || c is '#' or '&' or '_' or '-' or '.' or ',' or '{' or '}' or '\\';

    // '(' ')' '/' occur in port keys ("PORT\VID_14E1&PID_3508\PCIROOT(0)/PCI(1400)/...").
    // ".." is still rejected above, so '/' cannot form a traversal sequence.
    private static bool IsValidInstanceIdChar(char c) =>
        char.IsLetterOrDigit(c) || c is '&' or '_' or '-' or '.' or '\\' or '{' or '}' or '(' or ')' or '/';

    /// <summary>
    /// Adds a new vendor ID to the whitelist (admin operation, logged).
    /// </summary>
    public static void AddAllowedVendorId(ushort vendorId)
    {
        if (vendorId == 0)
            throw new ArgumentException("Vendor ID 0 is reserved.", nameof(vendorId));

        // W-8 fix: lock guards concurrent HashSet.Add + Contains calls
        lock (_vendorIdLock)
        {
            AllowedVendorIds.Add(vendorId);
        }
    }

    /// <summary>
    /// Gets a snapshot of the current whitelist of allowed vendor IDs.
    /// Returns a copy: handing out the live set would let callers read it without the lock
    /// that <see cref="AddAllowedVendorId"/> takes.
    /// </summary>
    public static IReadOnlySet<ushort> GetAllowedVendorIds()
    {
        lock (_vendorIdLock)
        {
            return new HashSet<ushort>(AllowedVendorIds);
        }
    }
}
