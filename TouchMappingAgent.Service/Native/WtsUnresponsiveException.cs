namespace TouchMappingAgent.Service.Native;

/// <summary>
/// Thrown by <see cref="TimeBoundedWtsSessionProvider"/> when a wtsapi32 call did not return
/// within its allotted time budget.
///
/// SEMANTICS — READ BEFORE ACTING ON THIS EXCEPTION: it does NOT mean the underlying call will
/// never return. It means only that it did not return within the budget given to
/// <see cref="TimeBoundedWtsSessionProvider"/>. The abandoned native thread keeps running in
/// the background; if Terminal Services (termsrv.exe) recovers, that thread completes
/// normally and releases the provider's outstanding-probe latch, after which the very next
/// call gets a completely ordinary, un-abandoned attempt.
///
/// Callers — <see cref="TouchMappingAgent.Service.HealthMonitoring.ConsoleSessionWatchdog"/> in
/// particular — must treat this as "unresponsive right now", not "permanently dead": log text
/// must not claim termsrv is dead, and the resulting health verdict must be allowed to fall
/// back to healthy on a subsequent successful call rather than latching permanently.
/// </summary>
public sealed class WtsUnresponsiveException : Exception
{
    /// <summary>Initializes a new instance with the given message.</summary>
    public WtsUnresponsiveException(string message) : base(message)
    {
    }

    /// <summary>Initializes a new instance with the given message and inner exception.</summary>
    public WtsUnresponsiveException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
