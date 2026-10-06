using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Evolved.EdidManager.Registry;

/// <summary>
/// Reads the hardware EDID of a monitor and manages its EDID override.
/// </summary>
public interface IEdidRegistryStore
{
    /// <summary>Reads the EDID the hardware reported, or null when the monitor has none.</summary>
    byte[]? ReadHardwareEdid(string instanceId);

    /// <summary>Reads the currently installed override, or null when none is installed.</summary>
    byte[]? ReadOverride(string instanceId);

    /// <summary>Installs an override. Returns true on success.</summary>
    bool WriteOverride(string instanceId, byte[] edid);

    /// <summary>Removes any installed override. Returns true when one was removed.</summary>
    bool RemoveOverride(string instanceId);

    /// <summary>Enumerates the instance ids of all monitors that expose an EDID.</summary>
    IReadOnlyList<string> EnumerateMonitorInstanceIds();
}

/// <summary>
/// Registry-backed implementation.
///
/// ON THE OVERRIDE LAYOUT — this differs from the obvious reading of "write the bytes as a
/// binary value named EDID_OVERRIDE", and the difference decides whether the feature works at
/// all. Windows' monitor class installer reads the override from a SUBKEY whose values are
/// numbered per EDID block:
///
///   HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\Device Parameters\EDID_OVERRIDE
///       "0" = REG_BINARY   block 0
///       "1" = REG_BINARY   first extension block, when present
///
/// This is the shape a monitor INF produces via "HKR, EDID_OVERRIDE, 0, 1, &lt;bytes&gt;".
/// A flat REG_BINARY value called EDID_OVERRIDE sitting next to EDID is read by nobody: the
/// write succeeds, the log says "override installed", and the monitor keeps reporting its
/// factory serial. Writing the documented shape is what makes the override take effect.
/// </summary>
public sealed class EdidRegistryStore : IEdidRegistryStore
{
    /// <summary>Root of the PnP enumeration tree.</summary>
    public const string EnumRootPath = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>Value name holding the EDID the hardware reported.</summary>
    public const string HardwareEdidValueName = "EDID";

    /// <summary>Subkey name holding the override blocks.</summary>
    public const string OverrideKeyName = "EDID_OVERRIDE";

    /// <summary>Enumerator branch monitors live under.</summary>
    public const string MonitorEnumerator = "DISPLAY";

    private readonly ILogger<EdidRegistryStore> _logger;
    private readonly RegistryKey _root;

    /// <summary>Initializes a new instance backed by HKEY_LOCAL_MACHINE.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public EdidRegistryStore(ILogger<EdidRegistryStore> logger)
        : this(logger, Microsoft.Win32.Registry.LocalMachine)
    {
    }

    /// <summary>
    /// Testing seam: lets a test point the store at a writable hive instead of HKLM, which a
    /// non-elevated process cannot write.
    /// </summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="root">Hive the enumeration tree lives under.</param>
    public EdidRegistryStore(ILogger<EdidRegistryStore> logger, RegistryKey root)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <inheritdoc/>
    public byte[]? ReadHardwareEdid(string instanceId)
    {
        if (!Pnp.PnpDeviceInstanceId.IsValidInstanceId(instanceId))
            return null;

        try
        {
            using var key = _root.OpenSubKey(DeviceParametersPath(instanceId));
            return key?.GetValue(HardwareEdidValueName) as byte[];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read hardware EDID for {InstanceId}", instanceId);
            return null;
        }
    }

    /// <inheritdoc/>
    public byte[]? ReadOverride(string instanceId)
    {
        if (!Pnp.PnpDeviceInstanceId.IsValidInstanceId(instanceId))
            return null;

        try
        {
            using var key = _root.OpenSubKey($@"{DeviceParametersPath(instanceId)}\{OverrideKeyName}");
            if (key == null)
                return null;

            // Blocks are numbered "0", "1", ... and must be concatenated in order.
            var blocks = new List<byte>();
            for (int index = 0; ; index++)
            {
                if (key.GetValue(index.ToString()) is not byte[] block)
                    break;

                blocks.AddRange(block);
            }

            return blocks.Count > 0 ? blocks.ToArray() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read EDID override for {InstanceId}", instanceId);
            return null;
        }
    }

    /// <inheritdoc/>
    public bool WriteOverride(string instanceId, byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);

        if (!Pnp.PnpDeviceInstanceId.IsValidInstanceId(instanceId))
        {
            _logger.LogWarning("Refusing to write an override for malformed instance id {InstanceId}", instanceId);
            return false;
        }

        if (edid.Length % Edid.EdidBlock.BlockSize != 0 || edid.Length == 0)
        {
            _logger.LogError(
                "Refusing to write an override of {Length} bytes for {InstanceId}: " +
                "must be a whole number of {BlockSize}-byte blocks",
                edid.Length, instanceId, Edid.EdidBlock.BlockSize);
            return false;
        }

        try
        {
            using var deviceParameters = _root.OpenSubKey(DeviceParametersPath(instanceId), writable: true);
            if (deviceParameters == null)
            {
                _logger.LogError(
                    "Device Parameters key not found for {InstanceId}; is the monitor still present?",
                    instanceId);
                return false;
            }

            // Recreate rather than merge: a stale higher-numbered block left over from a
            // previous override would silently be appended to the new one.
            deviceParameters.DeleteSubKeyTree(OverrideKeyName, throwOnMissingSubKey: false);

            using var overrideKey = deviceParameters.CreateSubKey(OverrideKeyName, writable: true);
            if (overrideKey == null)
            {
                _logger.LogError("Could not create {OverrideKey} for {InstanceId}", OverrideKeyName, instanceId);
                return false;
            }

            int blockCount = edid.Length / Edid.EdidBlock.BlockSize;
            for (int index = 0; index < blockCount; index++)
            {
                var block = new byte[Edid.EdidBlock.BlockSize];
                Array.Copy(edid, index * Edid.EdidBlock.BlockSize, block, 0, Edid.EdidBlock.BlockSize);
                overrideKey.SetValue(index.ToString(), block, RegistryValueKind.Binary);
            }

            _logger.LogInformation(
                "Installed EDID override for {InstanceId} ({BlockCount} block(s), {Length} bytes)",
                instanceId, blockCount, edid.Length);

            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "Insufficient permissions to write the EDID override for {InstanceId}. " +
                "Writing under HKLM\\SYSTEM\\CurrentControlSet\\Enum requires elevation.",
                instanceId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write the EDID override for {InstanceId}", instanceId);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool RemoveOverride(string instanceId)
    {
        if (!Pnp.PnpDeviceInstanceId.IsValidInstanceId(instanceId))
            return false;

        try
        {
            using var deviceParameters = _root.OpenSubKey(DeviceParametersPath(instanceId), writable: true);
            if (deviceParameters == null)
                return false;

            if (!deviceParameters.GetSubKeyNames().Contains(OverrideKeyName, StringComparer.OrdinalIgnoreCase))
                return false;

            deviceParameters.DeleteSubKeyTree(OverrideKeyName, throwOnMissingSubKey: false);
            _logger.LogInformation("Removed EDID override for {InstanceId}", instanceId);
            return true;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex,
                "Insufficient permissions to remove the EDID override for {InstanceId}", instanceId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove the EDID override for {InstanceId}", instanceId);
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> EnumerateMonitorInstanceIds()
    {
        var results = new List<string>();

        try
        {
            using var displayKey = _root.OpenSubKey($@"{EnumRootPath}\{MonitorEnumerator}");
            if (displayKey == null)
                return results;

            foreach (var hardwareId in displayKey.GetSubKeyNames())
            {
                try
                {
                    using var hardwareKey = displayKey.OpenSubKey(hardwareId);
                    if (hardwareKey == null)
                        continue;

                    foreach (var instance in hardwareKey.GetSubKeyNames())
                    {
                        var instanceId = $@"{MonitorEnumerator}\{hardwareId}\{instance}";

                        // Only monitors that actually report an EDID are candidates. The
                        // "Default_Monitor" nodes Windows keeps around for past connections
                        // have no EDID and nothing to de-collide.
                        if (ReadHardwareEdid(instanceId) != null)
                            results.Add(instanceId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Skipping unreadable monitor hardware id {HardwareId}", hardwareId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not enumerate monitors under {EnumRoot}", EnumRootPath);
        }

        return results;
    }

    private static string DeviceParametersPath(string instanceId) =>
        $@"{EnumRootPath}\{Pnp.PnpDeviceInstanceId.DeviceParametersSubPath(instanceId)}";
}
