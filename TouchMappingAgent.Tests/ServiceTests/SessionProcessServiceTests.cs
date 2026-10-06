using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for <see cref="SessionProcessService"/> — the highest-blast-radius code in the
/// whole watchdog feature, since it forcibly terminates processes. The safety GATE
/// (<see cref="SessionProcessService.EvaluateSkipReason"/>) is tested directly against
/// hand-built <see cref="SessionProcessInfo"/> records: no test here ever calls
/// <see cref="Process.Kill()"/> against a real process — doing so against whatever happens to
/// be running in the test session would be exactly the kind of accident this class exists to
/// prevent, not something to risk in an automated test run.
///
/// <see cref="SessionProcessService.ListUserProcesses"/> IS exercised against the real, live
/// process table (it is read-only and has no side effects), to prove the SessionId filter and
/// the enumeration path actually work end-to-end.
/// </summary>
public class SessionProcessServiceTests
{
    private readonly SessionProcessService _service = new(NullLogger<SessionProcessService>.Instance);

    private static SessionProcessInfo Candidate(
        int pid = 1234,
        string name = "application",
        int sessionId = 2,
        string? userSid = "S-1-5-21-1-2-3-1001",
        bool critical = false) =>
        new(pid, name, sessionId, "DOMAIN\\user", userSid, "Medium", critical);

    [Theory]
    [InlineData("csrss")]
    [InlineData("winlogon")]
    [InlineData("wininit")]
    [InlineData("lsass")]
    [InlineData("services")]
    [InlineData("smss")]
    [InlineData("dwm")]
    [InlineData("fontdrvhost")]
    [InlineData("sihost")]
    [InlineData("system")]
    [InlineData("idle")]
    [InlineData("registry")]
    [InlineData("memcompression")]
    public void NeverEligible_WhenFlaggedCriticalOrProtected(string processName)
    {
        // IsCriticalOrProtected is set by TryDescribe (name blocklist match, failed handle
        // open, or the real BreakOnTermination flag) — here it is asserted directly, so this
        // test does not depend on which of those three signals fired.
        var info = Candidate(name: processName, critical: true);

        var reason = _service.EvaluateSkipReason(info);

        Assert.NotNull(reason);
    }

    [Fact]
    public void NeverEligible_WhenIdentityCouldNotBeDetermined()
    {
        // UserSid == null means "could not verify" — must be treated as unsafe, never as
        // "assume it's the interactive user and proceed".
        var info = Candidate(userSid: null);

        var reason = _service.EvaluateSkipReason(info);

        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("S-1-5-18")] // LocalSystem
    [InlineData("S-1-5-19")] // LocalService
    [InlineData("S-1-5-20")] // NetworkService
    public void NeverEligible_WhenRunningUnderASystemServiceAccount(string systemSid)
    {
        var info = Candidate(userSid: systemSid);

        var reason = _service.EvaluateSkipReason(info);

        Assert.NotNull(reason);
    }

    [Fact]
    public void NeverEligible_WhenItIsThisServicesOwnProcess()
    {
        var info = Candidate(pid: Environment.ProcessId, critical: false, userSid: "S-1-5-21-1-2-3-1001");

        var reason = _service.EvaluateSkipReason(info);

        Assert.NotNull(reason);
    }

    [Fact]
    public void Eligible_WhenAnOrdinaryUserApplicationPassesEveryGate()
    {
        // Deliberately NOT this process's own pid, not critical, not a system SID.
        var info = Candidate(pid: Environment.ProcessId + 1, name: "application", critical: false);

        var reason = _service.EvaluateSkipReason(info);

        Assert.Null(reason);
    }

    [Fact]
    public void Eligible_TheClientsOwnWpfProcessIsAnOrdinaryCandidate()
    {
        // The plan is explicit that the client's own WPF process in the unresponsive session
        // IS a regular kill candidate — its absent heartbeat is the very signal that got the
        // watchdog here. Only THIS service's own process (the caller) is exempt.
        var info = Candidate(pid: Environment.ProcessId + 1, name: "TouchMappingAgent.WPF", critical: false);

        var reason = _service.EvaluateSkipReason(info);

        Assert.Null(reason);
    }

    [Fact]
    public void ListUserProcesses_FiltersByTheGivenSessionId_AndIncludesTheCallingProcess()
    {
        var ownSessionId = Process.GetCurrentProcess().SessionId;

        var processes = _service.ListUserProcesses(ownSessionId);

        Assert.All(processes, p => Assert.Equal(ownSessionId, p.SessionId));
        Assert.Contains(processes, p => p.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void ListUserProcesses_ForANonExistentSession_ReturnsEmpty()
    {
        var processes = _service.ListUserProcesses(sessionId: 999_999);

        Assert.Empty(processes);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new SessionProcessService(null!));
    }
}
