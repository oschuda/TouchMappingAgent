using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Reports which PHYSICAL digitizer produced a touch, by listening to raw input.
///
/// WHY RAW INPUT AND NOT WPF's TOUCH EVENTS: WPF's TouchDown gives a TouchDevice with an
/// ephemeral contact id. It says a touch happened and where, but not which hardware sent it.
/// With two structurally identical PM1715 digitizers that is precisely the missing piece — the
/// whole point of the learn step is to find out which of the two the operator just touched.
///
/// Raw input carries a device handle on every message; GetRawInputDeviceInfo turns that handle
/// into the HID interface path, which TouchDigitizerEnumerator can match back to a device and
/// therefore to its USB parent instance — the anchor that gets persisted.
///
/// RIDEV_INPUTSINK is used so the messages arrive even when the identify window does not have
/// keyboard focus, which matters on a multi-monitor desktop where the operator may touch a
/// screen the window is not focused on.
/// </summary>
public sealed class RawTouchListener : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_HEADER = 0x10000005;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIM_TYPEHID = 2;

    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    private const ushort HID_USAGE_DIGITIZER_PEN = 0x02;
    private const ushort HID_USAGE_DIGITIZER_TOUCHSCREEN = 0x04;
    private const ushort HID_USAGE_DIGITIZER_TOUCHPAD = 0x05;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// Raised with the HID interface path of the digitizer that produced a touch.
    /// Fires on the UI thread (it comes out of the window procedure).
    /// </summary>
    public event Action<string>? TouchDetected;

    /// <summary>
    /// Starts listening for raw digitizer input directed at the given window handle.
    /// </summary>
    /// <param name="hwnd">Window handle to receive WM_INPUT messages.</param>
    /// <exception cref="InvalidOperationException">If the handle has no HwndSource.</exception>
    public RawTouchListener(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("No HwndSource for the supplied window handle.");

        _source.AddHook(WndProc);
        _registered = RegisterDevices(RIDEV_INPUTSINK, hwnd);
    }

    /// <summary>
    /// True when the raw input registration succeeded. When false the caller must fall back to
    /// a manual selection: without raw input there is no way to tell which digitizer was
    /// touched, and guessing would defeat the purpose of the learn step.
    /// </summary>
    public bool IsListening => _registered;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || _disposed)
            return IntPtr.Zero;

        try
        {
            var devicePath = TryGetDevicePath(lParam);
            if (!string.IsNullOrEmpty(devicePath))
                TouchDetected?.Invoke(devicePath!);
        }
        catch
        {
            // A malformed raw input message must never take down the window procedure.
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Reads only the RAWINPUTHEADER (RID_HEADER) — the payload is irrelevant here, the device
    /// handle is the whole point — and resolves it to the HID interface path.
    /// </summary>
    private static string? TryGetDevicePath(IntPtr hRawInput)
    {
        int headerSize = Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = (uint)headerSize;
        var header = new RAWINPUTHEADER();

        int written = GetRawInputData(hRawInput, RID_HEADER, ref header, ref size, headerSize);
        if (written != headerSize || header.dwType != RIM_TYPEHID || header.hDevice == IntPtr.Zero)
            return null;

        return TryGetDeviceName(header.hDevice);
    }

    private static string? TryGetDeviceName(IntPtr hDevice)
    {
        uint charCount = 0;
        if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref charCount) != 0 || charCount == 0)
            return null;

        var buffer = new StringBuilder((int)charCount + 1);
        int result = GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref charCount);

        return result > 0 ? buffer.ToString() : null;
    }

    private static bool RegisterDevices(uint flags, IntPtr hwnd)
    {
        var devices = new[]
        {
            NewDevice(HID_USAGE_DIGITIZER_TOUCHSCREEN, flags, hwnd),
            NewDevice(HID_USAGE_DIGITIZER_PEN, flags, hwnd),
            NewDevice(HID_USAGE_DIGITIZER_TOUCHPAD, flags, hwnd)
        };

        try
        {
            return RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static RAWINPUTDEVICE NewDevice(ushort usage, uint flags, IntPtr hwnd) => new()
    {
        usUsagePage = HID_USAGE_PAGE_DIGITIZER,
        usUsage = usage,
        dwFlags = flags,
        // RIDEV_REMOVE requires hwndTarget to be NULL; any other flag needs the real handle.
        hwndTarget = flags == RIDEV_REMOVE ? IntPtr.Zero : hwnd
    };

    /// <summary>
    /// Unregisters the raw input devices and detaches the window hook.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (_registered)
            {
                RegisterDevices(RIDEV_REMOVE, _hwnd);
                _registered = false;
            }
        }
        catch
        {
            // Best effort — the window is going away regardless.
        }

        try
        {
            _source.RemoveHook(WndProc);
        }
        catch
        {
            // Same.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetRawInputData(
        IntPtr hRawInput, uint uiCommand, ref RAWINPUTHEADER pData, ref uint pcbSize, int cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetRawInputDeviceInfo(
        IntPtr hDevice, uint uiCommand, StringBuilder pData, ref uint pcbSize);
}
