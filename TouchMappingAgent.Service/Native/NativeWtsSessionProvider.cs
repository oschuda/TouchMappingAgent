using System.Runtime.InteropServices;

namespace TouchMappingAgent.Service.Native;

/// <summary>
/// Real <see cref="IWtsSessionProvider"/> backed by <c>wtsapi32.dll</c>. Explicit "W" (wide/
/// Unicode) entry points are pinvoked directly rather than relying on CharSet auto-selection,
/// so behaviour cannot change with the process's default charset.
/// </summary>
public sealed class NativeWtsSessionProvider : IWtsSessionProvider
{
    // WTS_CURRENT_SERVER_HANDLE — null means "the local Terminal Services instance", which is
    // always what a service on the same box wants; there is no remote server to address here.
    private static readonly IntPtr LocalServerHandle = IntPtr.Zero;

    private const int WtsInfoClassConnectState = 8; // WTS_INFO_CLASS.WTSConnectState

    /// <inheritdoc/>
    public IReadOnlyList<WtsSessionInfo> EnumerateSessions()
    {
        if (!WTSEnumerateSessionsW(LocalServerHandle, Reserved: 0, Version: 1, out var pSessionInfo, out var count))
        {
            throw new InvalidOperationException(
                $"WTSEnumerateSessionsW failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var result = new List<WtsSessionInfo>(count);
            var structSize = Marshal.SizeOf<WTS_SESSION_INFO>();

            for (int i = 0; i < count; i++)
            {
                var current = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessionInfo + i * structSize);
                var winStationName = current.pWinStationName == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(current.pWinStationName) ?? string.Empty;

                result.Add(new WtsSessionInfo(current.SessionId, winStationName, (WtsConnectState)current.State));
            }

            return result;
        }
        finally
        {
            WTSFreeMemory(pSessionInfo);
        }
    }

    /// <inheritdoc/>
    public WtsConnectState? QueryConnectState(int sessionId)
    {
        if (!WTSQuerySessionInformationW(
                LocalServerHandle, sessionId, WtsInfoClassConnectState, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return (WtsConnectState)Marshal.ReadInt32(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <inheritdoc/>
    public bool LogoffSession(int sessionId, bool wait) =>
        WTSLogoffSession(LocalServerHandle, sessionId, wait);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int Reserved, int Version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSLogoffSession", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
