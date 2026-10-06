using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Services;

/// <summary>
/// Persists touch-to-monitor assignments under
/// HKLM\SOFTWARE\PadaLuma\TouchMappingAgent\Mappings.
///
/// LAYOUT CHANGE FROM PHASE 1: the old store was a flat set of values where the value NAME was
/// the HID interface path and the value DATA was "\\.\DISPLAYn". Both are re-issued by Windows
/// on every boot, so the record could silently come to mean a different pair of devices after
/// a restart — and nothing ever read it back anyway.
///
/// Now each mapping is its own subkey, named after the digitizer's USB parent instance (its
/// physical port) with backslashes escaped, holding the monitor's hardware anchors as values.
/// Subkeys rather than values because a mapping is now a record, not a single string.
///
/// Old flat values found under the key are migrated away on first write: they cannot be
/// upgraded (the anchors they would need were never captured) so they are removed and logged,
/// which surfaces as "these assignments must be re-learned" rather than as silent breakage.
/// </summary>
public class MappingStore
{
    /// <summary>Registry path (under HKLM) holding all persisted mappings.</summary>
    public const string MappingKeyPath = @"SOFTWARE\PadaLuma\TouchMappingAgent\Mappings";

    private readonly ILogger<MappingStore> _logger;
    private readonly RegistryKey _rootKey;
    private readonly object _writeLock = new();

    /// <summary>Initializes a new instance of <see cref="MappingStore"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    public MappingStore(ILogger<MappingStore> logger)
        : this(logger, Registry.LocalMachine)
    {
    }

    /// <summary>
    /// Testing seam: lets a test point the store at a writable key (e.g. under HKCU) instead
    /// of HKLM, which a non-elevated test process cannot write.
    /// </summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="rootKey">Registry hive the mapping key lives under.</param>
    public MappingStore(ILogger<MappingStore> logger, RegistryKey rootKey)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootKey = rootKey ?? throw new ArgumentNullException(nameof(rootKey));
    }

    /// <summary>
    /// Reads every persisted mapping. Never throws: an unreadable individual entry is skipped
    /// so one corrupt record cannot hide the rest (MVO 2023/1230 partial-success principle).
    /// </summary>
    public List<TouchMapping> GetAll()
    {
        var mappings = new List<TouchMapping>();

        try
        {
            using var key = _rootKey.OpenSubKey(MappingKeyPath);
            if (key == null)
                return mappings;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                try
                {
                    using var entry = key.OpenSubKey(subKeyName);
                    if (entry == null)
                        continue;

                    var mapping = ReadMapping(UnescapeKeyName(subKeyName), entry);
                    if (mapping != null)
                        mappings.Add(mapping);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Skipping unreadable mapping entry {MappingKey}", subKeyName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read mappings from {MappingKeyPath}", MappingKeyPath);
        }

        return mappings;
    }

    /// <summary>
    /// Writes (or replaces) one mapping, keyed on its touch hardware anchor.
    /// </summary>
    /// <returns>True when the mapping was persisted.</returns>
    public bool Save(TouchMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (string.IsNullOrWhiteSpace(mapping.TouchHardwareKey))
        {
            _logger.LogWarning(
                "Refusing to persist a mapping without a touch hardware anchor: " +
                "it could never be resolved back to a device");
            return false;
        }

        lock (_writeLock)
        {
            try
            {
                using var key = _rootKey.CreateSubKey(MappingKeyPath, writable: true);
                if (key == null)
                {
                    _logger.LogError("Cannot open or create registry key {MappingKeyPath}", MappingKeyPath);
                    return false;
                }

                RemoveLegacyFlatValues(key);

                var subKeyName = EscapeKeyName(mapping.TouchHardwareKey);

                if (HidDeviceInfo.IsPortKey(mapping.TouchHardwareKey))
                    RemoveSupersededInstanceAnchoredEntries(key, subKeyName, mapping.MonitorHardwareKey);
                using var entry = key.CreateSubKey(subKeyName, writable: true);
                if (entry == null)
                {
                    _logger.LogError("Cannot create mapping entry {MappingKey}", subKeyName);
                    return false;
                }

                WriteMapping(entry, mapping);

                _logger.LogInformation(
                    "Persisted mapping {TouchKey} -> {MonitorConnector} ({MonitorKey})",
                    mapping.TouchHardwareKey, mapping.MonitorConnectorLabel, mapping.MonitorHardwareKey);

                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    $"{mapping.TouchHardwareKey}=>{mapping.MonitorConnectorLabel}",
                    success: true);

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex,
                    "Insufficient permissions to write {MappingKeyPath}. The service must run as SYSTEM.",
                    MappingKeyPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist mapping for {TouchKey}", mapping.TouchHardwareKey);
                return false;
            }
        }
    }

    /// <summary>
    /// Removes a persisted mapping by its touch hardware anchor.
    /// </summary>
    /// <returns>True when an entry was removed.</returns>
    public bool Delete(string touchHardwareKey)
    {
        if (string.IsNullOrWhiteSpace(touchHardwareKey))
            return false;

        lock (_writeLock)
        {
            try
            {
                using var key = _rootKey.OpenSubKey(MappingKeyPath, writable: true);
                if (key == null)
                    return false;

                var subKeyName = EscapeKeyName(touchHardwareKey);
                if (!key.GetSubKeyNames().Contains(subKeyName, StringComparer.OrdinalIgnoreCase))
                    return false;

                key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);

                _logger.LogInformation("Removed mapping {TouchKey}", touchHardwareKey);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove mapping for {TouchKey}", touchHardwareKey);
                return false;
            }
        }
    }

    /// <summary>
    /// Records that a mapping was successfully re-applied, for operator diagnostics.
    /// Best-effort: a failure here must not fail the re-application itself.
    /// </summary>
    public void MarkApplied(string touchHardwareKey, DateTime appliedUtc)
    {
        if (string.IsNullOrWhiteSpace(touchHardwareKey))
            return;

        lock (_writeLock)
        {
            try
            {
                using var key = _rootKey.OpenSubKey(MappingKeyPath, writable: true);
                using var entry = key?.OpenSubKey(EscapeKeyName(touchHardwareKey), writable: true);
                entry?.SetValue(
                    TouchMapping.ValueNames.LastAppliedUtc,
                    appliedUtc.ToString("O"),
                    RegistryValueKind.String);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not stamp last-applied time for {TouchKey}", touchHardwareKey);
            }
        }
    }

    private static TouchMapping? ReadMapping(string touchHardwareKey, RegistryKey entry)
    {
        var monitorHardwareKey = ReadString(entry, TouchMapping.ValueNames.MonitorHardwareKey);
        var connectorLabel = ReadString(entry, TouchMapping.ValueNames.MonitorConnectorLabel);
        var boundsKey = ReadString(entry, TouchMapping.ValueNames.MonitorBoundsKey);

        // A record with no monitor anchor at all can never be resolved; treat as corrupt.
        if (monitorHardwareKey.Length == 0 && connectorLabel.Length == 0 && boundsKey.Length == 0)
            return null;

        uint targetId = 0;
        var rawTargetId = ReadString(entry, TouchMapping.ValueNames.MonitorTargetId);
        if (rawTargetId.Length > 0)
            uint.TryParse(rawTargetId, out targetId);

        return new TouchMapping(
            TouchHardwareKey: touchHardwareKey,
            MonitorHardwareKey: monitorHardwareKey,
            MonitorConnectorLabel: connectorLabel,
            MonitorTargetId: targetId,
            MonitorBoundsKey: boundsKey,
            TouchProductName: ReadString(entry, TouchMapping.ValueNames.TouchProductName),
            MonitorFriendlyName: ReadString(entry, TouchMapping.ValueNames.MonitorFriendlyName),
            LastKnownTouchDevicePath: ReadString(entry, TouchMapping.ValueNames.LastKnownTouchDevicePath),
            LastKnownMonitorDeviceId: ReadString(entry, TouchMapping.ValueNames.LastKnownMonitorDeviceId),
            LearnedUtc: ReadString(entry, TouchMapping.ValueNames.LearnedUtc),
            LastAppliedUtc: ReadString(entry, TouchMapping.ValueNames.LastAppliedUtc));
    }

    private static void WriteMapping(RegistryKey entry, TouchMapping mapping)
    {
        SetString(entry, TouchMapping.ValueNames.MonitorHardwareKey, mapping.MonitorHardwareKey);
        SetString(entry, TouchMapping.ValueNames.MonitorConnectorLabel, mapping.MonitorConnectorLabel);
        SetString(entry, TouchMapping.ValueNames.MonitorTargetId, mapping.MonitorTargetId.ToString());
        SetString(entry, TouchMapping.ValueNames.MonitorBoundsKey, mapping.MonitorBoundsKey);
        SetString(entry, TouchMapping.ValueNames.TouchProductName, mapping.TouchProductName);
        SetString(entry, TouchMapping.ValueNames.MonitorFriendlyName, mapping.MonitorFriendlyName);
        SetString(entry, TouchMapping.ValueNames.LastKnownTouchDevicePath, mapping.LastKnownTouchDevicePath);
        SetString(entry, TouchMapping.ValueNames.LastKnownMonitorDeviceId, mapping.LastKnownMonitorDeviceId);
        SetString(entry, TouchMapping.ValueNames.LearnedUtc,
            mapping.LearnedUtc.Length > 0 ? mapping.LearnedUtc : DateTime.UtcNow.ToString("O"));
        SetString(entry, TouchMapping.ValueNames.LastAppliedUtc, mapping.LastAppliedUtc);
    }

    /// <summary>
    /// Deletes leftovers from the phase-1 flat layout (value name = HID path,
    /// data = "\\.\DISPLAYn"). They cannot be upgraded because the hardware anchors were never
    /// recorded, so removing them turns an invisible stale record into a visible "re-learn me".
    /// </summary>
    private void RemoveLegacyFlatValues(RegistryKey key)
    {
        foreach (var valueName in key.GetValueNames())
        {
            if (valueName.Length == 0)
                continue;

            try
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
                _logger.LogWarning(
                    "Removed legacy flat mapping value {LegacyValue}. It was keyed on volatile " +
                    "identifiers and must be re-learned by touching the target screen.",
                    valueName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove legacy mapping value {LegacyValue}", valueName);
            }
        }
    }

    /// <summary>
    /// Re-learning a screen with a port anchor replaces the entry that the former USB-instance
    /// anchor left for the same monitor. Without this the old entry would linger forever:
    /// it no longer resolves (see ReapplyCoordinator) but would still count as an unresolvable
    /// assignment in every status report.
    /// </summary>
    private void RemoveSupersededInstanceAnchoredEntries(
        RegistryKey key, string newSubKeyName, string monitorHardwareKey)
    {
        if (string.IsNullOrWhiteSpace(monitorHardwareKey))
            return;

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            if (string.Equals(subKeyName, newSubKeyName, StringComparison.OrdinalIgnoreCase) ||
                HidDeviceInfo.IsPortKey(UnescapeKeyName(subKeyName)))
            {
                continue;
            }

            try
            {
                string storedMonitor;
                using (var entry = key.OpenSubKey(subKeyName))
                    storedMonitor = entry == null ? string.Empty : ReadString(entry, TouchMapping.ValueNames.MonitorHardwareKey);

                if (!string.Equals(storedMonitor, monitorHardwareKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                key.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
                _logger.LogInformation(
                    "Replaced instance-anchored mapping {LegacyKey} for monitor {MonitorKey} with a port-anchored one",
                    UnescapeKeyName(subKeyName), monitorHardwareKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove superseded mapping {LegacyKey}", subKeyName);
            }
        }
    }

    private static string ReadString(RegistryKey entry, string valueName) =>
        entry.GetValue(valueName) as string ?? string.Empty;

    private static void SetString(RegistryKey entry, string valueName, string value) =>
        entry.SetValue(valueName, value ?? string.Empty, RegistryValueKind.String);

    /// <summary>
    /// A PnP instance id contains backslashes, which are subkey separators in the registry.
    /// Escaping them keeps one mapping in one subkey instead of silently creating a nested
    /// tree ("USB" -> "VID_14E1&amp;PID_3508" -> "7&amp;1b9afb93&amp;0&amp;6").
    /// </summary>
    internal static string EscapeKeyName(string hardwareKey) =>
        HidDeviceInfo.NormalizeInstanceId(hardwareKey).Replace('\\', '#');

    /// <summary>Reverses <see cref="EscapeKeyName"/>.</summary>
    internal static string UnescapeKeyName(string subKeyName) =>
        subKeyName.Replace('#', '\\');
}
