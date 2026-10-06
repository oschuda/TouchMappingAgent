namespace TouchMappingAgent.Service.Native;

/// <summary>
/// Connection state of a Terminal Services session, mirroring the native
/// <c>WTS_CONNECTSTATE_CLASS</c> enum (wtsapi32.h). Values are intentionally numbered to match
/// the native enum exactly, since <see cref="NativeWtsSessionProvider"/> casts the raw
/// <c>WTS_SESSION_INFO.State</c> field straight into this type.
/// </summary>
public enum WtsConnectState
{
    /// <summary>A user is logged on and actively using the session.</summary>
    Active = 0,

    /// <summary>An RDP client is connected to the session.</summary>
    Connected = 1,

    /// <summary>The session is in the process of connecting.</summary>
    ConnectQuery = 2,

    /// <summary>The session is remote-controlling another session (shadow).</summary>
    Shadow = 3,

    /// <summary>The session is active but no client is attached — this is the state the
    /// physical console is normally in after Ctrl+Alt+Del "lock" or after an RDP session
    /// takes over the display without logging the console off.</summary>
    Disconnected = 4,

    /// <summary>The session is idle, waiting for a client connection.</summary>
    Idle = 5,

    /// <summary>The session is listening for a client connection.</summary>
    Listen = 6,

    /// <summary>The session is being reset.</summary>
    Reset = 7,

    /// <summary>The session is down due to an error.</summary>
    Down = 8,

    /// <summary>The session is initializing.</summary>
    Init = 9,
}

/// <summary>
/// One session as reported by <c>WTSEnumerateSessions</c>: its id, station name and connect
/// state. The physical console is the one session whose <see cref="WinStationName"/> is
/// literally "Console" — RDP sessions are always named "RDP-Tcp#&lt;n&gt;".
/// </summary>
public readonly record struct WtsSessionInfo(int SessionId, string WinStationName, WtsConnectState State);

/// <summary>
/// Abstraction over the three <c>wtsapi32.dll</c> calls the console session watchdog needs.
/// Exists purely so the watchdog and <see cref="Services.ConsoleSessionResetService"/> can be
/// unit-tested against a fake session table instead of the real Terminal Services subsystem —
/// there is no safe way to exercise <c>WTSLogoffSession</c> against a real session in a test run.
/// </summary>
public interface IWtsSessionProvider
{
    /// <summary>Enumerates every session known to Terminal Services on the local machine.</summary>
    IReadOnlyList<WtsSessionInfo> EnumerateSessions();

    /// <summary>
    /// Re-queries the live connect state of one session by id. Used as a last-instant
    /// double-check immediately before a destructive reset, since the state captured by
    /// <see cref="EnumerateSessions"/> may be milliseconds stale (the operator could have just
    /// logged back in). Returns null if the session no longer exists or the query fails.
    /// </summary>
    WtsConnectState? QueryConnectState(int sessionId);

    /// <summary>
    /// Forcibly logs off the given session (<c>WTSLogoffSession</c>). Returns true on success.
    /// </summary>
    /// <param name="sessionId">The session to log off.</param>
    /// <param name="wait">Whether to block until the logoff completes.</param>
    bool LogoffSession(int sessionId, bool wait);
}
