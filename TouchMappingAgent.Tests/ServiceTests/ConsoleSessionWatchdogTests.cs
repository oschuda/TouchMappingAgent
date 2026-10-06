using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Service.HealthMonitoring;
using TouchMappingAgent.Service.Native;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for <see cref="ConsoleSessionWatchdog"/>'s detection state machine: boot
/// hysteresis, debounce, per-session rate limiting, the console black-screen path (unchanged
/// from the watchdog's original design), the new HeartbeatMissing -&gt; SessionUnresponsive
/// escalation for non-console sessions, and TerminalServicesUnresponsive routing. Everything
/// is driven against a fake clock and mocked <see cref="IWtsSessionProvider"/> /
/// <see cref="ISessionProcessService"/> — no real timers, no real WTS or process calls.
/// </summary>
public class ConsoleSessionWatchdogTests
{
    private const int ConsoleSessionId = 1;
    private const int RdpSessionId = 2;
    private const string ClientProcessName = "TouchMappingAgent.WPF";

    private static readonly WtsSessionInfo HealthyConsole =
        new(ConsoleSessionId, "Console", WtsConnectState.Active);
    private static readonly WtsSessionInfo RdpSession =
        new(RdpSessionId, "RDP-Tcp#0", WtsConnectState.Active);

    private readonly ManualTimeProvider _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    private readonly Mock<IWtsSessionProvider> _wts = new();
    private readonly Mock<ISessionProcessService> _processService = new();
    private readonly ConsoleDisplayHeartbeatState _heartbeat = new();

    public ConsoleSessionWatchdogTests()
    {
        // Default: no client process anywhere, unless a test says otherwise. This matters for
        // the RDP-session escalation tests, where "client process present" is a hard
        // precondition for ever escalating past HeartbeatMissing.
        _processService.Setup(p => p.ListUserProcesses(It.IsAny<int>()))
            .Returns(Array.Empty<SessionProcessInfo>());
    }

    private ConsoleSessionWatchdog CreateWatchdog()
    {
        _wts.Setup(w => w.QueryConnectState(It.IsAny<int>())).Returns(WtsConnectState.Active);

        var resetService = new ConsoleSessionResetService(_wts.Object, NullLogger<ConsoleSessionResetService>.Instance);

        return new ConsoleSessionWatchdog(
            NullLogger<ConsoleSessionWatchdog>.Instance,
            _wts.Object,
            resetService,
            _processService.Object,
            _heartbeat,
            _clock);
    }

    /// <summary>Sets up a suspect session table: only the console, reporting no monitors.</summary>
    private void ArrangeHangingConsole()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
    }

    /// <summary>Advances the fake clock and, unless suppressed, re-reports a matching heartbeat
    /// so "stale heartbeat" does not accidentally start masquerading as the condition under
    /// test once elapsed time exceeds <see cref="ConsoleSessionWatchdog.HeartbeatStaleness"/>.</summary>
    private void AdvanceAndRefreshHeartbeat(TimeSpan by, int sessionId, int monitorCount)
    {
        _clock.Advance(by);
        _heartbeat.Report(sessionId, monitorCount, _clock.GetUtcNow());
    }

    private void SetClientPresent(int sessionId, bool present)
    {
        _processService.Setup(p => p.ListUserProcesses(sessionId))
            .Returns(present
                ? new[] { new SessionProcessInfo(1234, ClientProcessName, sessionId, "user", "S-1-5-21-1", "Medium", false) }
                : Array.Empty<SessionProcessInfo>());
    }

    // =========================================================================================
    // CONSOLE path — unchanged behaviour, signatures updated for the per-session heartbeat API.
    // =========================================================================================

    [Fact]
    public void BootHysteresis_SuppressesResetEvenWithFullySuspectCondition()
    {
        var watchdog = CreateWatchdog();
        ArrangeHangingConsole();

        for (int i = 0; i < 8; i++)
        {
            AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(10), ConsoleSessionId, monitorCount: 0);
            watchdog.EvaluateOnce();
        }

        Assert.True(_clock.GetUtcNow() - DateTimeOffset.Parse("2026-01-01T00:00:00Z") < ConsoleSessionWatchdog.BootHysteresis);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void AfterBootHysteresis_ResetDoesNotFireBeforeDebounceWindowElapses()
    {
        var watchdog = CreateWatchdog();
        ArrangeHangingConsole();

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce();

        AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(10), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void AfterDebounceWindowElapses_ResetFiresExactlyOnceOnTheConsoleSession()
    {
        var watchdog = CreateWatchdog();
        ArrangeHangingConsole();

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce();

        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Once);
    }

    [Fact]
    public void RateLimiter_SuppressesASecondResetWithin15MinutesEvenIfConditionPersists()
    {
        var watchdog = CreateWatchdog();
        ArrangeHangingConsole();

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce();
        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Once);

        for (int i = 0; i < 5; i++)
        {
            AdvanceAndRefreshHeartbeat(TimeSpan.FromMinutes(2), ConsoleSessionId, monitorCount: 0);
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public void RateLimiter_AllowsASecondResetOnceCooldownElapses()
    {
        var watchdog = CreateWatchdog();
        ArrangeHangingConsole();

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce();
        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Once);

        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.ResetCooldown + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();
        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Exactly(2));
    }

    [Fact]
    public void HealthyHeartbeat_NeverTriggersAReset()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 10; i++)
        {
            AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(20), ConsoleSessionId, monitorCount: 2);
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void NoConsoleSessionPresent_NeverTriggersAConsoleReset()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 5; i++)
        {
            AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(20), ConsoleSessionId, monitorCount: 0);
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void StaleHeartbeat_NeverLogsOffTheConsole()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 2, _clock.GetUtcNow());
        watchdog.EvaluateOnce();

        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void NoHeartbeatEverReceived_NeverLogsOffTheConsole()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void HeartbeatGapWhileTabcalRunsAfterLogin_DoesNotLogOffTheConsole()
    {
        // Field incident: healthy heartbeat right after login, then a 60s tabcal.exe run plus
        // the 15s poll interval without any report — the old logic logged the console off at
        // ~63s of staleness, mid-re-application.
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 2, _clock.GetUtcNow());

        for (int cycle = 0; cycle < 4; cycle++)
        {
            for (int i = 0; i < 15; i++)
            {
                _clock.Advance(ConsoleSessionWatchdog.PollInterval);
                watchdog.EvaluateOnce();
            }

            _heartbeat.Report(ConsoleSessionId, 2, _clock.GetUtcNow());
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void ZeroDisplaysAfterAStaleHeartbeat_StillLogsOffTheConsole()
    {
        // The black-screen detection itself must survive the change: once the client is
        // reporting again and says 0 displays, the normal debounce applies.
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce(); // no heartbeat yet — diagnostic only

        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce();
        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Once);
    }

    [Fact]
    public void AmbiguousMultipleConsoleSessions_NeverTriggersAReset()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions())
            .Returns(new[] { HealthyConsole, HealthyConsole with { SessionId = 3 } });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 5; i++)
        {
            AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(20), ConsoleSessionId, monitorCount: 0);
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void WtsGenericEnumerationFailure_FailsSafeAndNeverTriggersAReset()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Throws(new InvalidOperationException("WTS unavailable"));

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            var exception = Record.Exception(() => watchdog.EvaluateOnce());
            Assert.Null(exception); // MVO 2023/1230: an evaluation failure must never throw/crash the loop
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    // =========================================================================================
    // NON-CONSOLE session path — new: HeartbeatMissing plausibilisation and escalation.
    // =========================================================================================

    [Fact]
    public void HeartbeatMissingWithoutClientProcess_NeverEscalates()
    {
        // The client simply isn't running in this session (crashed, never started, closed by
        // the user, ...) — heartbeat absence here is expected, not proof of a hang.
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });
        SetClientPresent(RdpSessionId, present: false);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        // Advance well past even the extended escalation window — still no client, still no action.
        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void HeartbeatMissingWithClientProcessPresent_DoesNotEscalateBeforeEscalationWindow()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce(); // observes HeartbeatMissing, starts the escalation window

        _clock.Advance(ConsoleSessionWatchdog.SessionUnresponsiveEscalationWindow - TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void HeartbeatMissingWithClientProcessPresent_EscalatesToLogoffAfterEscalationWindow()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();

        _clock.Advance(ConsoleSessionWatchdog.SessionUnresponsiveEscalationWindow + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);
    }

    [Fact]
    public void DisconnectedRdpSession_IsNeverAutomaticallyLoggedOff()
    {
        // A Disconnected session is often deliberate (operator closed the RDP window without
        // logging off). Even with the client process confirmed present, it must never be
        // touched — only Active sessions are eligible for automatic recovery.
        var watchdog = CreateWatchdog();
        var disconnected = RdpSession with { State = WtsConnectState.Disconnected };
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { disconnected });
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(30));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void PerSessionRateLimit_IsIndependentOfTheConsolesCooldown()
    {
        // The console resets once; the RDP session's own escalation must not be blocked by
        // the console's cooldown, and vice versa — they are tracked independently.
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole, RdpSession });
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        _heartbeat.Report(ConsoleSessionId, 0, _clock.GetUtcNow());
        watchdog.EvaluateOnce(); // console debounce starts; RDP escalation window starts

        AdvanceAndRefreshHeartbeat(ConsoleSessionWatchdog.DebounceWindow + TimeSpan.FromSeconds(1), ConsoleSessionId, monitorCount: 0);
        watchdog.EvaluateOnce(); // console resets (15s debounce satisfied)

        _wts.Verify(w => w.LogoffSession(ConsoleSessionId, false), Times.Once);
        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Never); // 120s window not yet elapsed

        _clock.Advance(ConsoleSessionWatchdog.SessionUnresponsiveEscalationWindow);
        watchdog.EvaluateOnce(); // RDP session's own, independent window now satisfied

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);
    }

    [Fact]
    public void RdpSessionRateLimiter_SuppressesASecondAttemptWithin15Minutes()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();
        _clock.Advance(ConsoleSessionWatchdog.SessionUnresponsiveEscalationWindow + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);

        for (int i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);
    }

    // =========================================================================================
    // TerminalServicesUnresponsive routing.
    // =========================================================================================

    [Fact]
    public void GlobalEnumerationTimeout_NeverAttemptsAnyLogoff()
    {
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions())
            .Throws(new WtsUnresponsiveException("EnumerateSessions did not respond within 10s."));

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));

        for (int i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(20));
            var exception = Record.Exception(() => watchdog.EvaluateOnce());
            Assert.Null(exception);
        }

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
        _processService.Verify(p => p.TerminateUserProcesses(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void GlobalEnumerationTimeout_IsNotLatching_RecoversOnceTermsrvResponds()
    {
        // WtsUnresponsiveException's contract: "did not respond within budget" is not
        // "permanently dead" — the very next successful call must clear the condition.
        var watchdog = CreateWatchdog();
        _wts.SetupSequence(w => w.EnumerateSessions())
            .Throws(new WtsUnresponsiveException("timeout"))
            .Returns(new[] { HealthyConsole });

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce(); // times out

        _heartbeat.Report(ConsoleSessionId, 2, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromSeconds(5));
        watchdog.EvaluateOnce(); // recovers — healthy console, no lingering "unresponsive" state

        // A further healthy poll must not suddenly fire a stale/leftover condition.
        AdvanceAndRefreshHeartbeat(TimeSpan.FromSeconds(20), ConsoleSessionId, monitorCount: 2);
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { HealthyConsole }); // stop the sequence, stay healthy
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void SessionSpecificLogoffTimeout_EscalatesToProcessTerminationNotToRetryingLogoff()
    {
        // The real-incident case: the session is correctly classified SessionUnresponsive
        // (Active, heartbeat missing, client process present), but the actual LogoffSession
        // call reveals Terminal Services is wedged FOR THIS SESSION specifically.
        var watchdog = CreateWatchdog();
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[] { RdpSession });
        _wts.Setup(w => w.QueryConnectState(RdpSessionId)).Returns(WtsConnectState.Active);
        _wts.Setup(w => w.LogoffSession(RdpSessionId, false))
            .Throws(new WtsUnresponsiveException("LogoffSession did not respond within 10s."));
        SetClientPresent(RdpSessionId, present: true);

        _clock.Advance(ConsoleSessionWatchdog.BootHysteresis + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();
        _clock.Advance(ConsoleSessionWatchdog.SessionUnresponsiveEscalationWindow + TimeSpan.FromSeconds(1));
        watchdog.EvaluateOnce();

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);
        _processService.Verify(p => p.TerminateUserProcesses(RdpSessionId, It.IsAny<string>()), Times.Once);

        // Subsequent polls, still within the per-session cooldown, must not retry the same
        // blocked LogoffSession call again — the rate limiter covers this escalation path too.
        for (int i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(2));
            watchdog.EvaluateOnce();
        }

        _wts.Verify(w => w.LogoffSession(RdpSessionId, false), Times.Once);
    }

    /// <summary>
    /// Minimal <see cref="TimeProvider"/> fake exposing a settable, advanceable UTC clock.
    /// Only <see cref="GetUtcNow"/> is overridden — the watchdog's tested surface
    /// (<see cref="ConsoleSessionWatchdog.EvaluateOnce"/>) never calls CreateTimer/GetTimestamp,
    /// so the base implementations of those are never exercised here.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
