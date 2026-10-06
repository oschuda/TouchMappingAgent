using System.Windows.Threading;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Hosts a single WPF <see cref="System.Windows.Application"/> on one STA thread for the whole
/// test collection.
///
/// WPF permits exactly one Application per AppDomain, and every window is affine to the thread
/// that created it. Creating an Application per test therefore fails on the second one, and
/// touching a window from the xUnit worker thread throws "the calling thread cannot access this
/// object". So: one thread, one Application, and all window work marshalled onto it.
///
/// The Application instance is the product's own <see cref="TouchMappingAgent.WPF.App"/>,
/// because loading its merged resource dictionary is exactly what the tests need to verify —
/// the windows resolve their EvolvedDesignUI styles and converters against it.
/// </summary>
public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher? _dispatcher;
    private Exception? _startupFailure;

    /// <summary>Starts the STA thread and waits until the Application is live.</summary>
    public WpfApplicationFixture()
    {
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "WPF test host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Task.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("The WPF test host did not start within 30 seconds.");

        if (_startupFailure != null)
            throw new InvalidOperationException("The WPF test host failed to start.", _startupFailure);
    }

    private void ThreadMain()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Only construct it once; a second instance would throw.
            if (System.Windows.Application.Current == null)
                _ = new TouchMappingAgent.WPF.App();

            _ready.SetResult();
        }
        catch (Exception ex)
        {
            _startupFailure = ex;
            _ready.TrySetResult();
            return;
        }

        Dispatcher.Run();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread and rethrows anything it threw, so the
    /// assertion reports the real cause rather than a marshalling wrapper.
    /// </summary>
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_dispatcher == null)
            throw new InvalidOperationException("The WPF test host is not running.");

        Exception? failure = null;

        _dispatcher.Invoke(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure != null)
            throw new Xunit.Sdk.XunitException($"UI work threw: {failure}");
    }

    /// <summary>Looks up an application-level resource from the UI thread.</summary>
    public object? FindResource(string key)
    {
        object? value = null;
        Invoke(() => value = System.Windows.Application.Current?.TryFindResource(key));
        return value;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _dispatcher?.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Collection definition binding every WPF test to the single Application host.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfApplicationFixture>
{
    /// <summary>Collection name.</summary>
    public const string Name = "WPF UI";
}
