using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Service.Native;

namespace TouchMappingAgent.Service.Services;

/// <summary>Result of a <see cref="ConsoleSessionResetService.ResetConsoleSession"/> attempt.</summary>
public enum ConsoleResetOutcome
{
    /// <summary>WTSLogoffSession was called against the console session and reported success.</summary>
    Success,

    /// <summary>WTSLogoffSession was called but reported failure (see the Win32 error in the log).</summary>
    LogoffCallFailed,

    /// <summary>No session with WinStationName "Console" exists — nobody to reset.</summary>
    NoConsoleSession,

    /// <summary>More than one session reports WinStationName "Console". Never happens on a
    /// normal single-console machine; refusing to guess rather than resetting the wrong one.</summary>
    AmbiguousConsoleSession,

    /// <summary>The session named "Console" failed a sanity check (e.g. SessionId &lt;= 0) and
    /// was refused as a safety measure.</summary>
    SafetyCheckFailed,

    /// <summary>By the time of the final pre-reset re-check, the session was no longer in a
    /// state ("Active" or "Disconnected") that warrants resetting — e.g. the operator logged
    /// back in between detection and action. Left alone.</summary>
    NoLongerEligible,

    /// <summary>A WTS call (enumerate, query, or the logoff itself) did not respond within its
    /// time budget — see <see cref="TouchMappingAgent.Service.Native.WtsUnresponsiveException"/>'s
    /// remarks. This is NOT proof the session or Terminal Services is permanently dead, and it
    /// is NOT the same as <see cref="LogoffCallFailed"/>: that means termsrv answered "no";
    /// this means termsrv did not answer at all within budget. Callers must never retry
    /// WTSLogoffSession on this outcome — it is the same blocked path that just timed out —
    /// and should route to an alternative recovery path instead (process termination via the
    /// termsrv-independent <see cref="SessionProcessService"/>, for the caller that has one).</summary>
    TerminalServicesUnresponsive,
}

/// <summary>
/// Wraps <see cref="IWtsSessionProvider"/> with the safety guarantees required before this
/// service is ever allowed to call <c>WTSLogoffSession</c>: it must be unmistakably the physical
/// console (never an RDP session), and its state must be re-confirmed immediately before the
/// call. Used both by <see cref="TouchMappingAgent.Service.HealthMonitoring.ConsoleSessionWatchdog"/>
/// (automatic) and the "ForceConsoleSessionReset" IPC command (manual operator override) — both
/// paths funnel through the exact same guarded method.
/// </summary>
public sealed class ConsoleSessionResetService
{
    private const string ConsoleWinStationName = "Console";

    private readonly IWtsSessionProvider _wts;
    private readonly ILogger<ConsoleSessionResetService> _logger;

    /// <summary>Initializes a new instance of <see cref="ConsoleSessionResetService"/>.</summary>
    public ConsoleSessionResetService(IWtsSessionProvider wts, ILogger<ConsoleSessionResetService> logger)
    {
        _wts = wts ?? throw new ArgumentNullException(nameof(wts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Locates and resets ONLY the physical console session. Never touches a session whose
    /// WinStationName is not exactly "Console" (in particular, never an "RDP-Tcp#n" session) —
    /// this is the one invariant every caller depends on, automatic or manual.
    /// </summary>
    /// <param name="reason">Short, human-readable trigger description for the audit log
    /// (e.g. "0 displays detected for 15s" or "manual operator override").</param>
    public ConsoleResetOutcome ResetConsoleSession(string reason)
    {
        IReadOnlyList<WtsSessionInfo> sessions;
        try
        {
            sessions = _wts.EnumerateSessions();
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] WTSEnumerateSessions did not respond while resetting the console session.");
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SessionWatchdog] WTSEnumerateSessions failed; cannot reset console session.");
            return ConsoleResetOutcome.NoConsoleSession;
        }

        var consoleSessions = sessions
            .Where(s => string.Equals(s.WinStationName, ConsoleWinStationName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (consoleSessions.Count == 0)
        {
            _logger.LogWarning("[SessionWatchdog] No session named \"Console\" found; nothing to reset.");
            return ConsoleResetOutcome.NoConsoleSession;
        }

        if (consoleSessions.Count > 1)
        {
            _logger.LogError(
                "[SessionWatchdog] {Count} sessions report WinStationName=\"Console\" simultaneously; " +
                "refusing to guess which one to reset (session ids: {SessionIds}).",
                consoleSessions.Count, string.Join(", ", consoleSessions.Select(s => s.SessionId)));
            return ConsoleResetOutcome.AmbiguousConsoleSession;
        }

        var console = consoleSessions[0];

        // Defense in depth: SessionId 0 is the Services session and must never be targeted.
        // WTSEnumerateSessions should never label it "Console", but this guard makes the
        // invariant explicit rather than implicit in the enumeration's honesty.
        if (console.SessionId <= 0)
        {
            _logger.LogError(
                "[SessionWatchdog] Session reported as \"Console\" has implausible SessionId {SessionId}; refusing to reset.",
                console.SessionId);
            return ConsoleResetOutcome.SafetyCheckFailed;
        }

        // Re-verify immediately before acting: the caller's own detection may be seconds old,
        // and the operator could have logged back in (or out) in the meantime.
        WtsConnectState currentState;
        try
        {
            currentState = _wts.QueryConnectState(console.SessionId) ?? console.State;
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] QueryConnectState did not respond for console session {SessionId}.", console.SessionId);
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }

        if (currentState != WtsConnectState.Active && currentState != WtsConnectState.Disconnected)
        {
            _logger.LogInformation(
                "[SessionWatchdog] Console session {SessionId} is now {State}; no longer eligible for reset, skipping.",
                console.SessionId, currentState);
            return ConsoleResetOutcome.NoLongerEligible;
        }

        _logger.LogWarning(
            "[SessionWatchdog] Suspected hanging console session ({Reason}). Executing WTSLogoffSession on " +
            "session {SessionId} (state: {State})...",
            reason, console.SessionId, currentState);

        bool success;
        try
        {
            success = _wts.LogoffSession(console.SessionId, wait: false);
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] WTSLogoffSession did not respond for console session {SessionId}.", console.SessionId);
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.TerminalServicesUnresponsive, $"SESSION_{console.SessionId}", success: false,
                errorMessage: "WTSLogoffSession did not respond within budget");
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SessionWatchdog] WTSLogoffSession threw for session {SessionId}.", console.SessionId);
            success = false;
        }

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.ConsoleSessionReset,
            $"SESSION_{console.SessionId}",
            success,
            success ? null : "WTSLogoffSession failed");

        if (!success)
        {
            _logger.LogError("[SessionWatchdog] WTSLogoffSession reported failure for session {SessionId}.", console.SessionId);
            return ConsoleResetOutcome.LogoffCallFailed;
        }

        _logger.LogInformation("[SessionWatchdog] Console session {SessionId} logoff requested successfully.", console.SessionId);
        return ConsoleResetOutcome.Success;
    }

    /// <summary>
    /// Locates and resets ONE specific, previously-classified-unresponsive session by id —
    /// the RDP-session counterpart to <see cref="ResetConsoleSession"/> after the RDP
    /// hard-exclusion was deliberately relaxed (see this method's remarks).
    ///
    /// ARCHITECTURE INVARIANT — the two methods stay deliberately separate and must never be
    /// merged or have their guards shared:
    /// <code>
    /// ResetConsoleSession()        -> may touch ONLY the physical console session.
    /// ResetUnresponsiveSession()   -> may touch ONLY a session the CALLER has already
    ///                                  classified SessionUnresponsive (Active, heartbeat
    ///                                  missing/stale for the full escalation window, AND its
    ///                                  own client process independently confirmed still
    ///                                  present in that session) — never a session that is
    ///                                  merely HeartbeatMissing, and never Disconnected.
    /// </code>
    /// This split exists so nobody is tempted to fold the console's original safety barrier
    /// away later "for convenience" — the two methods represent two independently-reasoned-
    /// about sets of guarantees, and collapsing them would silently widen both.
    /// </summary>
    /// <param name="sessionId">The session to reset. Must not be the console — use
    /// <see cref="ResetConsoleSession"/> for that.</param>
    /// <param name="reason">Short, human-readable trigger description for the audit log.</param>
    public ConsoleResetOutcome ResetUnresponsiveSession(int sessionId, string reason)
    {
        if (sessionId <= 0)
        {
            _logger.LogError("[SessionWatchdog] Refusing to reset session {SessionId}: implausible session id.", sessionId);
            return ConsoleResetOutcome.SafetyCheckFailed;
        }

        IReadOnlyList<WtsSessionInfo> sessions;
        try
        {
            sessions = _wts.EnumerateSessions();
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] WTSEnumerateSessions did not respond while resetting session {SessionId}.", sessionId);
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SessionWatchdog] WTSEnumerateSessions failed; cannot reset session {SessionId}.", sessionId);
            return ConsoleResetOutcome.NoConsoleSession;
        }

        var matches = sessions.Where(s => s.SessionId == sessionId).ToList();
        if (matches.Count == 0)
        {
            _logger.LogWarning("[SessionWatchdog] Session {SessionId} no longer exists; nothing to reset.", sessionId);
            return ConsoleResetOutcome.NoConsoleSession;
        }

        var target = matches[0];

        // Hard exclusion in the OTHER direction from ResetConsoleSession: this path must
        // never be used against the console either. If the caller somehow passed the
        // console's own session id, refuse rather than silently doing the same thing
        // ResetConsoleSession already exists for.
        if (string.Equals(target.WinStationName, ConsoleWinStationName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "[SessionWatchdog] Refusing: session {SessionId} is the physical console; use ResetConsoleSession instead.",
                sessionId);
            return ConsoleResetOutcome.SafetyCheckFailed;
        }

        WtsConnectState currentState;
        try
        {
            currentState = _wts.QueryConnectState(sessionId) ?? target.State;
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] QueryConnectState did not respond for session {SessionId}.", sessionId);
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }

        // Automatic recovery applies ONLY to Active. A Disconnected RDP session is very often
        // deliberately left that way by the operator (closing the RDP window without logging
        // off), and a stale/missing heartbeat there is no evidence anything is actually
        // broken — see ConsoleSessionWatchdog's SessionUnresponsive remarks for why the caller
        // must never even offer a Disconnected session to this method as the reason.
        if (currentState != WtsConnectState.Active)
        {
            _logger.LogInformation(
                "[SessionWatchdog] Session {SessionId} is now {State}, not Active; no longer eligible for reset, skipping.",
                sessionId, currentState);
            return ConsoleResetOutcome.NoLongerEligible;
        }

        _logger.LogWarning(
            "[SessionWatchdog] Session {SessionId} unresponsive ({Reason}). Executing WTSLogoffSession...",
            sessionId, reason);

        bool success;
        try
        {
            success = _wts.LogoffSession(sessionId, wait: false);
        }
        catch (WtsUnresponsiveException ex)
        {
            _logger.LogWarning(ex, "[SessionWatchdog] WTSLogoffSession did not respond for session {SessionId}.", sessionId);
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.TerminalServicesUnresponsive, $"SESSION_{sessionId}", success: false,
                errorMessage: "WTSLogoffSession did not respond within budget");
            return ConsoleResetOutcome.TerminalServicesUnresponsive;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SessionWatchdog] WTSLogoffSession threw for session {SessionId}.", sessionId);
            success = false;
        }

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.SessionUnresponsiveReset,
            $"SESSION_{sessionId}",
            success,
            success ? null : "WTSLogoffSession failed");

        if (!success)
        {
            _logger.LogError("[SessionWatchdog] WTSLogoffSession reported failure for session {SessionId}.", sessionId);
            return ConsoleResetOutcome.LogoffCallFailed;
        }

        _logger.LogInformation("[SessionWatchdog] Session {SessionId} logoff requested successfully.", sessionId);
        return ConsoleResetOutcome.Success;
    }
}
