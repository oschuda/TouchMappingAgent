using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Service.Native;
using TouchMappingAgent.Service.Services;

namespace TouchMappingAgent.Service.HealthMonitoring;

/// <summary>
/// Overall health verdict for one evaluation pass. See <see cref="ConsoleSessionWatchdog"/>'s
/// remarks for the full detection/escalation chain each value feeds into.
/// </summary>
public enum SessionHealthVerdict
{
    /// <summary>Nothing to do this cycle.</summary>
    Healthy,

    /// <summary>A non-console session's heartbeat is missing/stale. Pure diagnostic state —
    /// never triggers an action on its own. See <see cref="ConsoleSessionWatchdog"/>'s remarks
    /// for why a missing heartbeat alone proves nothing.</summary>
    HeartbeatMissing,

    /// <summary>A non-console session is Active, its heartbeat has been missing/stale for the
    /// full escalation window, AND its own client process is independently confirmed still
    /// present in that session — ruling out "the client simply isn't running". Routes to
    /// <see cref="ConsoleSessionResetService.ResetUnresponsiveSession"/>.</summary>
    SessionUnresponsive,

    /// <summary>The physical console is Active/Disconnected and its live client reports 0
    /// displays. A missing/stale heartbeat alone no longer qualifies (see
    /// <see cref="ConsoleSessionWatchdog"/>'s remarks). Routes to
    /// <see cref="ConsoleSessionResetService.ResetConsoleSession"/>.</summary>
    ConsoleBlackScreen,

    /// <summary>A WTS call did not respond within its time budget
    /// (<see cref="WtsUnresponsiveException"/>). Self-healing, not terminal — see that
    /// exception's remarks. Never routes to a logoff (same blocked path); routes to alerting
    /// and, when a specific session is already known to be the cause, to process termination
    /// via <see cref="SessionProcessService"/> instead.</summary>
    TerminalServicesUnresponsive,
}

/// <summary>
/// Detects and heals two independent classes of hung interactive session:
///
///  1. A hung PHYSICAL CONSOLE (black screen / headless DWM after a power event, extender
///     power-cycle, or graphics driver reset) — the watchdog's original purpose: console
///     Active/Disconnected + a live client reporting 0 displays forces a logoff via
///     <see cref="ConsoleSessionResetService.ResetConsoleSession"/>. A missing/stale heartbeat
///     used to qualify as well; it no longer does, because the client goes quiet for up to 60s
///     while tabcal.exe runs after every login, and the console was being logged off in the
///     middle of exactly that re-application.
///
///  2. A hung RDP (or any other non-console) SESSION whose own client process has stopped
///     answering — added after a real incident where such a session's own logoff/reset
///     commands themselves hung. Detection here is deliberately more conservative than the
///     console path: a missing heartbeat is <see cref="SessionHealthVerdict.HeartbeatMissing"/>
///     ONLY, a purely diagnostic state, because a missing heartbeat is equally consistent with
///     "the client crashed", "the client was never started", "autostart failed", or "the user
///     closed it" — none of which warrant forcing anyone's session closed. It escalates to
///     <see cref="SessionHealthVerdict.SessionUnresponsive"/> (which DOES trigger a logoff)
///     only once the client's own process is independently confirmed STILL PRESENT in that
///     session (via <see cref="SessionProcessService"/>, a termsrv-independent path) AND the
///     combined state persists for the full escalation window. Automatic action is further
///     restricted to sessions reported <c>Active</c> — a <c>Disconnected</c> RDP session is
///     very often deliberately left that way by the operator and is never touched.
///
/// A THIRD condition, <see cref="SessionHealthVerdict.TerminalServicesUnresponsive"/>, can
/// surface from either path whenever a WTS call itself times out
/// (<see cref="TimeBoundedWtsSessionProvider"/>). This is NEVER routed into a logoff attempt —
/// that would just retry the exact same blocked call. Instead: if the watchdog cannot even
/// enumerate sessions, it can only alert (no specific session is known). If a SPECIFIC
/// session's own logoff attempt reveals termsrv is unresponsive FOR THAT SESSION — exactly
/// what happened in the real incident this was built for — the watchdog escalates to
/// terminating that session's user-mode processes directly, bypassing termsrv entirely (see
/// <see cref="SessionProcessService"/>).
///
/// SAFETY GUARDS common to both console and per-session automatic recovery:
///  1. Boot hysteresis (<see cref="BootHysteresis"/>): fully inactive for the first 90s after
///     service start.
///  2. Debounce: the console path uses <see cref="DebounceWindow"/> (15s, unchanged); the
///     per-session path additionally requires the full <see cref="SessionUnresponsiveEscalationWindow"/>
///     (120s) of combined "heartbeat missing + client process present" before acting at all.
///  3. Rate limiting (<see cref="ResetCooldown"/>): at most one action per affected
///     session (or the console) per 15 minutes, tracked independently per session.
///
/// <see cref="ConsoleSessionResetService"/> enforces its own further, independent guarantee:
/// its two methods can never be merged, so the console's original "never touch anything but
/// the console" contract cannot be silently widened later.
/// </summary>
public sealed class ConsoleSessionWatchdog : BackgroundService, IDisposable, IAsyncDisposable
{
    /// <summary>How often the condition is (re-)evaluated.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>The watchdog does nothing at all until this long after service start.</summary>
    internal static readonly TimeSpan BootHysteresis = TimeSpan.FromSeconds(90);

    /// <summary>The console's black-screen condition must hold continuously this long before
    /// a reset fires. Unchanged from the watchdog's original design.</summary>
    internal static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(15);

    /// <summary>Minimum spacing between automatic action attempts against the SAME session
    /// (the console, or one specific non-console session) — tracked independently per session,
    /// so an unresponsive RDP session cannot exhaust or be exhausted by the console's cooldown.</summary>
    internal static readonly TimeSpan ResetCooldown = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A client heartbeat older than this is treated the same as "no heartbeat at all" —
    /// suspicious, since the client polls roughly every 15s (TouchMappingAgent.WPF.Services.
    /// ReapplyAgent); three missed cycles is long enough to rule out ordinary jitter or a
    /// single slow poll.
    /// </summary>
    internal static readonly TimeSpan HeartbeatStaleness = TimeSpan.FromSeconds(45);

    /// <summary>
    /// How long a non-console session must continuously show HeartbeatMissing WITH its client
    /// process still confirmed present before escalating to SessionUnresponsive. Deliberately
    /// longer than the console's DebounceWindow: a missing heartbeat is far more ambiguous
    /// than "0 displays reported by a live client", so the bar to act automatically is higher.
    /// </summary>
    internal static readonly TimeSpan SessionUnresponsiveEscalationWindow = TimeSpan.FromSeconds(120);

    /// <summary>How long "cannot even enumerate sessions" must persist before the (alert-only,
    /// no specific session known) TerminalServicesUnresponsive condition is logged. A single
    /// 10s probe timeout is not yet proof of anything — see WtsUnresponsiveException's remarks.</summary>
    internal static readonly TimeSpan TerminalServicesDebounceWindow = TimeSpan.FromSeconds(60);

    private const string ConsoleWinStationName = "Console";

    private readonly ILogger<ConsoleSessionWatchdog> _logger;
    private readonly IWtsSessionProvider _wts;
    private readonly ConsoleSessionResetService _resetService;
    private readonly ISessionProcessService _processService;
    private readonly ConsoleDisplayHeartbeatState _heartbeat;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _serviceStartedUtc;

    // Console tracking — unchanged in spirit from the watchdog's original, single-session
    // design; only renamed (from _suspectSinceUtc / _lastResetAttemptUtc) now that per-session
    // state exists alongside it.
    private DateTimeOffset? _consoleSuspectSinceUtc;
    private DateTimeOffset? _consoleLastResetAttemptUtc;
    private bool _consoleHeartbeatMissingLogged;

    // Global "cannot even enumerate sessions" tracking — no specific session is known in this
    // case, so there is nothing to key a per-session dictionary entry on.
    private DateTimeOffset? _globalUnresponsiveSinceUtc;
    private DateTimeOffset? _globalUnresponsiveLastAlertUtc;

    // Per-(non-console)-session tracking, keyed by SessionId. Pruned each poll to whatever
    // EnumerateSessions() just reported, so a logged-off session's state does not linger.
    private readonly Dictionary<int, SessionTrackingState> _sessionStates = new();

    private bool _disposed;

    /// <summary>Initializes a new instance of <see cref="ConsoleSessionWatchdog"/>.</summary>
    public ConsoleSessionWatchdog(
        ILogger<ConsoleSessionWatchdog> logger,
        IWtsSessionProvider wts,
        ConsoleSessionResetService resetService,
        ISessionProcessService processService,
        ConsoleDisplayHeartbeatState heartbeat,
        TimeProvider timeProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _wts = wts ?? throw new ArgumentNullException(nameof(wts));
        _resetService = resetService ?? throw new ArgumentNullException(nameof(resetService));
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _serviceStartedUtc = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Hosted-service entry point. Runs for the lifetime of the service.
    /// MVO 2023/1230: MUST NOT throw — an evaluation failure may never take the service down.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[SessionWatchdog] Started (boot hysteresis {BootHysteresisSeconds:F0}s, console debounce " +
            "{DebounceSeconds:F0}s, session escalation window {EscalationSeconds:F0}s, cooldown " +
            "{CooldownMinutes:F0}min).",
            BootHysteresis.TotalSeconds, DebounceWindow.TotalSeconds,
            SessionUnresponsiveEscalationWindow.TotalSeconds, ResetCooldown.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested && !_disposed)
        {
            try
            {
                EvaluateOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SessionWatchdog] Evaluation failed; will retry on next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("[SessionWatchdog] Stopped");
    }

    /// <summary>
    /// One evaluation pass over every session. Exposed internally so tests can drive it
    /// directly against a fake <see cref="TimeProvider"/> instead of waiting on real timers.
    /// </summary>
    internal void EvaluateOnce()
    {
        var now = _timeProvider.GetUtcNow();

        if (now - _serviceStartedUtc < BootHysteresis)
            return;

        IReadOnlyList<WtsSessionInfo> sessions;
        try
        {
            sessions = _wts.EnumerateSessions();
        }
        catch (WtsUnresponsiveException ex)
        {
            EvaluateGlobalUnresponsive(now, ex);
            return;
        }
        catch (Exception ex)
        {
            // Any other failure fails safe — treated as "cannot tell", never as "must act".
            _logger.LogWarning(ex, "[SessionWatchdog] WTSEnumerateSessions failed during evaluation; treating as healthy this cycle.");
            return;
        }

        // Recovered from a prior global-unresponsive episode (if any) — the very next
        // successful enumeration clears it. Not latching; see WtsUnresponsiveException's remarks.
        if (_globalUnresponsiveSinceUtc != null)
        {
            _logger.LogInformation("[SessionWatchdog] Terminal Services responded again; clearing the global-unresponsive condition.");
            _globalUnresponsiveSinceUtc = null;
            _globalUnresponsiveLastAlertUtc = null;
        }

        var liveSessionIds = sessions.Select(s => s.SessionId).ToHashSet();
        _heartbeat.PruneSessionsNotIn(liveSessionIds);
        PruneSessionStates(liveSessionIds);

        EvaluateConsole(now, sessions);

        foreach (var session in sessions)
        {
            if (string.Equals(session.WinStationName, ConsoleWinStationName, StringComparison.OrdinalIgnoreCase))
                continue; // handled by EvaluateConsole above

            EvaluateNonConsoleSession(now, session);
        }
    }

    // =========================================================================================
    // GLOBAL "cannot even enumerate sessions" path — alert only, no specific session known.
    // =========================================================================================

    private void EvaluateGlobalUnresponsive(DateTimeOffset now, WtsUnresponsiveException ex)
    {
        if (_globalUnresponsiveSinceUtc == null)
        {
            _globalUnresponsiveSinceUtc = now;
            _logger.LogWarning(ex, "[SessionWatchdog] WTSEnumerateSessions did not respond; starting {DebounceSeconds:F0}s debounce.",
                TerminalServicesDebounceWindow.TotalSeconds);
            return;
        }

        var duration = now - _globalUnresponsiveSinceUtc.Value;
        if (duration < TerminalServicesDebounceWindow)
            return;

        if (_globalUnresponsiveLastAlertUtc is DateTimeOffset lastAlert && now - lastAlert < TerminalServicesDebounceWindow)
            return; // already alerted this debounce cycle

        _globalUnresponsiveLastAlertUtc = now;

        _logger.LogError(
            "[SessionWatchdog] Terminal Services has not responded to WTSEnumerateSessions for {DurationSeconds:F0}s. " +
            "No specific session can be identified, so no automatic action is possible — this is an alert only. " +
            "This is not necessarily permanent; Terminal Services may still recover on its own.",
            duration.TotalSeconds);

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.TerminalServicesUnresponsive,
            "GLOBAL_ENUMERATION",
            success: false,
            errorMessage: $"WTSEnumerateSessions unresponsive for {duration.TotalSeconds:F0}s");
    }

    // =========================================================================================
    // CONSOLE path — original detection and action, unchanged.
    // =========================================================================================

    private void EvaluateConsole(DateTimeOffset now, IReadOnlyList<WtsSessionInfo> sessions)
    {
        if (!TryDetectConsoleBlackScreen(sessions, now, out var detail))
        {
            if (_consoleSuspectSinceUtc != null)
                _logger.LogInformation("[SessionWatchdog] Console condition cleared; resetting debounce.");

            _consoleSuspectSinceUtc = null;
            return;
        }

        if (_consoleSuspectSinceUtc == null)
        {
            _consoleSuspectSinceUtc = now;
            _logger.LogInformation(
                "[SessionWatchdog] Suspected hanging console session detected ({Detail}); starting {DebounceSeconds:F0}s debounce.",
                detail, DebounceWindow.TotalSeconds);
            return;
        }

        var suspectDuration = now - _consoleSuspectSinceUtc.Value;
        if (suspectDuration < DebounceWindow)
            return;

        if (_consoleLastResetAttemptUtc is DateTimeOffset lastAttempt && now - lastAttempt < ResetCooldown)
        {
            var remaining = ResetCooldown - (now - lastAttempt);
            _logger.LogWarning(
                "[SessionWatchdog] Suspected hanging console session ({Detail}) persists, but a reset was " +
                "already attempted within the last {CooldownMinutes:F0} minutes. Suppressing further attempts " +
                "for {RemainingMinutes:F0} more minute(s) to avoid an endless loop against possibly dead hardware.",
                detail, ResetCooldown.TotalMinutes, remaining.TotalMinutes);

            _consoleSuspectSinceUtc = now;
            return;
        }

        _logger.LogWarning(
            "[SessionWatchdog] Suspected hanging console session ({Detail} for {SuspectSeconds:F0}s). " +
            "Requesting a console session reset...",
            detail, suspectDuration.TotalSeconds);

        _consoleLastResetAttemptUtc = now;
        var outcome = _resetService.ResetConsoleSession(detail);

        if (outcome != ConsoleResetOutcome.Success)
        {
            _logger.LogError(
                "[SessionWatchdog] Console reset attempt did not succeed ({Outcome}). Further automatic attempts are " +
                "suppressed for {CooldownMinutes:F0} minutes.",
                outcome, ResetCooldown.TotalMinutes);
        }

        _consoleSuspectSinceUtc = null;
    }

    /// <summary>
    /// Evaluates the console's black-screen condition: an Active/Disconnected console session
    /// AND a fresh client heartbeat reporting 0 displays. A missing or stale heartbeat is
    /// logged as a diagnostic and never qualifies.
    /// </summary>
    private bool TryDetectConsoleBlackScreen(IReadOnlyList<WtsSessionInfo> sessions, DateTimeOffset now, out string detail)
    {
        detail = string.Empty;

        var consoleSessions = sessions
            .Where(s => string.Equals(s.WinStationName, ConsoleWinStationName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // No console session, or more than one (should never happen) — nothing coherent to
        // act on either way; ResetConsoleSession would refuse the ambiguous case anyway, but
        // there is no reason to even raise suspicion over it.
        if (consoleSessions.Count != 1)
            return false;

        var console = consoleSessions[0];
        if (console.State != WtsConnectState.Active && console.State != WtsConnectState.Disconnected)
            return false;

        var heartbeat = _heartbeat.TryGetLast(console.SessionId, now);

        // A missing or stale heartbeat is NOT evidence of a black screen and never triggers a
        // logoff. Field incident (2026-09-23 .. 2026-10-06, nine forced sign-outs): the client
        // legitimately stops reporting while it waits up to 60s for tabcal.exe after every
        // login, and the console was logged off right in the middle of that re-application —
        // which then restarted on the next login. The same absence is equally consistent with
        // "client not started yet", "client closed" or "client crashed". Only the positive
        // signal — a live client reporting 0 displays — proves the headless-DWM condition.
        if (heartbeat is null || heartbeat.Value.Age > HeartbeatStaleness)
        {
            if (!_consoleHeartbeatMissingLogged)
            {
                _consoleHeartbeatMissingLogged = true;
                _logger.LogInformation(
                    "[SessionWatchdog] Console session {SessionId}: {HeartbeatDetail}. Diagnostic only — a missing " +
                    "heartbeat never triggers a console logoff.",
                    console.SessionId,
                    heartbeat is null
                        ? "no client heartbeat received yet"
                        : $"client heartbeat stale ({heartbeat.Value.Age.TotalSeconds:F0}s old)");
            }

            return false;
        }

        _consoleHeartbeatMissingLogged = false;

        if (heartbeat.Value.MonitorCount != 0)
            return false; // client is alive and reporting a healthy display count

        detail = $"Session {console.SessionId} state={console.State}, client reports 0 displays";
        return true;
    }

    // =========================================================================================
    // NON-CONSOLE (RDP/other) session path — new: HeartbeatMissing -> SessionUnresponsive.
    // =========================================================================================

    private sealed class SessionTrackingState
    {
        public DateTimeOffset? HeartbeatMissingSinceUtc;
        public DateTimeOffset? LastActionAttemptUtc;
        public bool LoggedHeartbeatMissingWithoutClient;
    }

    private void PruneSessionStates(HashSet<int> liveSessionIds)
    {
        if (_sessionStates.Count == 0)
            return;

        var stale = _sessionStates.Keys.Where(id => !liveSessionIds.Contains(id)).ToList();
        foreach (var id in stale)
            _sessionStates.Remove(id);
    }

    private void EvaluateNonConsoleSession(DateTimeOffset now, WtsSessionInfo session)
    {
        // Automatic recovery only ever considers Active sessions. Disconnected is common and
        // often deliberate (operator closed the RDP window without logging off) — a stale
        // heartbeat there proves nothing and is not even tracked, so a Disconnected session
        // can never accumulate escalation time while disconnected.
        if (session.State != WtsConnectState.Active)
        {
            _sessionStates.Remove(session.SessionId);
            return;
        }

        var heartbeat = _heartbeat.TryGetLast(session.SessionId, now);
        var heartbeatOk = heartbeat is { } hb && hb.Age <= HeartbeatStaleness;

        if (heartbeatOk)
        {
            if (_sessionStates.Remove(session.SessionId, out var cleared) && cleared.HeartbeatMissingSinceUtc != null)
                _logger.LogInformation("[SessionWatchdog] Session {SessionId} heartbeat recovered.", session.SessionId);
            return;
        }

        if (!_sessionStates.TryGetValue(session.SessionId, out var state))
        {
            state = new SessionTrackingState();
            _sessionStates[session.SessionId] = state;
        }

        if (state.HeartbeatMissingSinceUtc == null)
        {
            state.HeartbeatMissingSinceUtc = now;
            state.LoggedHeartbeatMissingWithoutClient = false;
            _logger.LogInformation(
                "[SessionWatchdog] Session {SessionId} heartbeat missing/stale; diagnosing (this alone does not " +
                "trigger any action).",
                session.SessionId);
        }

        // Plausibilisation: a missing heartbeat is equally consistent with the client having
        // crashed, never started, been closed by the user, or failing autostart — none of
        // which warrant forcing the session closed. Only escalate if the client's own process
        // is independently confirmed still present.
        bool clientProcessPresent;
        try
        {
            clientProcessPresent = _processService
                .ListUserProcesses(session.SessionId)
                .Any(p => string.Equals(p.ProcessName, "TouchMappingAgent.WPF", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[SessionWatchdog] Could not check for the client process in session {SessionId}; not escalating this cycle.", session.SessionId);
            return;
        }

        if (!clientProcessPresent)
        {
            if (!state.LoggedHeartbeatMissingWithoutClient)
            {
                state.LoggedHeartbeatMissingWithoutClient = true;
                _logger.LogInformation(
                    "[SessionWatchdog] Client not running in session {SessionId} — heartbeat absence is expected, not a hang.",
                    session.SessionId);
            }
            return; // stays HeartbeatMissing; never escalates without the client actually running
        }

        var missingDuration = now - state.HeartbeatMissingSinceUtc.Value;
        if (missingDuration < SessionUnresponsiveEscalationWindow)
            return; // still within the diagnostic window

        if (state.LastActionAttemptUtc is DateTimeOffset lastAttempt && now - lastAttempt < ResetCooldown)
        {
            var remaining = ResetCooldown - (now - lastAttempt);
            _logger.LogWarning(
                "[SessionWatchdog] Session {SessionId} unresponsive (heartbeat missing for {MissingSeconds:F0}s, " +
                "client process still present), but an action was already attempted within the last " +
                "{CooldownMinutes:F0} minutes. Suppressing further attempts for {RemainingMinutes:F0} more minute(s).",
                session.SessionId, missingDuration.TotalSeconds, ResetCooldown.TotalMinutes, remaining.TotalMinutes);

            // Restart the escalation window so this warning repeats at most once per cooldown
            // cycle rather than on every single poll while suppressed.
            state.HeartbeatMissingSinceUtc = now;
            return;
        }

        var reason = $"heartbeat missing for {missingDuration.TotalSeconds:F0}s with client process present (SessionUnresponsive)";
        _logger.LogWarning(
            "[SessionWatchdog] Session {SessionId} classified SessionUnresponsive ({Reason}). Attempting logoff...",
            session.SessionId, reason);

        state.LastActionAttemptUtc = now;
        var outcome = _resetService.ResetUnresponsiveSession(session.SessionId, reason);

        switch (outcome)
        {
            case ConsoleResetOutcome.Success:
                _logger.LogInformation("[SessionWatchdog] Session {SessionId} logoff succeeded.", session.SessionId);
                break;

            case ConsoleResetOutcome.TerminalServicesUnresponsive:
                // This is exactly the real-incident case: the logoff attempt itself reveals
                // termsrv is wedged FOR THIS SESSION. Retrying WTSLogoffSession would just
                // block on the same path again — escalate to the termsrv-independent process
                // termination path instead. Never attempted for the console (ResetConsoleSession
                // never returns this into this code path; see the architecture invariant above
                // ResetUnresponsiveSession).
                _logger.LogError(
                    "[SessionWatchdog] Terminal Services did not respond while resetting session {SessionId}; " +
                    "escalating to process termination for this session (bypassing termsrv).",
                    session.SessionId);
                try
                {
                    _processService.TerminateUserProcesses(session.SessionId, reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SessionWatchdog] Process termination escalation failed for session {SessionId}.", session.SessionId);
                }
                break;

            default:
                _logger.LogError(
                    "[SessionWatchdog] Session {SessionId} reset attempt did not succeed ({Outcome}). Further " +
                    "automatic attempts are suppressed for {CooldownMinutes:F0} minutes.",
                    session.SessionId, outcome, ResetCooldown.TotalMinutes);
                break;
        }

        // Start a fresh diagnostic window regardless of outcome — ResetCooldown (tracked via
        // LastActionAttemptUtc) is what actually prevents a runaway retry loop.
        state.HeartbeatMissingSinceUtc = null;
    }

    /// <summary>Disposes of the watchdog gracefully.</summary>
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Synchronous disposal, required by the DI container's synchronous disposal path (see
    /// <see cref="TouchMappingAgent.Service.Hardware.ResilientHardwareWatcher"/>'s remarks for
    /// why both IDisposable and IAsyncDisposable are implemented here).
    /// </summary>
    public override void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
