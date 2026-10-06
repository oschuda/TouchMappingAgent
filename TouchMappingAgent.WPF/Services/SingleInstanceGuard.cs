using System.Threading;
using Microsoft.Extensions.Logging;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Keeps exactly one tray agent alive per session and lets a second launch hand its intent to
/// the one already running.
///
/// WHY THIS IS NOT COSMETIC: two agents in the same session both poll the service for pending
/// re-applications and both run tabcal.exe for the same assignment. Beyond the duplicate tray
/// icon that means two processes writing the Windows touch calibration concurrently. The
/// installer makes this concrete — it starts the agent silently AND offers a finish-page
/// checkbox that launches it again with --wizard — but autostart plus a desktop shortcut
/// produces the same collision without any installer involved.
///
/// Uses a named mutex for ownership and named events for the hand-off. Both are Local\, i.e.
/// per-session: a second interactive session (a technician on the console while an operator is
/// on RDP) legitimately needs its own agent, because each session has its own desktop and its
/// own tabcal.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\PadaLuma.TouchMappingAgent.Client";
    private const string ShowWizardEventName = @"Local\PadaLuma.TouchMappingAgent.ShowWizard";
    private const string ShowMainEventName = @"Local\PadaLuma.TouchMappingAgent.ShowMain";

    private readonly ILogger<SingleInstanceGuard> _logger;
    private Mutex? _mutex;
    private EventWaitHandle? _showWizard;
    private EventWaitHandle? _showMain;
    private CancellationTokenSource? _listenerCts;
    private bool _disposed;

    /// <summary>Raised when another launch asked for the wizard. Fires on a background thread.</summary>
    public event Action? ShowWizardRequested;

    /// <summary>Raised when another launch asked for the main window.</summary>
    public event Action? ShowMainWindowRequested;

    /// <summary>Initializes a new instance of <see cref="SingleInstanceGuard"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public SingleInstanceGuard(ILogger<SingleInstanceGuard> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>True when this process owns the single-instance slot.</summary>
    public bool IsPrimaryInstance { get; private set; }

    /// <summary>
    /// Claims the slot. Returns true when this process is the first — the caller should carry
    /// on starting up. Returns false when another agent already runs; the caller should signal
    /// its intent via <see cref="SignalExistingInstance"/> and exit.
    /// </summary>
    public bool TryClaim()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            IsPrimaryInstance = createdNew;

            if (!createdNew)
            {
                _logger.LogInformation("Another agent already runs in this session");
                _mutex.Dispose();
                _mutex = null;
            }

            return IsPrimaryInstance;
        }
        catch (Exception ex)
        {
            // If the guard itself fails, starting is the lesser evil: refusing to run would
            // leave the machine with no agent at all, which is the failure this product exists
            // to prevent.
            _logger.LogWarning(ex, "Single-instance check failed; continuing as primary");
            IsPrimaryInstance = true;
            return true;
        }
    }

    /// <summary>
    /// Starts listening for hand-offs from later launches. Only meaningful on the primary.
    /// </summary>
    public void StartListening()
    {
        if (!IsPrimaryInstance)
            return;

        try
        {
            _showWizard = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWizardEventName);
            _showMain = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainEventName);
            _listenerCts = new CancellationTokenSource();

            var token = _listenerCts.Token;
            var thread = new Thread(() => ListenLoop(token))
            {
                IsBackground = true,
                Name = "Single-instance listener"
            };
            thread.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start the single-instance listener");
        }
    }

    private void ListenLoop(CancellationToken token)
    {
        var handles = new WaitHandle[] { _showWizard!, _showMain!, token.WaitHandle };

        while (!token.IsCancellationRequested)
        {
            int index;
            try
            {
                index = WaitHandle.WaitAny(handles);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Single-instance listener stopped");
                return;
            }

            if (token.IsCancellationRequested || index == 2)
                return;

            try
            {
                if (index == 0)
                {
                    _logger.LogInformation("Another launch requested the setup wizard");
                    ShowWizardRequested?.Invoke();
                }
                else
                {
                    _logger.LogInformation("Another launch requested the main window");
                    ShowMainWindowRequested?.Invoke();
                }
            }
            catch (Exception ex)
            {
                // A handler throwing must not kill the listener; the next hand-off should
                // still get through.
                _logger.LogError(ex, "A single-instance hand-off handler threw");
            }
        }
    }

    /// <summary>
    /// Tells the running agent what this launch wanted. Safe to call from a secondary instance
    /// that is about to exit.
    /// </summary>
    /// <param name="wantsWizard">True to open the wizard, false for the main window.</param>
    public static void SignalExistingInstance(bool wantsWizard)
    {
        try
        {
            var name = wantsWizard ? ShowWizardEventName : ShowMainEventName;
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                using (handle)
                    handle.Set();
            }
        }
        catch (Exception)
        {
            // The other instance may be shutting down. Nothing useful to do; this process is
            // exiting either way.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _listenerCts?.Cancel();
            _listenerCts?.Dispose();
            _showWizard?.Dispose();
            _showMain?.Dispose();

            if (_mutex != null)
            {
                if (IsPrimaryInstance)
                    _mutex.ReleaseMutex();

                _mutex.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Single-instance cleanup threw");
        }
    }
}
