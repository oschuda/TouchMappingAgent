using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Reports whether this process is elevated and can relaunch it with elevation.
/// </summary>
public interface IElevationService
{
    /// <summary>True when the current process runs with administrative privileges.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Relaunches this executable elevated with the given arguments and returns true when the
    /// new process started. The caller is expected to shut itself down afterwards.
    /// </summary>
    bool RelaunchElevated(string arguments);
}

/// <summary>
/// Windows implementation.
///
/// WHY ELEVATION IS ONLY NEEDED FOR PART OF THE WORK — the split is not arbitrary, it follows
/// from who owns which registry key:
///
///   HKLM\SYSTEM\CurrentControlSet\Enum          (EDID_OVERRIDE)
///       SYSTEM has FullControl, so the background service writes it directly.
///       No UAC prompt is involved at all; the client just asks the service.
///
///   HKLM\SOFTWARE\Microsoft\Wisp\Touch          (tabcal calibration data)
///       Users have ReadKey only — writing is denied for a normal user (verified on the
///       target OS). And the service CANNOT do it either: tabcal.exe needs the interactive
///       session, which Session 0 is not. So this is the one case that genuinely requires a
///       UAC elevation of the CLIENT.
///
/// That is why the tray agent stays at asInvoker in autostart — a manifest-level requireAdmin
/// would put a UAC prompt in front of every boot — and elevation is requested only for the
/// wizard, on demand.
/// </summary>
public sealed class ElevationService : IElevationService
{
    private readonly ILogger<ElevationService> _logger;

    /// <summary>Initializes a new instance of <see cref="ElevationService"/>.</summary>
    /// <param name="logger">Logger for diagnostic output.</param>
    public ElevationService(ILogger<ElevationService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not determine the elevation state; assuming not elevated");
                return false;
            }
        }
    }

    /// <inheritdoc/>
    public bool RelaunchElevated(string arguments)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                _logger.LogError("Cannot relaunch elevated: the process path is unknown");
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                // UseShellExecute is REQUIRED for the runas verb: the elevation is performed by
                // the shell, not by CreateProcess. With UseShellExecute = false the verb is
                // ignored and the child simply starts unelevated, which looks like success and
                // then fails on the first write.
                UseShellExecute = true,
                Verb = "runas"
            };

            var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Elevated relaunch returned no process");
                return false;
            }

            _logger.LogInformation(
                "Relaunched elevated as PID {ProcessId} with arguments '{Arguments}'",
                process.Id, arguments);

            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the operator dismissed the UAC prompt. Expected, not a fault.
            _logger.LogInformation("The operator declined the elevation prompt");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not relaunch elevated");
            return false;
        }
    }
}
