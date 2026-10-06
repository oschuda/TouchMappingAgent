using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Service.Logging;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// Advanced repair service for touch and display system recovery (IEC 62443 / NIS2 compliance).
/// Handles driver conflicts, RDP issues, and HID remapping recovery.
/// </summary>
public class AdvancedRepairService
{
    private readonly ILogger<AdvancedRepairService> _logger;
    private readonly DisplayRefreshService _displayRefreshService;

    /// <summary>Initializes a new instance of <see cref="AdvancedRepairService"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="displayRefreshService">Service for broadcasting display-refresh messages.</param>
    public AdvancedRepairService(ILogger<AdvancedRepairService> logger, DisplayRefreshService displayRefreshService)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _displayRefreshService = displayRefreshService ?? throw new ArgumentNullException(nameof(displayRefreshService));
    }

    /// <summary>
    /// Executes the advanced repair sequence for touch and display systems.
    /// Operates in isolated phases to ensure partial success (MVO 2023/1230).
    /// IEC 62443 / NIS2: Comprehensive recovery after driver failures or RDP conflicts.
    /// </summary>
    /// <returns>Overall success status and recovery details.</returns>
    public async Task<(bool Success, string RecoveryDetails)> ExecuteAdvancedRepairAsync(CancellationToken cancellationToken = default)
    {
        var repairId = GenerateRepairId();
        var recoveryLog = new System.Text.StringBuilder();

        _logger.LogInformation("Advanced repair sequence started: {RepairId}", repairId);
        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.StartAdvancedRepair,
            repairId,
            success: false,
            errorMessage: "Repair started");

        bool overallSuccess = true;

        // Phase 1: Stop RDP and blocking processes
        try
        {
            _logger.LogInformation("Phase 1: Checking for RDP and blocking processes");
            await StopRdpConflictsAsync();
            recoveryLog.AppendLine("✓ Phase 1: RDP/blocking processes checked.");
            _logger.LogInformation("Phase 1 completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 1 (RDP check) encountered an issue");
            recoveryLog.AppendLine($"⚠ Phase 1: {ex.Message}");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.StartAdvancedRepair,
                repairId,
                success: false,
                errorMessage: "RDP check failed");
            // Do NOT return - continue to next phase (MVO: partial success)
        }

        // Phase 2: Reset HID registry mappings.
        //
        // This used to also invoke "tabcal.exe ClearCal" as a "global calibration reset" for
        // Windows Server 2022+. That argument is not a documented tabcal.exe switch (compare the
        // working, tested invocation in ReapplyCoordinator/ComplianceRequestHandler, which uses
        // "LinCal DisplayID=... DeviceKind=... DevicePath=... NoValidate"). In practice it fell
        // through to tabcal.exe's interactive calibration UI, which cannot render in this
        // service's Session 0 — the call hung for the full 30s watchdog and was then
        // force-killed (Kill(entireProcessTree: true)) mid-operation. On at least one field
        // installation (two Iiyama T2452MTS optical touch frames) this left both digitizers
        // permanently unresponsive to touch — surviving a full OS reboot, USB replug, and a
        // monitor mains power cycle — consistent with the kill having corrupted calibration data
        // held in the touch controllers' own non-volatile storage, not anything Windows or this
        // PC holds. Recovering from that needed the vendor's own reset procedure; nothing on the
        // PC side could undo it. Do not reintroduce a "global" tabcal reset here — per-device
        // calibration already exists and is safe: it goes through the interactive client's LinCal
        // path (see ComplianceRequestHandler.ProcessMappingAsync), which runs in the user's
        // session where tabcal.exe can actually show its UI.
        try
        {
            _logger.LogInformation("Phase 2: Resetting HID registry");
            ResetHidRegistry();
            recoveryLog.AppendLine("✓ Phase 2: HID registry reset completed.");
            _logger.LogInformation("Phase 2 completed successfully");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Phase 2: Insufficient permissions for registry modification");
            recoveryLog.AppendLine($"⚠ Phase 2: Registry access denied");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.StartAdvancedRepair,
                repairId,
                success: false,
                errorMessage: "Registry access denied");
            overallSuccess = false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phase 2: HID reset or calibration failed");
            recoveryLog.AppendLine($"⚠ Phase 2: {ex.Message}");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.StartAdvancedRepair,
                repairId,
                success: false,
                errorMessage: "HID reset failed");
            // Continue to Phase 3 (MVO: partial success)
        }

        // Phase 3: Safe display refresh (WM_SETTINGCHANGE broadcast)
        try
        {
            _logger.LogInformation("Phase 3: Invoking safe display refresh");
            _displayRefreshService.InvokeSafeRefresh();
            recoveryLog.AppendLine("✓ Phase 3: Display refresh broadcast sent.");
            _logger.LogInformation("Phase 3 completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phase 3: Display refresh failed");
            recoveryLog.AppendLine($"⚠ Phase 3: {ex.Message}");
            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.StartAdvancedRepair,
                repairId,
                success: false,
                errorMessage: "Display refresh failed");
            // Do NOT fail entire repair - display refresh is important but not critical
        }

        // Log final result
        var finalStatus = overallSuccess ? "SUCCESS" : "PARTIAL_SUCCESS";
        _logger.LogInformation("Advanced repair sequence completed: {Status} (RepairId: {RepairId})", finalStatus, repairId);
        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.StartAdvancedRepair,
            repairId,
            success: overallSuccess,
            errorMessage: recoveryLog.ToString());

        return (overallSuccess, recoveryLog.ToString());
    }

    /// <summary>
    /// Stops or suspends RDP sessions and identifies blocking processes.
    /// </summary>
    private async Task StopRdpConflictsAsync()
    {
        try
        {
            // Check if RDP is running
            var rdpProcesses = Process.GetProcessesByName("mstsc");
            if (rdpProcesses.Length > 0)
            {
                _logger.LogWarning("Found {RdpProcessCount} active RDP sessions", rdpProcesses.Length);

                foreach (var proc in rdpProcesses)
                {
                    try
                    {
                        _logger.LogInformation("Closing RDP process: {ProcessId}", proc.Id);
                        // Graceful shutdown
                        proc.CloseMainWindow();
                        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            try
                            {
                                await proc.WaitForExitAsync(cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                                // Timeout - force kill
                                if (!proc.HasExited)
                                {
                                    proc.Kill();
                                    await proc.WaitForExitAsync();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not close RDP process {ProcessId}", proc.Id);
                    }
                }
            }

            // Check for other device access conflicts (e.g., other HID consumers)
            var hidBlockers = Process.GetProcessesByName("explorer"); // Simplified check
            if (hidBlockers.Length > 0)
            {
                _logger.LogInformation("Found potential HID blocking processes");
                // Do not forcefully kill explorer - just log and continue
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for RDP conflicts");
            throw;
        }
    }

    /// <summary>
    /// Resets HID device registry mappings to default state.
    /// </summary>
    private void ResetHidRegistry()
    {
        try
        {
            // Open HID registry
            using (var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Enum\HID", writable: true))
            {
                if (key == null)
                {
                    _logger.LogWarning("HID registry path not found");
                    return;
                }

                var subKeyNames = key.GetSubKeyNames().ToList();
                _logger.LogInformation("Found {DeviceCount} HID devices for potential reset", subKeyNames.Count);

                foreach (var subKeyName in subKeyNames)
                {
                    try
                    {
                        // Reset device-specific mappings (careful: only reset touch-related flags)
                        using (var subKey = key.OpenSubKey(subKeyName, writable: true))
                        {
                            if (subKey != null)
                            {
                                // Remove any custom touch mapping flags (if present)
                                var values = subKey.GetValueNames();
                                if (values.Contains("TouchMapping"))
                                {
                                    subKey.DeleteValue("TouchMapping", throwOnMissingValue: false);
                                    _logger.LogInformation("Cleared TouchMapping flag for {DeviceId}", subKeyName);
                                }
                            }
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // W-4 fix: skip locked key, do not abort the entire loop (MVO partial-success)
                        _logger.LogWarning(ex, "Cannot access registry key {DeviceId} – skipping", subKeyName);
                        // Continue with next device
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing registry key {DeviceId}", subKeyName);
                        // Continue with next device
                    }
                }
            }

            _logger.LogInformation("HID registry reset completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error resetting HID registry");
            throw;
        }
    }

    /// <summary>
    /// Generates a unique repair identifier.
    /// </summary>
    private static string GenerateRepairId()
    {
        return $"RPR_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}"; // O-3: range operator
    }
}
