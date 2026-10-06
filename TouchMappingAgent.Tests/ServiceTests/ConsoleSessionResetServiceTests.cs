using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Service.Native;
using TouchMappingAgent.Service.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for <see cref="ConsoleSessionResetService"/>'s safety invariants. This is the
/// single chokepoint both the automatic watchdog and the manual "ForceConsoleSessionReset" IPC
/// override funnel through, so its one job — never touch anything but the physical console
/// session — is tested directly and exhaustively here, independent of the watchdog's own
/// detection logic.
/// </summary>
public class ConsoleSessionResetServiceTests
{
    private readonly Mock<IWtsSessionProvider> _wts = new();

    private ConsoleSessionResetService CreateService() =>
        new(_wts.Object, NullLogger<ConsoleSessionResetService>.Instance);

    [Fact]
    public void ResetsTheConsoleSession_WhenExactlyOneExistsAndIsActive()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Active)
        });
        _wts.Setup(w => w.QueryConnectState(1)).Returns(WtsConnectState.Active);
        _wts.Setup(w => w.LogoffSession(1, false)).Returns(true);

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.Success, outcome);
        _wts.Verify(w => w.LogoffSession(1, false), Times.Once);
    }

    [Fact]
    public void NeverTouchesAnRdpSession_EvenWhenItIsTheOnlySessionPresent()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(2, "RDP-Tcp#0", WtsConnectState.Active)
        });

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.NoConsoleSession, outcome);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void ResetsOnlyTheConsoleSession_WhenBothConsoleAndRdpSessionsArePresent()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Active),
            new WtsSessionInfo(2, "RDP-Tcp#0", WtsConnectState.Active),
            new WtsSessionInfo(3, "RDP-Tcp#1", WtsConnectState.Disconnected)
        });
        _wts.Setup(w => w.QueryConnectState(1)).Returns(WtsConnectState.Active);
        _wts.Setup(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>())).Returns(true);

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.Success, outcome);
        _wts.Verify(w => w.LogoffSession(1, false), Times.Once);
        _wts.Verify(w => w.LogoffSession(2, It.IsAny<bool>()), Times.Never);
        _wts.Verify(w => w.LogoffSession(3, It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void RefusesToGuess_WhenMultipleSessionsAreNamedConsole()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Active),
            new WtsSessionInfo(4, "Console", WtsConnectState.Active)
        });

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.AmbiguousConsoleSession, outcome);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void RefusesImplausibleSessionId()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(0, "Console", WtsConnectState.Active)
        });

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.SafetyCheckFailed, outcome);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData(WtsConnectState.Connected)]
    [InlineData(WtsConnectState.Listen)]
    [InlineData(WtsConnectState.Idle)]
    public void SkipsTheReset_WhenTheLiveRecheckShowsTheOperatorIsNoLongerEligible(WtsConnectState liveState)
    {
        // Simulates the operator logging back in between detection and action: the session
        // table still shows the stale "Active" snapshot, but the live re-check disagrees.
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Active)
        });
        _wts.Setup(w => w.QueryConnectState(1)).Returns(liveState);

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.NoLongerEligible, outcome);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void FallsBackToTheEnumeratedState_WhenTheLiveRecheckFails()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Disconnected)
        });
        _wts.Setup(w => w.QueryConnectState(1)).Returns((WtsConnectState?)null);
        _wts.Setup(w => w.LogoffSession(1, false)).Returns(true);

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.Success, outcome);
        _wts.Verify(w => w.LogoffSession(1, false), Times.Once);
    }

    [Fact]
    public void ReportsLogoffCallFailed_WhenWTSLogoffSessionReturnsFalse()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(new[]
        {
            new WtsSessionInfo(1, "Console", WtsConnectState.Active)
        });
        _wts.Setup(w => w.QueryConnectState(1)).Returns(WtsConnectState.Active);
        _wts.Setup(w => w.LogoffSession(1, false)).Returns(false);

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.LogoffCallFailed, outcome);
    }

    [Fact]
    public void FailsSafe_WhenEnumerateSessionsThrows()
    {
        _wts.Setup(w => w.EnumerateSessions()).Throws(new InvalidOperationException("WTS unavailable"));

        var outcome = Record.Exception(() => CreateService().ResetConsoleSession("test"));

        Assert.Null(outcome); // must not throw
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void NoSessionsAtAll_ReportsNoConsoleSession()
    {
        _wts.Setup(w => w.EnumerateSessions()).Returns(Array.Empty<WtsSessionInfo>());

        var outcome = CreateService().ResetConsoleSession("test");

        Assert.Equal(ConsoleResetOutcome.NoConsoleSession, outcome);
        _wts.Verify(w => w.LogoffSession(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public void Constructor_NullWtsProvider_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ConsoleSessionResetService(null!, NullLogger<ConsoleSessionResetService>.Instance));

        Assert.Equal("wts", exception.ParamName);
    }

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new ConsoleSessionResetService(_wts.Object, null!));

        Assert.Equal("logger", exception.ParamName);
    }
}
