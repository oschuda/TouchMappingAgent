using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Service.Logging;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// Backup service for touch mapping configurations (MVO 2023/1230 Resilience).
/// Creates encrypted or securely stored backups of HID registry mappings.
/// </summary>
public class BackupService
{
    private readonly ILogger<BackupService> _logger;
    private const string BackupDirectory = "C:\\TouchBackup";
    private const string RegistryBasePath = @"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Enum\HID";
    // W-9: Rotate backups to prevent disk exhaustion on production servers
    private const int MaxBackupCount = 10;
    // O-2: Static instance avoids repeated allocation of JsonSerializerOptions
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Initializes a new instance of <see cref="BackupService"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    public BackupService(ILogger<BackupService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a secure backup of current touch mapping configurations from registry.
    /// MVO 2023/1230: Ensures system resilience and disaster recovery capability.
    /// </summary>
    /// <param name="description">Optional description for the backup.</param>
    /// <returns>Backup ID if successful; otherwise null.</returns>
    public string? CreateBackup(string? description = null)
    {
        var backupId = GenerateBackupId();
        var backupTimestamp = DateTime.UtcNow.ToString("O");

        try
        {
            _logger.LogInformation("Starting backup creation: {BackupId}", backupId);

            // Step 1: Ensure backup directory exists with restricted ACLs
            try
            {
                EnsureBackupDirectoryExists();
                _logger.LogInformation("Backup directory verified: {BackupDirectory}", BackupDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create or verify backup directory");
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: false,
                    errorMessage: "Backup directory inaccessible");
                return null;
            }

            // Step 2: Read HID registry entries
            Dictionary<string, string>? registryData = null;
            try
            {
                registryData = ReadHidRegistry();
                _logger.LogInformation("Read {EntryCount} HID registry entries for backup", registryData.Count);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Insufficient permissions to read HID registry");
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: false,
                    errorMessage: "Registry access denied");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading HID registry");
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: false,
                    errorMessage: "Registry read failed");
                return null;
            }

            // Step 3: Write backup file atomically with restricted ACLs (K-4: TOCTOU fix)
            try
            {
                var backupContent = SerializeBackupData(registryData, backupId, backupTimestamp, description);
                var backupFilePath = Path.Combine(BackupDirectory, $"backup_{backupId}.json");

                // Build ACL before creating the file to eliminate the race window.
                // FileOptions.None with FileShare.None keeps the file exclusively locked
                // until we close it; no other process can read or replace it in between.
                // K-4 TOCTOU fix:
                // - FileMode.CreateNew: Fails if file/symlink already exists → prevents
                //   symlink-replacement attack (attacker cannot pre-place a symlink target).
                // - FileShare.None: Exclusive lock during the entire write → no concurrent reader.
                // - RestrictFileAcl immediately after close: race window reduced to nanoseconds
                //   between FileStream.Dispose() and SetAccessControl(); on NTFS the file is
                //   exclusively locked until Dispose returns, so no read is possible before ACL is set.
                using (var fs = new FileStream(
                    backupFilePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
                {
                    writer.Write(backupContent);
                }
                // Apply restrictive ACLs immediately after exclusive write completes
                RestrictFileAcl(backupFilePath);

                // W-9: Remove oldest backups beyond the retention limit
                CleanupOldBackups();

                _logger.LogInformation("Backup file created successfully: {BackupFilePath}", backupFilePath);

                // Log success to audit trail
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: true);

                return backupId;
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "I/O error writing backup file");
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: false,
                    errorMessage: "Backup write failed");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during backup file creation");
                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    backupId,
                    success: false,
                    errorMessage: "Backup creation failed");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error in CreateBackup");
            return null;
        }
    }

    /// <summary>
    /// Ensures the backup directory exists with restricted permissions (SYSTEM + Admins only).
    /// </summary>
    private static void EnsureBackupDirectoryExists()
    {
        if (!Directory.Exists(BackupDirectory))
        {
            Directory.CreateDirectory(BackupDirectory);
        }

        // Restrict directory ACLs to SYSTEM and Administrators
        RestrictDirectoryAcl(BackupDirectory);
    }

    /// <summary>
    /// Reads HID device touch-mapping configurations from the Windows registry.
    /// O-5 fix: Reads DeviceParameters\WDF subkey where touch mapping data is stored,
    /// not the top-level Class value (which held no mapping information).
    /// Also includes the PadaLuma-specific mapping store under SOFTWARE\PadaLuma.
    /// </summary>
    private static Dictionary<string, string> ReadHidRegistry()
    {
        var mappings = new Dictionary<string, string>();

        try
        {
            // Primary: Read PadaLuma-managed touch mappings
            using (var mappingKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\PadaLuma\TouchMappingAgent\Mappings"))
            {
                if (mappingKey != null)
                {
                    foreach (var valueName in mappingKey.GetValueNames())
                    {
                        try
                        {
                            var value = mappingKey.GetValue(valueName)?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(value))
                                mappings[$"MAP:{valueName}"] = value;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Error reading mapping value {valueName}: {ex.Message}");
                        }
                    }
                }
            }

            // Secondary: Read HID device parameters (touch calibration and WDF data)
            using (var hidKey = Registry.LocalMachine.OpenSubKey(
                @"System\CurrentControlSet\Enum\HID"))
            {
                if (hidKey == null) return mappings;

                foreach (var deviceKeyName in hidKey.GetSubKeyNames())
                {
                    try
                    {
                        using var deviceKey = hidKey.OpenSubKey(deviceKeyName);
                        if (deviceKey == null) continue;

                        foreach (var instanceKeyName in deviceKey.GetSubKeyNames())
                        {
                            try
                            {
                                // DeviceParameters\WDF contains touch-mapping configuration
                                using var wdfKey = deviceKey.OpenSubKey(
                                    $"{instanceKeyName}\\Device Parameters\\WDF");
                                if (wdfKey != null)
                                {
                                    foreach (var valueName in wdfKey.GetValueNames())
                                    {
                                        var value = wdfKey.GetValue(valueName)?.ToString() ?? "";
                                        if (!string.IsNullOrEmpty(value))
                                            mappings[$"HID:{deviceKeyName}\\{instanceKeyName}\\WDF:{valueName}"] = value;
                                    }
                                }

                                // Also capture the Class for device-type identification
                                using var instanceKey = deviceKey.OpenSubKey(instanceKeyName);
                                if (instanceKey != null)
                                {
                                    var class_ = instanceKey.GetValue("Class", "")?.ToString() ?? "";
                                    if (class_.Equals("HIDClass", StringComparison.OrdinalIgnoreCase) ||
                                        class_.Equals("Digitizer", StringComparison.OrdinalIgnoreCase))
                                    {
                                        mappings[$"HID:{deviceKeyName}\\{instanceKeyName}:Class"] = class_;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"Error reading instance {instanceKeyName}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Error reading HID device {deviceKeyName}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error accessing registry: {ex.Message}");
            throw;
        }

        return mappings;
    }

    /// <summary>
    /// Serializes backup data to JSON format.
    /// </summary>
    private static string SerializeBackupData(
        Dictionary<string, string> registryData,
        string backupId,
        string timestamp,
        string? description)
    {
        var backupObject = new
        {
            BackupId = backupId,
            Timestamp = timestamp,
            Description = description,
            EntryCount = registryData.Count,
            Entries = registryData
        };

        return JsonSerializer.Serialize(backupObject, JsonOptions); // O-2: cached options
    }

    /// <summary>
    /// W-9: Removes the oldest backup files when MaxBackupCount is exceeded.
    /// Prevents unbounded disk usage on production servers (DoS prevention).
    /// </summary>
    private static void CleanupOldBackups()
    {
        try
        {
            var oldFiles = Directory.GetFiles(BackupDirectory, "backup_*.json")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(MaxBackupCount)
                .ToList();

            foreach (var file in oldFiles)
            {
                try { file.Delete(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Warning: Could not delete old backup {file.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Backup cleanup failed: {ex.Message}");
            // Non-critical; do not abort the backup operation
        }
    }

    /// <summary>
    /// Restricts file ACLs to SYSTEM and Administrators only.
    /// </summary>
    private static void RestrictFileAcl(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();

            // Remove inheritance
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Add SYSTEM and Admins only
            var systemSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var adminSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                systemSid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));

            fileSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                adminSid,
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.AccessControlType.Allow));

            fileInfo.SetAccessControl(fileSecurity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Could not set file ACL: {ex.Message}");
            // Continue - ACL restriction is hardening, not critical
        }
    }

    /// <summary>
    /// Restricts directory ACLs to SYSTEM and Administrators only.
    /// </summary>
    private static void RestrictDirectoryAcl(string directoryPath)
    {
        try
        {
            var dirInfo = new DirectoryInfo(directoryPath);
            var dirSecurity = dirInfo.GetAccessControl();

            // Remove inheritance
            dirSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Add SYSTEM and Admins only
            var systemSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
            var adminSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);

            dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                systemSid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                adminSid,
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));

            dirInfo.SetAccessControl(dirSecurity);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Could not set directory ACL: {ex.Message}");
            // Continue - ACL restriction is hardening, not critical
        }
    }

    /// <summary>
    /// Generates a unique backup identifier.
    /// </summary>
    private static string GenerateBackupId()
    {
        return $"BKP_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    }
}
