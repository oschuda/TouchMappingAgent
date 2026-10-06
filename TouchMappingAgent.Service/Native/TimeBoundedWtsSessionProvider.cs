using Microsoft.Extensions.Logging;

namespace TouchMappingAgent.Service.Native;

/// <summary>
/// Decorator over <see cref="IWtsSessionProvider"/> that bounds every call to a fixed time
/// budget.
///
/// WHY THIS EXISTS: wtsapi32 calls are synchronous LRPC to termsrv.exe with no timeout of
/// their own. If termsrv's session worker is wedged — exactly the state that made a real
/// incident's "logoff 2" and "rwinsta 2" console commands themselves hang — a raw
/// WTSEnumerateSessionsW call blocks forever.
/// <see cref="TouchMappingAgent.Service.HealthMonitoring.ConsoleSessionWatchdog"/> calls
/// <see cref="EnumerateSessions"/> every 5s on its BackgroundService loop thread; without this
/// decorator, that one blocked call wedges the loop permanently and SILENTLY (no exception is
/// ever thrown, so nothing is ever logged) — the watchdog never polls again, never re-checks
/// its CancellationToken, and gives no sign it has stopped working at all.
///
/// A THREAD BLOCKED INSIDE A NATIVE P/INVOKE CANNOT BE CANCELLED OR ABORTED IN .NET — it can
/// only be abandoned. This decorator therefore:
///  - runs each probe on a dedicated background <see cref="Thread"/> (never Task.Run / the
///    thread pool, which would permanently consume a worker thread and eventually starve it
///    if the wedge lasts — as one plausibly could — for hours),
///  - bounds the wait on the calling thread without ever blocking past the budget,
///  - and uses a single-slot <see cref="SemaphoreSlim"/> as an "outstanding probe" latch, so a
///    wedge lasting minutes or hours leaks exactly ONE abandoned thread total, not one per poll
///    cycle. While a probe is outstanding, every new call fails fast with
///    <see cref="WtsUnresponsiveException"/> instead of piling up more abandoned threads.
///
/// See <see cref="WtsUnresponsiveException"/>'s own remarks for what the exception does and
/// does not mean — that is as important as the mechanism here.
/// </summary>
public sealed class TimeBoundedWtsSessionProvider : IWtsSessionProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly IWtsSessionProvider _inner;
    private readonly ILogger<TimeBoundedWtsSessionProvider> _logger;
    private readonly TimeSpan _timeout;

    // Deliberately never disposed, and this class deliberately does NOT implement
    // IDisposable: a timed-out probe leaves its native thread still running, and that thread
    // releases this exact gate from its own finally block whenever termsrv eventually answers
    // (or never — it is a background thread, so it simply vanishes with the process on exit).
    // If this class were IDisposable and a service-shutdown Dispose() call disposed the gate
    // while that abandoned thread was still alive, the thread's later Release() call would
    // race an already-disposed SemaphoreSlim and throw ObjectDisposedException from inside a
    // thread nothing is watching. The gate is designed to live for the process's lifetime —
    // do not "clean this up" by adding IDisposable later.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of <see cref="TimeBoundedWtsSessionProvider"/>.</summary>
    /// <param name="inner">The real (or fake, in tests) provider to bound.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="timeout">Time budget per call. Defaults to 10 seconds.</param>
    public TimeBoundedWtsSessionProvider(
        IWtsSessionProvider inner,
        ILogger<TimeBoundedWtsSessionProvider> logger,
        TimeSpan? timeout = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <inheritdoc/>
    public IReadOnlyList<WtsSessionInfo> EnumerateSessions() =>
        Invoke(nameof(EnumerateSessions), _inner.EnumerateSessions);

    /// <inheritdoc/>
    public WtsConnectState? QueryConnectState(int sessionId) =>
        Invoke(nameof(QueryConnectState), () => _inner.QueryConnectState(sessionId));

    /// <inheritdoc/>
    public bool LogoffSession(int sessionId, bool wait) =>
        Invoke(nameof(LogoffSession), () => _inner.LogoffSession(sessionId, wait));

    /// <summary>
    /// Runs <paramref name="op"/> on a dedicated background thread and waits up to
    /// <see cref="_timeout"/> for it. Throws <see cref="WtsUnresponsiveException"/> — without
    /// waiting for the abandoned thread — if the budget elapses first. If a probe is already
    /// outstanding (a previous call already timed out and its thread has not yet returned),
    /// fails fast with the same exception rather than starting a second abandoned thread.
    /// </summary>
    private T Invoke<T>(string opName, Func<T> op)
    {
        if (!_gate.Wait(0))
        {
            throw new WtsUnresponsiveException(
                $"A previous {opName} call is still outstanding; refusing to start another " +
                "probe against Terminal Services while one is already abandoned.");
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                tcs.TrySetResult(op());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                // Whoever gets here — a fast call, or one that only completed long after the
                // caller below already gave up — releases the gate. This is what makes the
                // unresponsive state self-healing: the very next call after this one gets a
                // completely fresh, un-abandoned attempt.
                try
                {
                    _gate.Release();
                }
                catch (Exception releaseEx)
                {
                    // Defensive only: the gate is never disposed (see the field's remarks),
                    // so this should not happen — but a background probe thread must never
                    // let an unexpected failure propagate unobserved.
                    _logger.LogWarning(
                        releaseEx,
                        "TimeBoundedWtsSessionProvider: failed to release the probe gate after {OpName}",
                        opName);
                }
            }
        })
        {
            IsBackground = true,
            Name = $"WtsProbe-{opName}"
        };
        thread.Start();

        // WaitAny (not Task.Wait) deliberately: Task.Wait(timeout) throws AggregateException
        // synchronously the moment it observes a Faulted task, even within the timeout window,
        // which would force unwrapping it just to get the real exception back. WaitAny returns
        // the completed index for ANY completion state (RanToCompletion, Faulted, Canceled)
        // without throwing, or -1 on timeout — GetAwaiter().GetResult() below then rethrows
        // op()'s original exception type with its original stack trace intact.
        var completedIndex = Task.WaitAny(new[] { (Task)tcs.Task }, _timeout);
        if (completedIndex == -1)
        {
            _logger.LogWarning(
                "TimeBoundedWtsSessionProvider: {OpName} did not return within {TimeoutSeconds:F0}s. " +
                "This does not mean the call has failed permanently — Terminal Services may " +
                "still recover, in which case the next call will succeed normally once the " +
                "abandoned thread returns and releases the probe gate.",
                opName, _timeout.TotalSeconds);
            throw new WtsUnresponsiveException(
                $"{opName} did not return within {_timeout.TotalSeconds:F0}s.");
        }

        return tcs.Task.GetAwaiter().GetResult();
    }
}
