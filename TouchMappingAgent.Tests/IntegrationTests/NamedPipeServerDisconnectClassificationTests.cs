using System.IO;
using TouchMappingAgent.Service.IPC;
using Xunit;

namespace TouchMappingAgent.Tests.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="NamedPipeServer.IsExpectedDisconnect"/> / IsExpectedPipeDisconnect
/// — the classification fix for RDP-session (and every other ordinary client disconnect)
/// churn spamming the Windows Event Log as UNAUTHORIZED_ACCESS/PipeServerError entries.
///
/// The one regression this suite specifically guards against: an earlier version of this fix
/// treated EVERY IOException as an ordinary disconnect. That is deliberately NOT what shipped
/// — only ERROR_BROKEN_PIPE/ERROR_NO_DATA/ERROR_PIPE_NOT_CONNECTED are classified as harmless,
/// so a genuine I/O fault (disk full, handle exhaustion, ...) surfacing as IOException must
/// still reach the Event Log as a real error.
/// </summary>
public class NamedPipeServerDisconnectClassificationTests
{
    private static int Win32HResult(int win32Error) => unchecked((int)0x80070000 | win32Error);

    [Theory]
    [InlineData(NamedPipeServer_ErrorBrokenPipe)]
    [InlineData(NamedPipeServer_ErrorNoData)]
    [InlineData(NamedPipeServer_ErrorPipeNotConnected)]
    public void KnownDisconnectCodes_AreClassifiedAsExpected(int win32Error)
    {
        var ex = new IOException("Pipe is broken.", Win32HResult(win32Error));

        Assert.True(NamedPipeServer.IsExpectedPipeDisconnect(ex));
    }

    [Fact]
    public void UnrelatedWin32Error_IsNotClassifiedAsExpected()
    {
        // ERROR_DISK_FULL (112) — a real fault that happens to also be an IOException. Must
        // NOT be swallowed as "just a disconnect".
        var ex = new IOException("Disk full.", Win32HResult(112));

        Assert.False(NamedPipeServer.IsExpectedPipeDisconnect(ex));
    }

    [Fact]
    public void IOExceptionWithoutAWin32Hresult_IsNotClassifiedAsExpected()
    {
        // Plain constructor — HResult is COR_E_IO (0x80131620 or similar managed HResult),
        // not FACILITY_WIN32. Must fail the facility check and be treated as a real fault
        // rather than accidentally matching a low-order-bits coincidence.
        var ex = new IOException("Some other I/O problem.");

        Assert.False(NamedPipeServer.IsExpectedPipeDisconnect(ex));
    }

    [Fact]
    public void IsExpectedDisconnect_BrokenPipeIOException_IsExpectedRegardlessOfCancellation()
    {
        var ex = new IOException("Pipe is broken.", Win32HResult(NamedPipeServer_ErrorBrokenPipe));
        using var cts = new CancellationTokenSource();

        Assert.True(NamedPipeServer.IsExpectedDisconnect(ex, cts.Token, duringTeardown: false));
        Assert.True(NamedPipeServer.IsExpectedDisconnect(ex, cts.Token, duringTeardown: true));
    }

    [Fact]
    public void IsExpectedDisconnect_UnrelatedIOException_IsNeverExpected()
    {
        var ex = new IOException("Disk full.", Win32HResult(112));
        using var cts = new CancellationTokenSource();

        Assert.False(NamedPipeServer.IsExpectedDisconnect(ex, cts.Token, duringTeardown: false));
        Assert.False(NamedPipeServer.IsExpectedDisconnect(ex, cts.Token, duringTeardown: true));
    }

    [Fact]
    public void IsExpectedDisconnect_OperationCanceled_OnlyExpectedWhenCancellationWasActuallyRequested()
    {
        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        using var liveCts = new CancellationTokenSource();

        var ex = new OperationCanceledException();

        Assert.True(NamedPipeServer.IsExpectedDisconnect(ex, cancelledCts.Token, duringTeardown: false));
        Assert.False(NamedPipeServer.IsExpectedDisconnect(ex, liveCts.Token, duringTeardown: false));
    }

    [Fact]
    public void IsExpectedDisconnect_ObjectDisposed_DuringTeardown_IsAlwaysExpected()
    {
        using var liveCts = new CancellationTokenSource();
        var ex = new ObjectDisposedException("pipeServer");

        // duringTeardown: true covers disposal-time ObjectDisposedException even without an
        // observed cancellation — the pipe object can legitimately already be torn down from
        // the OS side purely because the client vanished.
        Assert.True(NamedPipeServer.IsExpectedDisconnect(ex, liveCts.Token, duringTeardown: true));
    }

    [Fact]
    public void IsExpectedDisconnect_ObjectDisposed_MidRequest_OnlyExpectedIfCancellationWasObserved()
    {
        using var liveCts = new CancellationTokenSource();
        using var cancelledCts = new CancellationTokenSource();
        cancelledCts.Cancel();
        var ex = new ObjectDisposedException("writer");

        Assert.False(NamedPipeServer.IsExpectedDisconnect(ex, liveCts.Token, duringTeardown: false));
        Assert.True(NamedPipeServer.IsExpectedDisconnect(ex, cancelledCts.Token, duringTeardown: false));
    }

    [Fact]
    public void IsExpectedDisconnect_AnyOtherExceptionType_IsNeverExpected()
    {
        using var cts = new CancellationTokenSource();

        Assert.False(NamedPipeServer.IsExpectedDisconnect(new InvalidOperationException(), cts.Token, duringTeardown: true));
        Assert.False(NamedPipeServer.IsExpectedDisconnect(new ArgumentException(), cts.Token, duringTeardown: true));
    }

    // Mirrors NamedPipeServer's own internal constants so this test file does not need
    // InternalsVisibleTo-only access to them individually for the Theory attribute (which
    // requires compile-time constants).
    private const int NamedPipeServer_ErrorBrokenPipe = 109;
    private const int NamedPipeServer_ErrorNoData = 232;
    private const int NamedPipeServer_ErrorPipeNotConnected = 233;
}
