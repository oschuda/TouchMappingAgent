using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Evolved.EdidManager.Pnp;

/// <summary>
/// Triggers a PnP re-enumeration of a device node so Windows re-reads its configuration.
/// </summary>
public interface IPnpDeviceControl
{
    /// <summary>
    /// Asks the PnP manager to re-enumerate the given device node.
    /// </summary>
    /// <param name="instanceId">Device instance id, e.g. "DISPLAY\CHR8910\5&amp;...".</param>
    /// <returns>True when the PnP manager accepted the request.</returns>
    bool ReenumerateDevice(string instanceId);
}

/// <summary>
/// CfgMgr32-based implementation.
///
/// SCOPE OF WHAT THIS ACHIEVES — worth being precise about, because it is easy to over-promise:
/// re-enumeration asks the PnP manager to re-evaluate the node, which is what makes Windows
/// pick up a freshly written EDID_OVERRIDE without a reboot in the common case. It is NOT
/// guaranteed for every display driver: some read the EDID once when the display adapter
/// initialises and only refresh on a real topology change (a hot-plug, a mode set, or a
/// driver restart). Callers must therefore treat a successful return as "the request was
/// accepted", not as "every consumer now sees the new serial", and verify by re-reading.
/// </summary>
public sealed class PnpDeviceControl : IPnpDeviceControl
{
    private const int CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x00000000;
    private const uint CM_REENUMERATE_SYNCHRONOUS = 0x00000001;

    private readonly ILogger<PnpDeviceControl> _logger;

    /// <summary>Initializes a new instance of <see cref="PnpDeviceControl"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PnpDeviceControl(ILogger<PnpDeviceControl> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public bool ReenumerateDevice(string instanceId)
    {
        if (!PnpDeviceInstanceId.IsValidInstanceId(instanceId))
        {
            _logger.LogWarning("Refusing to re-enumerate malformed instance id {InstanceId}", instanceId);
            return false;
        }

        try
        {
            int locateResult = CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL);
            if (locateResult != CR_SUCCESS)
            {
                _logger.LogWarning(
                    "CM_Locate_DevNode failed for {InstanceId} with CR code {ConfigRet}",
                    instanceId, locateResult);
                return false;
            }

            int reenumResult = CM_Reenumerate_DevNode(devInst, CM_REENUMERATE_SYNCHRONOUS);
            if (reenumResult != CR_SUCCESS)
            {
                _logger.LogWarning(
                    "CM_Reenumerate_DevNode failed for {InstanceId} with CR code {ConfigRet}",
                    instanceId, reenumResult);
                return false;
            }

            _logger.LogInformation("Re-enumerated device node {InstanceId}", instanceId);
            return true;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogError(ex, "cfgmgr32.dll not available; cannot re-enumerate {InstanceId}", instanceId);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            _logger.LogError(ex, "CfgMgr32 entry point missing; cannot re-enumerate {InstanceId}", instanceId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error re-enumerating {InstanceId}", instanceId);
            return false;
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    private static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);
}
