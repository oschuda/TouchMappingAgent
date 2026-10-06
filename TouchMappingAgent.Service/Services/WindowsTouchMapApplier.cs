using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Services;

/// <summary>Result of <see cref="IWindowsTouchMapApplier.Apply"/>.</summary>
public enum TouchMapApplyOutcome
{
    /// <summary>Windows already routes this digitizer to this monitor; nothing was touched.</summary>
    AlreadyCurrent,

    /// <summary>The routing entry was written and the digitizer restarted so it takes effect now.</summary>
    Updated,

    /// <summary>Nothing usable could be done (invalid input, registry or restart failure).</summary>
    Failed,
}

/// <summary>
/// Makes Windows route one digitizer to one monitor.
/// </summary>
public interface IWindowsTouchMapApplier
{
    /// <summary>
    /// Ensures Windows routes <paramref name="touch"/> to <paramref name="monitor"/>.
    /// Idempotent: when the routing is already in place the digitizer is NOT restarted —
    /// unless <paramref name="forceRestart"/> is set, which is what an operator's explicit
    /// "apply now" means: the table may be right while Windows has not picked it up yet.
    /// </summary>
    TouchMapApplyOutcome Apply(HidDeviceInfo touch, MonitorInfo monitor, bool forceRestart = false);
}

/// <summary>
/// Writes Windows' own touch-to-display routing table and restarts the digitizer so the change
/// takes effect immediately. Replaces tabcal.exe for applying assignments.
///
/// WHY NOT tabcal.exe: "tabcal LinCal ... DevicePath=..." refuses to run with more than one
/// touch device attached ("Only one touch input device can be calibrated at a time") — on the
/// target installation it never applied anything; its "successes" were the operator dismissing
/// that dialog, after which tabcal exits 0. It also needs the interactive session.
///
/// THE ROUTING TABLE: HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon, one REG_SZ per digitizer:
///   name "20-&lt;HID interface path&gt;"  ->  data "&lt;monitor interface path&gt;"
/// e.g. "20-\\?\HID#VID_14E1&amp;PID_3508#8&amp;290d7833&amp;0&amp;0000#{4d1e55b2-...}" ->
///      "\\?\DISPLAY#CHR8910#5&amp;2c72b841&amp;0&amp;UID250116#{e6f07b5f-...}".
/// The "20-" prefix is taken verbatim from the entries Windows itself wrote on the target.
///
/// WHEN WINDOWS READS IT (measured on the target, 2026-10-06): writing the value alone changes
/// nothing until the next logon. Restarting the digitizer's HID device node
/// ("pnputil /restart-device") makes Windows look the device up again and the new routing
/// applies at once. TabletInputService cannot be restarted (CanStop = false), so that is not an
/// alternative.
///
/// WHY THE SERVICE DOES THIS: it runs as SYSTEM, which may write HKLM and restart devices — no
/// interactive session, no elevation prompt, no UI. The table is keyed on the HID interface
/// path, which behind the range extenders moves between the two physical touch screens after a
/// power-cycle; rewriting it from the port-anchored assignment on every hardware generation is
/// exactly what keeps the routing correct.
/// </summary>
public sealed class WindowsTouchMapApplier : IWindowsTouchMapApplier
{
    /// <summary>Registry path of the routing table under HKLM.</summary>
    public const string DigimonKeyPath = @"SOFTWARE\Microsoft\Wisp\Pen\Digimon";

    /// <summary>Value-name prefix observed on every touch entry Windows wrote itself.</summary>
    public const string TouchEntryPrefix = "20-";

    private const string MonitorPathPrefix = @"\\?\DISPLAY#";
    private const string HidPathPrefix = @"\\?\HID#";

    private readonly ILogger<WindowsTouchMapApplier> _logger;
    private readonly RegistryKey _rootKey;
    private readonly Func<string, bool> _restartDevice;
    private readonly object _lock = new();

    // Digitizers whose entry was written but whose restart failed. Their entry already reads
    // as current on the next attempt, so without this the restart would never be retried and
    // the new routing would silently wait for the next logon.
    private readonly HashSet<string> _restartOutstanding = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance writing the real HKLM table and restarting via pnputil.</summary>
    public WindowsTouchMapApplier(ILogger<WindowsTouchMapApplier> logger)
        : this(logger, RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), null)
    {
    }

    /// <summary>
    /// Testing seam: a writable registry root (e.g. under HKCU) and a fake device restart, so no
    /// test ever touches the real routing table or restarts a real device.
    /// </summary>
    internal WindowsTouchMapApplier(
        ILogger<WindowsTouchMapApplier> logger,
        RegistryKey rootKey,
        Func<string, bool>? restartDevice)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rootKey = rootKey ?? throw new ArgumentNullException(nameof(rootKey));
        _restartDevice = restartDevice ?? RestartDeviceWithPnputil;
    }

    /// <inheritdoc/>
    public TouchMapApplyOutcome Apply(HidDeviceInfo touch, MonitorInfo monitor, bool forceRestart = false)
    {
        ArgumentNullException.ThrowIfNull(touch);
        ArgumentNullException.ThrowIfNull(monitor);

        var valueName = BuildValueName(touch.DevicePath);
        var monitorPath = monitor.DevicePath?.Trim() ?? string.Empty;

        if (valueName == null || !IsPlausibleMonitorPath(monitorPath) || !IsPlausibleInstanceId(touch.InstanceId))
        {
            _logger.LogWarning(
                "Cannot route {TouchPath} to {MonitorConnector}: missing or malformed device identifiers " +
                "(monitor path '{MonitorPath}', instance '{InstanceId}')",
                touch.DevicePath, monitor.ConnectorLabel, monitorPath, touch.InstanceId);
            return TouchMapApplyOutcome.Failed;
        }

        lock (_lock)
        {
            try
            {
                using var key = _rootKey.CreateSubKey(DigimonKeyPath, writable: true);
                if (key == null)
                {
                    _logger.LogError("Cannot open {DigimonKey}", DigimonKeyPath);
                    return TouchMapApplyOutcome.Failed;
                }

                // Registry value names are case-insensitive, so this also finds an entry Windows
                // wrote with different casing; SetValue then updates that same entry.
                var current = key.GetValue(valueName) as string;
                bool tableCurrent = string.Equals(current, monitorPath, StringComparison.OrdinalIgnoreCase);

                if (tableCurrent && !forceRestart && !_restartOutstanding.Contains(touch.InstanceId))
                    return TouchMapApplyOutcome.AlreadyCurrent;

                if (!tableCurrent)
                {
                    key.SetValue(valueName, monitorPath, RegistryValueKind.String);
                    _logger.LogInformation(
                        "Routing table: {ValueName} -> {MonitorPath} (was {Previous})",
                        valueName, monitorPath, current ?? "(none)");
                }

                if (!_restartDevice(touch.InstanceId))
                {
                    _restartOutstanding.Add(touch.InstanceId);
                    _logger.LogWarning(
                        "Routing for {InstanceId} is written but the device restart failed; it applies at the " +
                        "next logon unless a later retry succeeds",
                        touch.InstanceId);
                    return TouchMapApplyOutcome.Failed;
                }

                _restartOutstanding.Remove(touch.InstanceId);

                ComplianceAuditLogger.LogCriticalAction(
                    AuditActions.SaveMapping,
                    $"ROUTE:{touch.InstanceId}=>{monitor.ConnectorLabel}",
                    success: true);

                return TouchMapApplyOutcome.Updated;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to route {TouchPath} to {MonitorConnector}", touch.DevicePath, monitor.ConnectorLabel);
                return TouchMapApplyOutcome.Failed;
            }
        }
    }

    /// <summary>
    /// Builds the routing value name in the casing Windows uses for its own entries:
    /// "\\?\HID#VID_xxxx&amp;PID_xxxx" upper-case, instance and interface GUID lower-case.
    /// SetupAPI reports the whole path lower-case.
    /// </summary>
    internal static string? BuildValueName(string? hidDevicePath)
    {
        var path = hidDevicePath?.Trim() ?? string.Empty;
        if (!path.StartsWith(HidPathPrefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith('}') ||
            path.Contains("..") || path.Any(char.IsControl))
        {
            return null;
        }

        var parts = path.Split('#');
        if (parts.Length < 4)
            return null;

        parts[0] = parts[0].ToUpperInvariant();
        parts[1] = parts[1].ToUpperInvariant();
        for (int i = 2; i < parts.Length; i++)
            parts[i] = parts[i].ToLowerInvariant();

        return TouchEntryPrefix + string.Join('#', parts);
    }

    private static bool IsPlausibleMonitorPath(string path) =>
        path.StartsWith(MonitorPathPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith('}') &&
        path.Length <= 512 &&
        !path.Contains("..") &&
        !path.Any(char.IsControl);

    private static bool IsPlausibleInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        instanceId.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase) &&
        instanceId.Length <= 512 &&
        instanceId.All(c => char.IsLetterOrDigit(c) || c is '\\' or '&' or '_' or '-' or '.');

    /// <summary>
    /// "pnputil /restart-device" — the exact step verified on the target. Arguments are passed
    /// via ArgumentList (no shell, no quoting issues); the instance id is validated above.
    /// </summary>
    private bool RestartDeviceWithPnputil(string instanceId)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/restart-device");
            startInfo.ArgumentList.Add(instanceId);

            using var process = Process.Start(startInfo);
            if (process == null)
                return false;

            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                _logger.LogWarning("pnputil /restart-device {InstanceId} did not finish within 30s", instanceId);
                return false;
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "pnputil /restart-device {InstanceId} exited with {ExitCode}: {Output}",
                    instanceId, process.ExitCode, stdout.Result.Trim());
                return false;
            }

            _logger.LogInformation("Restarted digitizer {InstanceId} so the new routing applies", instanceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restart digitizer {InstanceId}", instanceId);
            return false;
        }
    }
}
