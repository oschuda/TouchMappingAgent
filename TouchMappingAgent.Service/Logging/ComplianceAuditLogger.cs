using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TouchMappingAgent.Service.Logging;

/// <summary>
/// Compliance audit logger for ISO 27001 and NIS2 directives.
/// All critical actions logged to Windows Event Log with PII sanitization.
/// </summary>
public static class ComplianceAuditLogger
{
    private const string EventSourceName = "TouchMappingAgent";
    private const string EventLogName = "Application";
    // W-3 fix: 256 characters truncated multi-phase recovery details, violating ISO 27001 A.8.15.
    // Windows Event Log supports up to 32,766 bytes per entry.
    private const int MaxLogLength = 16384;
    private const int SuccessEventId = 1000;
    private const int FailureEventId = 1001;

    /// <summary>
    /// Ensures the event source exists (call during service installation).
    /// </summary>
    public static void EnsureEventSourceExists()
    {
        try
        {
            if (!EventLog.SourceExists(EventSourceName))
            {
                EventLog.CreateEventSource(EventSourceName, EventLogName);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to create event source: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs a critical action to Windows Event Log (ISO 27001 A.8.15 compliant).
    /// </summary>
    /// <param name="action">The action performed (e.g., SAVE_MAPPING, START_REPAIR).</param>
    /// <param name="deviceId">The unique device identifier.</param>
    /// <param name="success">Whether the action succeeded.</param>
    /// <param name="errorMessage">Optional error message (will be sanitized).</param>
    public static void LogCriticalAction(
        string action,
        string deviceId,
        bool success,
        string? errorMessage = null)
    {
        try
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            var userSession = GetSafeUserSession();
            var status = success ? "SUCCESS" : "FAILURE";

            // ISO 27001 A.8.15 compliant format
            var logEntry = $"[{timestamp}] User: {userSession} | Action: {action} | " +
                           $"DeviceId: {deviceId} | Status: {status}";

            if (!success && !string.IsNullOrEmpty(errorMessage))
            {
                var sanitized = SanitizeErrorMessage(errorMessage);
                logEntry += $" | Error: {sanitized}";
            }

            // Truncate if needed
            if (logEntry.Length > MaxLogLength)
                logEntry = logEntry.Substring(0, MaxLogLength - 3) + "...";

            using (var eventLog = new EventLog(EventLogName))
            {
                eventLog.Source = EventSourceName;
                var eventType = success ? EventLogEntryType.Information : EventLogEntryType.Warning;
                var eventId = success ? SuccessEventId : FailureEventId;
                eventLog.WriteEntry(logEntry, eventType, eventId);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Audit logging failed: {ex.Message}");
            // IMPORTANT: Do not throw - ensure logging never crashes the service
        }
    }

    /// <summary>
    /// Logs a configuration change with full audit trail.
    /// </summary>
    public static void LogConfigurationChange(
        string configKey,
        string? oldValue,
        string? newValue,
        bool success)
    {
        var sanitizedOld = SanitizeErrorMessage(oldValue ?? "[null]");
        var sanitizedNew = SanitizeErrorMessage(newValue ?? "[null]");

        LogCriticalAction(
            $"CONFIG_CHANGE",
            configKey,
            success,
            $"Old: {sanitizedOld} | New: {sanitizedNew}");
    }

    /// <summary>
    /// Sanitizes error messages to prevent PII leakage.
    /// Removes file paths, email addresses, and sensitive data.
    /// </summary>
    private static string SanitizeErrorMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        // Remove Windows file paths (C:\..., D:\..., etc.)
        var sanitized = Regex.Replace(
            message,
            @"[C-Z]:\\[^\s]*",
            "[PATH]",
            RegexOptions.IgnoreCase);

        // Remove UNC paths (\\server\share)
        sanitized = Regex.Replace(
            sanitized,
            @"\\\\[^\s]*",
            "[UNC]");

        // Remove email addresses
        sanitized = Regex.Replace(
            sanitized,
            @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
            "[EMAIL]");

        // Remove SID patterns (S-1-5-...)
        sanitized = Regex.Replace(
            sanitized,
            @"S-1-\d+-(?:\d+-)*\d+",
            "[SID]");

        // Remove registry paths
        sanitized = Regex.Replace(
            sanitized,
            @"HKEY_[A-Z_]+\\[^\s]*",
            "[REGKEY]");

        return sanitized.Length > MaxLogLength
            ? sanitized.Substring(0, MaxLogLength)
            : sanitized;
    }

    /// <summary>
    /// Gets the current user session safely (without exposing PII).
    /// </summary>
    private static string GetSafeUserSession()
    {
        try
        {
            // Return SYSTEM or the service account name only
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var accountName = identity?.Name ?? "UNKNOWN";

            // Mask domain if present
            if (accountName.Contains("\\"))
            {
                return "[SERVICE]";
            }

            return accountName;
        }
        catch
        {
            return "[SERVICE]";
        }
    }
}

/// <summary>
/// Standard action names for audit logging (ISO 27001 compliance).
/// </summary>
public static class AuditActions
{
    /// <summary>Advanced repair process initiated.</summary>
    public const string StartAdvancedRepair = "START_ADVANCED_REPAIR";

    /// <summary>Touch device mapping saved to configuration.</summary>
    public const string SaveMapping = "SAVE_MAPPING";

    /// <summary>Touch device mapping deleted.</summary>
    public const string DeleteMapping = "DELETE_MAPPING";

    /// <summary>Configuration file loaded from disk.</summary>
    public const string LoadConfiguration = "LOAD_CONFIGURATION";

    /// <summary>Service started.</summary>
    public const string ServiceStartup = "SERVICE_STARTUP";

    /// <summary>Service stopped/shutdown.</summary>
    public const string ServiceShutdown = "SERVICE_SHUTDOWN";

    /// <summary>Administrative user accessed device mapping.</summary>
    public const string AdminAccess = "ADMIN_ACCESS";

    /// <summary>Whitelist entry added or modified.</summary>
    public const string WhitelistModified = "WHITELIST_MODIFIED";

    /// <summary>Unauthorized access attempt detected.</summary>
    public const string UnauthorizedAccess = "UNAUTHORIZED_ACCESS";

    /// <summary>The physical console session (Session 1) was forcibly logged off, either by
    /// the session watchdog after a suspected hang or via manual operator override.</summary>
    public const string ConsoleSessionReset = "CONSOLE_SESSION_RESET";

    /// <summary>An internal/infrastructure error inside the Named Pipe server (e.g. a pipe
    /// listen failure, a DI handler-resolution failure, or an abrupt client disconnect
    /// surfacing as an unhandled IOException during stream disposal). Deliberately distinct
    /// from <see cref="UnauthorizedAccess"/>: none of these imply a permissions violation or a
    /// malicious actor, and auditing them as UNAUTHORIZED_ACCESS made the audit trail cry
    /// wolf on ordinary RDP-session churn. Call sites that DO detect something genuinely
    /// security-relevant (a missing "$type" discriminator, a JSON parse failure on the
    /// request envelope) log their own explicit <see cref="UnauthorizedAccess"/> entry
    /// alongside this one — this category exists only for the generic catch-all.</summary>
    public const string PipeServerError = "PIPE_SERVER_ERROR";

    /// <summary>An RDP session (never the physical console — see ConsoleSessionReset for
    /// that) was forcibly logged off after being classified SessionUnresponsive: Active,
    /// heartbeat missing/stale, AND its own client process still confirmed present in that
    /// session (ruling out "client simply isn't running") for the full escalation window.
    /// Terminal Services itself was responsive when this fired — see
    /// TerminalServicesUnresponsive for the other case, where a logoff is never attempted.</summary>
    public const string SessionUnresponsiveReset = "SESSION_UNRESPONSIVE_RESET";

    /// <summary>Terminal Services (termsrv.exe) itself did not respond to a WTS call within
    /// its time budget (see TimeBoundedWtsSessionProvider / WtsUnresponsiveException). This is
    /// a self-healing, non-terminal condition, not proof termsrv is permanently dead — but it
    /// does mean WTSLogoffSession would hang on the exact same blocked path, so recovery in
    /// this state is limited to alerting and, as an escalation, terminating the session's own
    /// user-mode processes via the termsrv-independent path (see SessionProcessService) —
    /// never WTSLogoffSession.</summary>
    public const string TerminalServicesUnresponsive = "TERMINAL_SERVICES_UNRESPONSIVE";
}
