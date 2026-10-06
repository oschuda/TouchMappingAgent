using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// Display refresh service that broadcasts WM_SETTINGCHANGE to all windows.
/// Used to trigger safe system-wide display and input device reconfiguration.
/// </summary>
public class DisplayRefreshService
{
    private readonly ILogger<DisplayRefreshService> _logger;

    // Windows API P/Invoke for broadcasting messages
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int SendMessage(int hWnd, int Msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        IntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out IntPtr lpdwResult);

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint TimeoutMs = 2000;

    /// <summary>Initializes a new instance of <see cref="DisplayRefreshService"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public DisplayRefreshService(ILogger<DisplayRefreshService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Broadcasts WM_SETTINGCHANGE to all windows to notify them of display/input device changes.
    /// Safe, non-blocking operation that allows processes to adapt to new hardware configuration.
    /// </summary>
    public void InvokeSafeRefresh()
    {
        try
        {
            _logger.LogInformation("Broadcasting WM_SETTINGCHANGE to all windows");

            // Notify all windows about display settings change
            SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_SETTINGCHANGE,
                IntPtr.Zero,
                "intl",  // International settings changed
                SMTO_ABORTIFHUNG,
                TimeoutMs,
                out IntPtr result);

            _logger.LogInformation("Display refresh broadcast completed successfully");
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "user32.dll not found - WM_SETTINGCHANGE not available");
            // Non-critical - continue
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending WM_SETTINGCHANGE broadcast");
            // Non-critical - display refresh is informational, not essential
        }
    }

    /// <summary>
    /// Sends a device change notification (WM_DEVICECHANGE) to allow applications to detect new USB devices.
    /// </summary>
    public void NotifyDeviceChange()
    {
        try
        {
            _logger.LogInformation("Broadcasting WM_DEVICECHANGE notification");

            const int WM_DEVICECHANGE = 0x0219;
            const int DBT_DEVICEARRIVAL = 0x8000;

            SendMessageTimeout(
                (IntPtr)HWND_BROADCAST,
                WM_DEVICECHANGE,
                (IntPtr)DBT_DEVICEARRIVAL,
                string.Empty,
                SMTO_ABORTIFHUNG,
                TimeoutMs,
                out IntPtr result);

            _logger.LogInformation("Device change notification broadcast completed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error sending WM_DEVICECHANGE notification");
            // Non-critical
        }
    }
}