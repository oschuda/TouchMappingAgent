using System.Runtime.Versioning;
using System.Security.Principal;

namespace Evolved.EdidManager;

/// <summary>
/// Determines whether the current process may write under
/// HKLM\SYSTEM\CurrentControlSet\Enum.
/// </summary>
public interface IElevationCheck
{
    /// <summary>True when the process runs with administrative privileges.</summary>
    bool IsElevated { get; }
}

/// <summary>
/// Windows implementation via the process token.
///
/// Checked up front rather than relying on the write to fail: a non-elevated run would
/// otherwise get through enumeration, collision detection and mutation before dying on the
/// first registry write, having already logged a plan it cannot carry out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevationCheck : IElevationCheck
{
    /// <inheritdoc/>
    public bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                // If the token cannot be read, assume the worst and refuse to write.
                return false;
            }
        }
    }
}
