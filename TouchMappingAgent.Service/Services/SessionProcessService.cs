using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.Logging;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// One process running in some session, as evaluated for eligibility to be forcibly
/// terminated as part of an unresponsive-session recovery. Every field that can fail to
/// resolve is nullable rather than defaulted to something that LOOKS safe — a null field
/// means "could not be determined", and every caller treats "could not be determined" the
/// same as "assume the worst", never as "assume it's fine".
/// </summary>
public sealed record SessionProcessInfo(
    int ProcessId,
    string ProcessName,
    int SessionId,
    string? UserName,
    string? UserSid,
    string? IntegrityLevel,
    bool IsCriticalOrProtected);

/// <summary>One process this decision chain declined to touch, and why.</summary>
public sealed record SessionProcessSkip(SessionProcessInfo Process, string Reason);

/// <summary>One process a termination attempt was made for, and the outcome.</summary>
public sealed record SessionProcessFailure(SessionProcessInfo Process, string Error);

/// <summary>Outcome of one <see cref="ISessionProcessService.TerminateUserProcesses"/> call.</summary>
public sealed record SessionTerminationReport(
    IReadOnlyList<SessionProcessInfo> Terminated,
    IReadOnlyList<SessionProcessSkip> Skipped,
    IReadOnlyList<SessionProcessFailure> Failed);

/// <summary>
/// Reads and, on request, forcibly terminates the user-mode application processes of one
/// Windows session — via a path that is independent of Terminal Services (termsrv.exe), so it
/// keeps working even when termsrv itself is wedged (see
/// <see cref="TouchMappingAgent.Service.HealthMonitoring.ConsoleSessionWatchdog"/>'s
/// TerminalServicesUnresponsive verdict, the one case this exists for). Also used, read-only,
/// by the watchdog's HeartbeatMissing plausibilisation check (does the client process still
/// exist in this session at all?).
/// </summary>
public interface ISessionProcessService
{
    /// <summary>Lists every process currently running in the given session, INCLUDING
    /// critical/system ones — callers that only care about presence (e.g. "is the client
    /// still running here?") filter by name themselves; <see cref="TerminateUserProcesses"/>
    /// applies the full safety gate internally.</summary>
    IReadOnlyList<SessionProcessInfo> ListUserProcesses(int sessionId);

    /// <summary>
    /// Terminates every process in <paramref name="sessionId"/> that survives the full safety
    /// gate (see this interface's remarks and <see cref="SessionProcessService"/>'s). Logs the
    /// complete candidate list BEFORE attempting any kill, and kills one process at a time so
    /// a single failure never aborts the rest.
    /// </summary>
    SessionTerminationReport TerminateUserProcesses(int sessionId, string reason);
}

/// <summary>
/// Real <see cref="ISessionProcessService"/>, backed by <see cref="Process.GetProcesses()"/>
/// (which resolves session ids via ProcessIdToSessionId — a kernel32 call against the OS
/// process table, NOT Terminal Services RPC — so it keeps working when termsrv is wedged) and
/// direct process-token inspection.
///
/// THIS IS THE HIGHEST-BLAST-RADIUS CODE IN THE WHOLE WATCHDOG FEATURE. Every gate below is
/// deliberately fail-closed: anything that cannot be positively verified as safe is treated as
/// unsafe (skipped), never as safe by default.
/// </summary>
public sealed class SessionProcessService : ISessionProcessService
{
    // TerminateProcess on any of these can bring down the whole machine
    // (STATUS_CRITICAL_PROCESS_DIED / STATUS_SYSTEM_PROCESS_TERMINATED bugcheck), regardless
    // of session. Matched case-insensitively, without the .exe extension. This list is a
    // second, independent layer on top of TryIsBreakOnTermination below — not a substitute
    // for it, since a process this list has never heard of could still be marked critical by
    // the OS itself.
    private static readonly HashSet<string> CriticalProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "winlogon", "wininit", "lsass", "services", "smss",
        "dwm", "fontdrvhost", "sihost", "system", "idle", "registry", "memcompression"
    };

    // Well-known service-account SIDs. A process running as one of these is a Windows/system
    // service, not the interactive user's own application — never a kill target here even if
    // it happens to report the target SessionId.
    private static readonly HashSet<string> SystemAccountSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18", // LocalSystem
        "S-1-5-19", // LocalService
        "S-1-5-20", // NetworkService
    };

    private readonly ILogger<SessionProcessService> _logger;

    /// <summary>Initializes a new instance of <see cref="SessionProcessService"/>.</summary>
    public SessionProcessService(ILogger<SessionProcessService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public IReadOnlyList<SessionProcessInfo> ListUserProcesses(int sessionId)
    {
        var result = new List<SessionProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                SessionProcessInfo? info;
                try
                {
                    info = TryDescribe(process, sessionId);
                }
                catch (Exception ex)
                {
                    // A single unreadable process (already exited, access denied, whatever)
                    // must never abort enumeration of the rest.
                    _logger.LogDebug(ex, "SessionProcessService: could not describe PID {ProcessId}", process.Id);
                    info = null;
                }

                if (info != null)
                    result.Add(info);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public SessionTerminationReport TerminateUserProcesses(int sessionId, string reason)
    {
        var candidates = ListUserProcesses(sessionId);

        var terminated = new List<SessionProcessInfo>();
        var skipped = new List<SessionProcessSkip>();
        var failed = new List<SessionProcessFailure>();

        // Audit-first, ALWAYS — the full candidate list (including what gets skipped and why)
        // is logged before a single TerminateProcess call is made. ISO 27001 A.8.15.
        _logger.LogWarning(
            "[SessionWatchdog] Session {SessionId} unresponsive ({Reason}). Evaluating {Count} process(es) for termination:",
            sessionId, reason, candidates.Count);
        foreach (var candidate in candidates)
        {
            _logger.LogWarning(
                "[SessionWatchdog]   {ProcessName} (PID {ProcessId}, user={UserName}, critical={IsCritical})",
                candidate.ProcessName, candidate.ProcessId, candidate.UserName ?? "<unknown>", candidate.IsCriticalOrProtected);
        }

        foreach (var candidate in candidates)
        {
            var skipReason = EvaluateSkipReason(candidate);
            if (skipReason != null)
            {
                skipped.Add(new SessionProcessSkip(candidate, skipReason));
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(candidate.ProcessId);
                process.Kill();
                terminated.Add(candidate);
                _logger.LogWarning(
                    "[SessionWatchdog] Terminated {ProcessName} (PID {ProcessId}) in unresponsive session {SessionId}.",
                    candidate.ProcessName, candidate.ProcessId, sessionId);
            }
            catch (Exception ex)
            {
                // Honest expectation (see ConsoleSessionWatchdog's TerminalServicesUnresponsive
                // remarks): a kernel-mode/driver-level hang can make TerminateProcess itself
                // never complete either. Record the failure; do not treat it as fatal to the
                // rest of the batch.
                failed.Add(new SessionProcessFailure(candidate, $"{ex.GetType().Name}: {ex.Message}"));
                _logger.LogError(
                    ex, "[SessionWatchdog] Failed to terminate {ProcessName} (PID {ProcessId}) in session {SessionId}.",
                    candidate.ProcessName, candidate.ProcessId, sessionId);
            }
        }

        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.TerminalServicesUnresponsive,
            $"SESSION_{sessionId}_PROCESS_TERMINATION",
            success: failed.Count == 0,
            errorMessage: failed.Count == 0
                ? null
                : $"{failed.Count} of {candidates.Count} candidate process(es) failed to terminate");

        return new SessionTerminationReport(terminated, skipped, failed);
    }

    /// <summary>
    /// The decision chain, in order: every stage must agree before a process is eligible for
    /// termination. Returns null when eligible, or the reason it was skipped. Internal (not
    /// private) specifically so this — the highest-stakes logic in the whole watchdog feature
    /// — can be unit-tested directly against hand-built <see cref="SessionProcessInfo"/>
    /// records, without ever calling <see cref="Process.Kill()"/> against a real process.
    /// </summary>
    internal string? EvaluateSkipReason(SessionProcessInfo info)
    {
        if (info.ProcessId == Environment.ProcessId)
            return "is this service's own process";

        if (info.IsCriticalOrProtected)
            return "critical/protected system process";

        if (info.UserSid == null)
            return "identity could not be determined";

        if (SystemAccountSids.Contains(info.UserSid))
            return "runs under a system service account";

        return null;
    }

    /// <summary>
    /// Builds a <see cref="SessionProcessInfo"/> for one live process, or returns null if it
    /// does not belong to <paramref name="sessionId"/>. Every failure to introspect a field
    /// leaves that field null rather than guessing — see the type's remarks.
    /// </summary>
    private SessionProcessInfo? TryDescribe(Process process, int sessionId)
    {
        int actualSessionId;
        string processName;
        try
        {
            actualSessionId = process.SessionId;
            processName = process.ProcessName;
        }
        catch (Exception)
        {
            // Process exited between GetProcesses() and here, or is otherwise unreadable.
            return null;
        }

        if (actualSessionId != sessionId)
            return null;

        string? userName = null;
        string? userSid = null;
        string? integrityLevel = null;
        bool isCriticalOrProtected = CriticalProcessNames.Contains(processName);

        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = process.Handle;
        }
        catch (Exception)
        {
            // Could not even open the process (commonly true for a genuinely protected
            // process, e.g. "Registry" or "MemCompression"). Fail closed: if we cannot open
            // it to verify anything, treat it as critical/protected rather than assuming it
            // is safe because the name blocklist happened not to already know it.
            isCriticalOrProtected = true;
        }

        if (processHandle != IntPtr.Zero)
        {
            (userName, userSid) = TryGetIdentity(processHandle);
            integrityLevel = TryGetIntegrityLevel(processHandle);

            var breakOnTermination = TryIsBreakOnTermination(processHandle);
            if (breakOnTermination != false)
            {
                // true, OR null (could not be determined) — fail closed either way.
                isCriticalOrProtected = isCriticalOrProtected || breakOnTermination != false;
            }
        }

        return new SessionProcessInfo(
            process.Id, processName, actualSessionId, userName, userSid, integrityLevel, isCriticalOrProtected);
    }

    /// <summary>Resolves a process's token owner via the same OpenProcessToken + WindowsIdentity
    /// pattern already used by SecureNamedPipeFactory.VerifyServiceIdentity. Returns (null, null)
    /// on any failure — never a guess.</summary>
    private static (string? UserName, string? UserSid) TryGetIdentity(IntPtr processHandle)
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out var tokenHandle))
                return (null, null);

            using (tokenHandle)
            {
                var identity = new WindowsIdentity(tokenHandle.DangerousGetHandle());
                return (identity.Name, identity.User?.Value);
            }
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Best-effort mandatory-integrity-level lookup (Untrusted/Low/Medium/High/
    /// System/Protected). Purely informational for the audit record — never a decision input
    /// on its own. Returns null on any difficulty, per this type's "null means unknown, not
    /// safe" convention.</summary>
    private static string? TryGetIntegrityLevel(IntPtr processHandle)
    {
        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr tokenInfo = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessTokenRaw(processHandle, NativeMethods.TOKEN_QUERY, out tokenHandle))
                return null;

            const int tokenIntegrityLevel = 25; // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel
            if (!NativeMethods.GetTokenInformation(tokenHandle, tokenIntegrityLevel, IntPtr.Zero, 0, out var requiredSize)
                && requiredSize == 0)
            {
                return null;
            }

            tokenInfo = Marshal.AllocHGlobal(requiredSize);
            if (!NativeMethods.GetTokenInformation(tokenHandle, tokenIntegrityLevel, tokenInfo, requiredSize, out _))
                return null;

            // TOKEN_MANDATORY_LABEL.Label.Sid is the first (and only, for this struct) field.
            var sidPtr = Marshal.ReadIntPtr(tokenInfo);
            var subAuthorityCount = Marshal.ReadByte(sidPtr, 1);
            if (subAuthorityCount == 0)
                return null;

            var lastRidOffset = 8 + (4 * (subAuthorityCount - 1));
            var rid = (uint)Marshal.ReadInt32(sidPtr, lastRidOffset);

            return rid switch
            {
                < 0x1000 => "Untrusted",
                < 0x2000 => "Low",
                < 0x3000 => "Medium",
                < 0x4000 => "High",
                < 0x5000 => "System",
                _ => "Protected",
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (tokenInfo != IntPtr.Zero)
                Marshal.FreeHGlobal(tokenInfo);
            if (tokenHandle != IntPtr.Zero)
                NativeMethods.CloseHandle(tokenHandle);
        }
    }

    /// <summary>
    /// Queries ProcessBreakOnTermination — the actual OS-level flag that determines whether
    /// TerminateProcess on this process would bugcheck the machine (STATUS_SYSTEM_PROCESS_
    /// TERMINATED). This is the real, authoritative check the CriticalProcessNames list is
    /// only a fallback/second-layer for. Returns null if the flag could not be read (e.g.
    /// insufficient access to a protected process) — callers treat null as "assume critical".
    /// </summary>
    private static bool? TryIsBreakOnTermination(IntPtr processHandle)
    {
        try
        {
            const int processBreakOnTermination = 0x1D;
            var status = NativeMethods.NtQueryInformationProcess(
                processHandle, processBreakOnTermination, out var value, sizeof(int), out _);

            return status == 0 ? value != 0 : null; // 0 == STATUS_SUCCESS
        }
        catch
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        internal const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(
            IntPtr processHandle, uint desiredAccess, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "OpenProcessToken")]
        internal static extern bool OpenProcessTokenRaw(
            IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool GetTokenInformation(
            IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
            int tokenInformationLength, out int returnLength);

        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            IntPtr processHandle, int processInformationClass, out int processInformation,
            int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll")]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
