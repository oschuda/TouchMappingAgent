namespace TouchMappingAgent.Service.HealthMonitoring;

/// <summary>
/// Holds the most recently reported "how many physical displays do I see" heartbeat from
/// every interactive WPF client instance, keyed by its own Windows session id.
///
/// WHY PER SESSION: the WPF client's HKLM Run-key autostart launches one instance per
/// interactive logon session — the console AND every RDP session alike, each enforcing a
/// single instance per session, not per machine. A single shared heartbeat slot (the
/// original design) meant sessions silently overwrote each other's last-reported state,
/// which was harmless while only the console session's heartbeat mattered, but would be
/// actively wrong once a per-session verdict is needed
/// (<see cref="ConsoleSessionWatchdog"/>'s SessionUnresponsive detection): a healthy RDP
/// session's fresh heartbeat could mask a hung console's stale one, or vice versa.
///
/// WHY THE SERVICE CANNOT MEASURE THIS ITSELF: a Windows Service runs in Session 0, which has no
/// interactive Window Station. GDI calls such as EnumDisplayDevices return an EMPTY list from
/// there regardless of the console's real state (confirmed empirically elsewhere in this
/// codebase — see GetPendingReapplyRequest's remarks and NamedPipeServer.HandleGetMonitorsAsync).
/// If <see cref="ConsoleSessionWatchdog"/> queried GDI directly it would see "0 displays"
/// permanently, healthy or not, and would force-logoff the operator's session on every single
/// poll. The interactive client in each session — which DOES have a real desktop — reports its
/// own, correct count here instead; this class is just the mailbox, one slot per session.
///
/// Registered as a singleton: one mailbox per service process, written by whichever pipe
/// connection last delivered a "ConsoleDisplayHeartbeat" request for a given session, read by
/// the watchdog's poll loop. A lock (rather than Volatile/Interlocked) guards each entry
/// because the count and its timestamp must always be read and written together — an update
/// that changed the count without also advancing the timestamp (or vice versa) would let the
/// watchdog pair a fresh count with a stale timestamp or the reverse.
/// </summary>
public sealed class ConsoleDisplayHeartbeatState
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (int MonitorCount, DateTimeOffset At)> _bySession = new();

    /// <summary>Records a heartbeat from one session's client: the monitor count it just
    /// enumerated, now.</summary>
    public void Report(int sessionId, int monitorCount, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            _bySession[sessionId] = (monitorCount, utcNow);
        }
    }

    /// <summary>
    /// Returns the given session's last reported monitor count and how long ago it was
    /// reported, or null if no heartbeat has ever been received for that session (e.g. right
    /// after service start, or before that session's client has completed its first poll).
    /// </summary>
    public (int MonitorCount, TimeSpan Age)? TryGetLast(int sessionId, DateTimeOffset utcNow)
    {
        lock (_gate)
        {
            if (!_bySession.TryGetValue(sessionId, out var entry))
                return null;

            return (entry.MonitorCount, utcNow - entry.At);
        }
    }

    /// <summary>
    /// Drops heartbeat entries for sessions that are not in <paramref name="liveSessionIds"/>,
    /// so a session that has logged off entirely does not linger in memory forever. The
    /// watchdog calls this once per poll with the session table it already enumerated for its
    /// own evaluation — no extra WTS call needed here.
    /// </summary>
    public void PruneSessionsNotIn(IReadOnlyCollection<int> liveSessionIds)
    {
        lock (_gate)
        {
            if (_bySession.Count == 0)
                return;

            var stale = _bySession.Keys.Where(id => !liveSessionIds.Contains(id)).ToList();
            foreach (var id in stale)
                _bySession.Remove(id);
        }
    }
}
