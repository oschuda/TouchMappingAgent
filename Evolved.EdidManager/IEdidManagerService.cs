using Evolved.EdidManager.Edid;

namespace Evolved.EdidManager;

/// <summary>
/// Manages monitor EDID overrides: resolving serial collisions between identical panels, and
/// substituting a template where a range extender delivers no usable EDID at all.
/// </summary>
public interface IEdidManagerService
{
    /// <summary>Reads the EDID of every monitor that reports one.</summary>
    IReadOnlyList<MonitorEdid> ReadAllMonitors();

    /// <summary>Reports which monitors share an EDID identity, without changing anything.</summary>
    IReadOnlyList<CollisionPlanEntry> Analyze();

    /// <summary>Gives every colliding monitor a unique serial number.</summary>
    /// <param name="dryRun">When true, computes and logs the plan but writes nothing.</param>
    EdidOperationResult ResolveCollisions(bool dryRun = false);

    /// <summary>
    /// Installs a template EDID on one port, stamped with the given serial.
    ///
    /// This is the answer to an extender that passes no DDC through, or passes a broken dummy
    /// block: there is no hardware EDID to clone, so a known-good block for the panel behind
    /// the extender is substituted instead.
    /// </summary>
    /// <param name="pnpDeviceInstanceId">
    /// Device instance id or device interface path of the target monitor.
    /// </param>
    /// <param name="templateEdid">The template, which must be a valid EDID.</param>
    /// <param name="customSerial">
    /// Serial to stamp into the clone. A purely numeric value is written to bytes 12-15 and to
    /// the 0xFF text descriptor; any other string is written as text, with a stable numeric
    /// serial derived from it.
    /// </param>
    EdidOperationResult ApplyCustomTemplate(string pnpDeviceInstanceId, byte[] templateEdid, string customSerial);

    /// <summary>Removes the EDID override from one monitor.</summary>
    EdidOperationResult RestoreDefaults(string pnpId);

    /// <summary>Removes every EDID override on the machine.</summary>
    EdidOperationResult RestoreAllDefaults();
}
