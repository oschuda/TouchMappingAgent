using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Service.Native;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Unit tests for <see cref="TimeBoundedWtsSessionProvider"/>: the timeout guard around
/// <see cref="IWtsSessionProvider"/>, and the self-healing "outstanding probe" latch that
/// stops a wedged call from leaking one abandoned thread per poll cycle.
/// </summary>
public class TimeBoundedWtsSessionProviderTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(200);

    [Fact]
    public void FastCall_ReturnsTheInnerResult()
    {
        var inner = new FakeProvider { Sessions = new[] { new WtsSessionInfo(1, "Console", WtsConnectState.Active) } };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        var result = provider.EnumerateSessions();

        Assert.Single(result);
        Assert.Equal(1, result[0].SessionId);
    }

    [Fact]
    public void SlowCall_ThrowsWtsUnresponsiveExceptionWithoutWaitingForTheAbandonedThread()
    {
        var inner = new FakeProvider { EnumerateBlocksUntil = new ManualResetEventSlim(false) };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exception = Record.Exception(() => provider.EnumerateSessions());
        sw.Stop();

        Assert.IsType<WtsUnresponsiveException>(exception);
        // The caller must not have waited anywhere near how long the inner call actually
        // takes to unblock — only up to (roughly) the configured timeout.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}, expected close to {ShortTimeout}");

        inner.EnumerateBlocksUntil!.Set(); // release the abandoned thread so the test process can exit cleanly
    }

    [Fact]
    public void InnerException_WithinBudget_PropagatesTheOriginalExceptionType()
    {
        var inner = new FakeProvider { EnumerateThrows = new InvalidOperationException("boom") };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        var exception = Record.Exception(() => provider.EnumerateSessions());

        // Must be the ORIGINAL exception type, not an AggregateException wrapper — this is
        // what Task.WaitAny + GetAwaiter().GetResult() (rather than Task.Wait/.Result) buys.
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public void OutstandingProbe_FailsFastRatherThanStartingASecondAbandonedThread()
    {
        var inner = new FakeProvider { EnumerateBlocksUntil = new ManualResetEventSlim(false) };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        var first = Record.Exception(() => provider.EnumerateSessions());
        Assert.IsType<WtsUnresponsiveException>(first);

        // The first probe's thread is still blocked (we never signalled it). A second call,
        // issued immediately, must fail fast without starting another thread against the
        // still-wedged inner provider.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = Record.Exception(() => provider.EnumerateSessions());
        sw.Stop();

        Assert.IsType<WtsUnresponsiveException>(second);
        Assert.True(sw.Elapsed < ShortTimeout, "The second call should fail immediately via the gate, not wait out its own timeout.");

        inner.EnumerateBlocksUntil!.Set();
    }

    [Fact]
    public void Recovery_OnceTheAbandonedThreadReturns_TheNextCallSucceedsNormally()
    {
        var gate = new ManualResetEventSlim(false);
        var inner = new FakeProvider { EnumerateBlocksUntil = gate };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        var first = Record.Exception(() => provider.EnumerateSessions());
        Assert.IsType<WtsUnresponsiveException>(first);

        // Let the abandoned thread's call complete and release the probe gate.
        gate.Set();
        inner.EnumerateBlocksUntil = null; // subsequent calls on the inner provider return immediately
        inner.Sessions = new[] { new WtsSessionInfo(1, "Console", WtsConnectState.Active) };

        // Give the background thread a moment to actually finish and release the gate before
        // asserting recovery — this is inherent to testing real background-thread behaviour.
        IReadOnlyList<WtsSessionInfo>? result = null;
        Exception? lastException = null;
        for (int attempt = 0; attempt < 50 && result == null; attempt++)
        {
            try
            {
                result = provider.EnumerateSessions();
            }
            catch (WtsUnresponsiveException ex)
            {
                lastException = ex;
                Thread.Sleep(20);
            }
        }

        Assert.NotNull(result);
        Assert.Single(result!);
    }

    [Fact]
    public void QueryConnectState_DelegatesToInner()
    {
        var inner = new FakeProvider { QueryResult = WtsConnectState.Disconnected };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        Assert.Equal(WtsConnectState.Disconnected, provider.QueryConnectState(5));
    }

    [Fact]
    public void LogoffSession_DelegatesToInner()
    {
        var inner = new FakeProvider { LogoffResult = true };
        var provider = new TimeBoundedWtsSessionProvider(inner, NullLogger<TimeBoundedWtsSessionProvider>.Instance, ShortTimeout);

        Assert.True(provider.LogoffSession(5, wait: false));
        Assert.Equal(5, inner.LastLogoffSessionId);
    }

    /// <summary>Fake <see cref="IWtsSessionProvider"/> that can be made to block indefinitely
    /// (until a supplied <see cref="ManualResetEventSlim"/> is set), throw, or return a fixed
    /// value — enough to drive every branch of the timeout decorator above.</summary>
    private sealed class FakeProvider : IWtsSessionProvider
    {
        public IReadOnlyList<WtsSessionInfo> Sessions = Array.Empty<WtsSessionInfo>();
        public ManualResetEventSlim? EnumerateBlocksUntil;
        public Exception? EnumerateThrows;
        public WtsConnectState? QueryResult;
        public bool LogoffResult;
        public int? LastLogoffSessionId;

        public IReadOnlyList<WtsSessionInfo> EnumerateSessions()
        {
            EnumerateBlocksUntil?.Wait();
            if (EnumerateThrows != null)
                throw EnumerateThrows;
            return Sessions;
        }

        public WtsConnectState? QueryConnectState(int sessionId) => QueryResult;

        public bool LogoffSession(int sessionId, bool wait)
        {
            LastLogoffSessionId = sessionId;
            return LogoffResult;
        }
    }
}
