using System.Runtime.InteropServices;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Shared.Hardware;

/// <summary>
/// Enumerates physical monitors attached to the desktop.
///
/// Combines two Win32 APIs because neither alone is sufficient:
///
/// * EnumDisplayDevices / EnumDisplaySettings give the "\\.\DISPLAYn" GDI device name that
///   tabcal.exe's DisplayID= switch expects, plus the current mode and desktop position.
///   They do NOT expose which physical graphics connector a monitor hangs on — at adapter
///   level, DISPLAY_DEVICE.DeviceString is the ADAPTER description, so on a two-output card
///   both monitors come back with the identical name (confirmed empirically: two distinct
///   panels both reported "DisplayLink USB Device").
///
/// * The Connected Display Configuration (CCD) API — QueryDisplayConfig plus
///   DisplayConfigGetDeviceInfo — gives the connector technology and instance (DP-2 vs DP-3),
///   the target id, the monitor's PnP device path and its real friendly name. That is what
///   makes two structurally identical panels behind extenders tellable apart at all, since
///   their emulated EDID is byte-for-byte the same.
///
/// LIVES IN Shared (not TouchMappingAgent.Service), and MUST be called from the interactive
/// WPF client, not from the background service. Confirmed empirically: calling this exact
/// code from the SYSTEM/Session-0 service process returns an EMPTY monitor list, even on a
/// machine where the same code — run from a normal interactive process — correctly finds the
/// real attached monitors. This is the same class of Session-0 visibility gap already known
/// from tabcal.exe (see ComplianceRequestHandler.ProcessMappingAsync): a Windows Service's
/// EnumDisplayDevices/EnumDisplaySettings view of "what's attached to the desktop" does not
/// reliably reflect the interactively logged-on user's actual session, especially for
/// USB/DisplayLink-attached or otherwise virtualized displays. Reading monitor info requires
/// no elevated privilege, so there is no reason to route it through the service at all — the
/// WPF client (which always runs in the interactive session) calls this directly.
/// </summary>
public static class DisplayEnumerator
{
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DefaultRefreshRateHz = 60;

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const int ERROR_SUCCESS = 0;

    /// <summary>
    /// Enumerates all monitors currently attached to the desktop.
    /// Never throws: individual enumeration failures are swallowed and result in an
    /// empty or partial list (MVO 2023/1230 partial-success principle).
    /// </summary>
    public static List<MonitorInfo> EnumerateMonitors()
    {
        // Connector metadata keyed by GDI device name. Empty when the CCD API is unavailable
        // or fails; the GDI pass below then still produces usable (if anchor-less) entries.
        var connectorInfo = TryQueryConnectorInfo();

        var monitors = new List<MonitorInfo>();

        uint deviceIndex = 0;
        while (true)
        {
            var device = new DISPLAY_DEVICE();
            device.cb = Marshal.SizeOf(device);

            bool hasDevice;
            try
            {
                hasDevice = EnumDisplayDevices(null, deviceIndex, ref device, 0);
            }
            catch (EntryPointNotFoundException)
            {
                break;
            }
            catch (DllNotFoundException)
            {
                break;
            }

            if (!hasDevice)
                break;

            deviceIndex++;

            bool attached = (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            if (!attached)
                continue;

            try
            {
                var monitor = TryBuildMonitorInfo(device, connectorInfo);
                if (monitor != null)
                    monitors.Add(monitor);
            }
            catch
            {
                // Skip this monitor, continue enumerating the rest.
            }
        }

        return monitors;
    }

    /// <summary>
    /// Finds the monitor whose bounds contain the given virtual-desktop point. Used by the
    /// learn flow to turn "the operator touched here" into "that is this monitor".
    /// </summary>
    public static MonitorInfo? FindMonitorContaining(IEnumerable<MonitorInfo> monitors, int x, int y) =>
        monitors.FirstOrDefault(m => x >= m.X && x < m.Right && y >= m.Y && y < m.Bottom);

    private static MonitorInfo? TryBuildMonitorInfo(
        DISPLAY_DEVICE device,
        IReadOnlyDictionary<string, ConnectorInfo> connectorInfo)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceName))
            return null;

        int width = 0;
        int height = 0;
        int x = 0;
        int y = 0;
        int refreshRate = DefaultRefreshRateHz;

        var mode = new DEVMODE();
        mode.dmDeviceName = new string(new char[32]);
        mode.dmFormName = new string(new char[32]);
        mode.dmSize = (short)Marshal.SizeOf<DEVMODE>();

        if (EnumDisplaySettings(device.DeviceName, ENUM_CURRENT_SETTINGS, ref mode))
        {
            if (mode.dmPelsWidth > 0)
                width = mode.dmPelsWidth;
            if (mode.dmPelsHeight > 0)
                height = mode.dmPelsHeight;
            if (mode.dmDisplayFrequency > 1) // 0/1 mean "hardware default", not a real Hz value
                refreshRate = mode.dmDisplayFrequency;

            // Desktop position. Previously read into the struct and then discarded, which is
            // why nothing downstream could tell the left monitor from the right one.
            x = mode.dmPositionX;
            y = mode.dmPositionY;
        }

        connectorInfo.TryGetValue(device.DeviceName, out var connector);

        // Prefer the CCD friendly name ("DM7000"). DISPLAY_DEVICE.DeviceString at adapter
        // level is the graphics card description and is identical for every output on the
        // same card, so it cannot serve as a label.
        var friendlyName =
            !string.IsNullOrWhiteSpace(connector?.FriendlyName) ? connector!.FriendlyName
            : !string.IsNullOrWhiteSpace(device.DeviceString) ? device.DeviceString
            : device.DeviceName;

        bool isPrimary = (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;

        return new MonitorInfo(
            DeviceId: device.DeviceName,
            DisplayName: friendlyName,
            Width: width,
            Height: height,
            RefreshRate: refreshRate,
            X: x,
            Y: y,
            ConnectorLabel: connector?.ConnectorLabel ?? string.Empty,
            AdapterId: connector?.AdapterId ?? string.Empty,
            TargetId: connector?.TargetId ?? 0,
            DevicePath: connector?.DevicePath ?? string.Empty,
            IsPrimary: isPrimary);
    }

    /// <summary>
    /// Reads connector metadata for every active display path via the CCD API.
    /// Returns an empty dictionary on any failure — callers degrade to GDI-only data.
    /// </summary>
    private static Dictionary<string, ConnectorInfo> TryQueryConnectorInfo()
    {
        var result = new Dictionary<string, ConnectorInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != ERROR_SUCCESS)
                return result;

            if (pathCount == 0)
                return result;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != ERROR_SUCCESS)
                return result;

            for (int i = 0; i < pathCount; i++)
            {
                try
                {
                    var path = paths[i];

                    var gdiName = TryGetSourceGdiName(path.sourceInfo.adapterId, path.sourceInfo.id);
                    if (string.IsNullOrWhiteSpace(gdiName))
                        continue;

                    var target = TryGetTargetDeviceName(path.targetInfo.adapterId, path.targetInfo.id);

                    result[gdiName] = new ConnectorInfo(
                        ConnectorLabel: BuildConnectorLabel(path.targetInfo.outputTechnology, target?.connectorInstance ?? 0),
                        AdapterId: FormatLuid(path.targetInfo.adapterId),
                        TargetId: path.targetInfo.id,
                        DevicePath: target?.monitorDevicePath ?? string.Empty,
                        FriendlyName: target?.monitorFriendlyDeviceName ?? string.Empty);
                }
                catch
                {
                    // Skip this path, keep the rest.
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
            // CCD API unavailable (pre-Windows 7). Caller degrades to GDI-only data.
        }
        catch (DllNotFoundException)
        {
            // Same.
        }
        catch
        {
            // Never let connector discovery break monitor enumeration.
        }

        return result;
    }

    private static string? TryGetSourceGdiName(LUID adapterId, uint sourceId)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = adapterId,
                id = sourceId
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.viewGdiDeviceName
            : null;
    }

    private static DISPLAYCONFIG_TARGET_DEVICE_NAME? TryGetTargetDeviceName(LUID adapterId, uint targetId)
    {
        var request = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = adapterId,
                id = targetId
            }
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS ? request : null;
    }

    /// <summary>
    /// Builds the operator-facing connector label ("DP-2", "HDMI-1", ...) from the output
    /// technology and the connector instance the CCD API reports.
    /// </summary>
    internal static string BuildConnectorLabel(uint outputTechnology, uint connectorInstance)
    {
        var prefix = outputTechnology switch
        {
            0 => "VGA",
            4 => "DVI",
            5 => "HDMI",
            6 => "LVDS",
            9 => "SDI",
            10 => "DP",           // DISPLAYPORT_EXTERNAL — the target installation's case
            11 => "eDP",          // DISPLAYPORT_EMBEDDED
            12 => "UDI",
            13 => "UDI-EMB",
            15 => "MIRACAST",
            16 => "INDIRECT",
            0x80000000 => "INTERNAL",
            _ => "OUT"
        };

        return $"{prefix}-{connectorInstance}";
    }

    /// <summary>
    /// Formats an adapter LUID the way the hardware survey does ("00000000-0001F416").
    /// Diagnostics only — the LUID is regenerated on every boot and must never be a key.
    /// </summary>
    internal static string FormatLuid(LUID luid) => $"{luid.HighPart:X8}-{luid.LowPart:X8}";

    private sealed record ConnectorInfo(
        string ConnectorLabel,
        string AdapterId,
        uint TargetId,
        string DevicePath,
        string FriendlyName);

    // =====================================================================================
    // Win32 interop
    // =====================================================================================

    /// <summary>Locally unique identifier for a graphics adapter.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID
    {
        /// <summary>Low 32 bits.</summary>
        public uint LowPart;
        /// <summary>High 32 bits.</summary>
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        [MarshalAs(UnmanagedType.Bool)]
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // The mode union is not read here (position and size come from EnumDisplaySettings), but
    // QueryDisplayConfig writes into the array, so the element size must be exact: 48 bytes
    // for the largest member (DISPLAYCONFIG_VIDEO_SIGNAL_INFO).
    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        [MarshalAs(UnmanagedType.U4)]
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        [MarshalAs(UnmanagedType.U4)]
        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplaySettings(
        string deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);
}
