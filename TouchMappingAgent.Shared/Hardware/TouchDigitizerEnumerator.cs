using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Shared.Hardware;

/// <summary>
/// Enumerates connected HID digitizer devices (touch screens, pens, touch pads) via
/// SetupAPI + hid.dll + cfgmgr32. Only devices whose HID top-level collection reports
/// Usage Page 0x0D (Digitizer) are returned, so generic HID devices (keyboards,
/// mice, game controllers) are excluded.
///
/// Beyond the interface path, each device carries its PnP instance id and — the part that
/// matters for persistent mapping — its USB PARENT's instance id and physical location path.
/// On the target installation two identical PM1715 digitizers hang off USB range extenders, so
/// vendor/product/name are useless for telling them apart. The HID node itself has no location
/// path, but its USB parent does, and that path (one port number per hub level) is what
/// identifies the physical side. See <see cref="HidDeviceInfo.HardwareKey"/>.
///
/// LIVES IN Shared: the interactive WPF client needs it for the learn flow (turning a raw
/// touch event's device path back into a hardware anchor), and the service needs it to answer
/// GetTouchDevices. Enumerating HID devices needs no elevated privilege.
/// </summary>
public static class TouchDigitizerEnumerator
{
    private static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private const int DIGCF_PRESENT = 0x2;
    private const int DIGCF_DEVICEINTERFACE = 0x10;
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint OPEN_EXISTING = 3;
    private const int HIDP_STATUS_SUCCESS = 0x00110000;
    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    private const int CR_SUCCESS = 0;
    private const int MaxDeviceIdLength = 512;

    // Digitizer, Pen, Touch Screen, Touch Pad (HID Usage Tables §14 "Digitizers Page")
    private static readonly HashSet<ushort> DigitizerUsages = new() { 0x01, 0x02, 0x04, 0x05 };

    /// <summary>
    /// Enumerates all connected touch/pen digitizer HID devices.
    /// Never throws: individual device read failures are skipped (MVO 2023/1230
    /// partial-success principle) so one broken device cannot hide all the others.
    /// </summary>
    public static List<HidDeviceInfo> EnumerateDigitizers()
    {
        var results = new List<HidDeviceInfo>();
        var hidGuid = GUID_DEVINTERFACE_HID;

        IntPtr deviceInfoSet = SetupDiGetClassDevs(
            ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == new IntPtr(-1))
            return results;

        try
        {
            int index = 0;
            while (true)
            {
                var interfaceData = new SP_DEVICE_INTERFACE_DATA();
                interfaceData.cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>();

                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    break;

                index++;

                try
                {
                    var devInfoData = new SP_DEVINFO_DATA();
                    devInfoData.cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>();

                    var devicePath = TryGetDevicePath(deviceInfoSet, ref interfaceData, ref devInfoData);
                    if (devicePath == null)
                        continue;

                    var device = TryReadDigitizerInfo(devicePath);
                    if (device == null)
                        continue;

                    // Only now (after the digitizer filter) pay for the PnP lookups.
                    var instanceId = TryGetDeviceInstanceId(deviceInfoSet, ref devInfoData) ?? string.Empty;
                    var (parentInstanceId, parentLocationPath) = TryGetParentIdentity(devInfoData.DevInst);

                    results.Add(device with
                    {
                        InstanceId = instanceId,
                        ParentInstanceId = parentInstanceId ?? string.Empty,
                        ParentLocationPath = parentLocationPath ?? string.Empty
                    });
                }
                catch
                {
                    // Skip this device interface, continue enumerating the rest.
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return results;
    }

    /// <summary>
    /// Finds the digitizer whose interface path matches the one raw input reported. Casing
    /// differs between the raw input and SetupAPI views of the same device, so the comparison
    /// is normalised on both sides.
    /// </summary>
    public static HidDeviceInfo? FindByDevicePath(IEnumerable<HidDeviceInfo> devices, string devicePath)
    {
        var normalized = HidDeviceInfo.NormalizeDevicePath(devicePath);
        if (normalized.Length == 0)
            return null;

        return devices.FirstOrDefault(d =>
            HidDeviceInfo.NormalizeDevicePath(d.DevicePath) == normalized);
    }

    private static string? TryGetDevicePath(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA interfaceData,
        ref SP_DEVINFO_DATA devInfoData)
    {
        SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out int requiredSize, IntPtr.Zero);

        if (requiredSize <= 0)
            return null;

        IntPtr detailDataBuffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize is the size of the *fixed* header only
            // (a well-known pinvoke.net workaround for the packing of the trailing char[] field):
            // 8 bytes on 64-bit processes (4-byte int + 4-byte padding before the char array),
            // 6 bytes on 32-bit processes (4-byte int + 2-byte char).
            Marshal.WriteInt32(detailDataBuffer, Environment.Is64BitProcess ? 8 : 4 + Marshal.SystemDefaultCharSize);

            // Passing the SP_DEVINFO_DATA out-parameter here (previously IntPtr.Zero) is what
            // gives us the devnode handle needed for the CM_Get_Parent lookup below.
            if (!SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet, ref interfaceData, detailDataBuffer, requiredSize, out _, ref devInfoData))
            {
                return null;
            }

            return Marshal.PtrToStringAuto(detailDataBuffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detailDataBuffer);
        }
    }

    /// <summary>Reads the device's own PnP instance id (e.g. "HID\VID_14E1&amp;PID_3508\8&amp;25188e6b&amp;0&amp;0000").</summary>
    private static string? TryGetDeviceInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA devInfoData)
    {
        try
        {
            var buffer = new StringBuilder(MaxDeviceIdLength);
            return SetupDiGetDeviceInstanceId(deviceInfoSet, ref devInfoData, buffer, buffer.Capacity, out _)
                ? buffer.ToString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Walks one level up the device tree to the USB device and reads both its instance id
    /// (e.g. "USB\VID_14E1&amp;PID_3508\7&amp;1b9afb93&amp;0&amp;6") and its physical location path
    /// (e.g. "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)#USB(4)#USB(6)").
    ///
    /// The location path is the real port anchor. The instance id is NOT stable behind the
    /// target installation's range extenders: both extender hubs report the same USB serial
    /// number, so whichever enumerates first gets the serial-based id and the other a
    /// port-derived one — and the digitizer ids below them follow suit. Measured 2026-10-06:
    /// "7&amp;1b9afb93&amp;0&amp;6" belonged to one physical touch screen before an extender
    /// power-cycle and to the other one after it.
    /// </summary>
    private static (string? InstanceId, string? LocationPath) TryGetParentIdentity(uint devInst)
    {
        try
        {
            if (CM_Get_Parent(out uint parentDevInst, devInst, 0) != CR_SUCCESS)
                return (null, null);

            var buffer = new StringBuilder(MaxDeviceIdLength);
            var instanceId = CM_Get_Device_ID(parentDevInst, buffer, buffer.Capacity, 0) == CR_SUCCESS
                ? buffer.ToString()
                : null;

            return (instanceId, TryGetPciRootLocationPath(parentDevInst));
        }
        catch
        {
            // Includes EntryPointNotFoundException / DllNotFoundException on stripped-down images.
            return (null, null);
        }
    }

    /// <summary>
    /// Reads DEVPKEY_Device_LocationPaths and returns the "PCIROOT(...)" entry. The property is a
    /// string list that typically also carries an ACPI-namespace variant of the same path; the
    /// PCIROOT form is the one that encodes every hub port down to the device.
    /// </summary>
    private static string? TryGetPciRootLocationPath(uint devInst)
    {
        var key = DEVPKEY_Device_LocationPaths;
        uint size = 0;

        int result = CM_Get_DevNode_Property(devInst, ref key, out _, null, ref size, 0);
        if (result != CR_BUFFER_SMALL || size == 0)
            return null;

        var buffer = new byte[size];
        if (CM_Get_DevNode_Property(devInst, ref key, out uint type, buffer, ref size, 0) != CR_SUCCESS ||
            type != DEVPROP_TYPE_STRING_LIST)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, (int)size)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(p => p.StartsWith("PCIROOT(", StringComparison.OrdinalIgnoreCase));
    }

    private static HidDeviceInfo? TryReadDigitizerInfo(string devicePath)
    {
        using var handle = CreateFile(
            devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        if (!HidD_GetPreparsedData(handle, out var preparsedData) || preparsedData == IntPtr.Zero)
            return null;

        try
        {
            var caps = new HIDP_CAPS();
            if (HidP_GetCaps(preparsedData, ref caps) != HIDP_STATUS_SUCCESS)
                return null;

            if (caps.UsagePage != HID_USAGE_PAGE_DIGITIZER || !DigitizerUsages.Contains(caps.Usage))
                return null;

            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes))
                return null;

            var productName = TryGetProductString(handle)
                ?? $"HID Digitizer (VID_{attributes.VendorID:X4}&PID_{attributes.ProductID:X4})";

            return new HidDeviceInfo(devicePath, productName, attributes.VendorID, attributes.ProductID);
        }
        finally
        {
            HidD_FreePreparsedData(preparsedData);
        }
    }

    private static string? TryGetProductString(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(256);
        return HidD_GetProductString(handle, buffer, buffer.Capacity * 2) ? buffer.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
        out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize,
        out int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData,
        StringBuilder deviceInstanceId, int deviceInstanceIdSize, out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer, int bufferLen, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private const int CR_BUFFER_SMALL = 0x1A;
    private const uint DEVPROP_TYPE_STRING_LIST = 0x2012;

    private static readonly DEVPROPKEY DEVPKEY_Device_LocationPaths = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 37
    };

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Property(
        uint dnDevInst, ref DEVPROPKEY propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint ulFlags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool HidD_GetProductString(SafeFileHandle hidDeviceObject, StringBuilder buffer, int bufferLength);
}
