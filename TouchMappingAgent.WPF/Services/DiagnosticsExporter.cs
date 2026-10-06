using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.WPF.Localization;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Collects everything a support case needs into one text file.
///
/// Deliberately more than a copy of the log files. The questions that actually come up —
/// "which monitor is on which connector", "do the digitizers have distinct anchors", "is an
/// override installed" — are answered by the hardware state at export time, and that state is
/// gone by the time the file is read. So the export captures it alongside the logs.
/// </summary>
public sealed class DiagnosticsExporter
{
    private readonly INamedPipeClient _pipeClient;
    private readonly ILogger<DiagnosticsExporter> _logger;
    private readonly ILocalizer _localizer;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PadaLuma", "TouchMappingAgent", "logs");

    /// <summary>Initializes a new instance of <see cref="DiagnosticsExporter"/>.</summary>
    /// <param name="pipeClient">IPC client for querying the service.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Resolves service findings, which arrive as message keys.</param>
    public DiagnosticsExporter(
        INamedPipeClient pipeClient,
        ILogger<DiagnosticsExporter> logger,
        ILocalizer localizer)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
    }

    /// <summary>Suggested file name, stamped so several exports do not overwrite each other.</summary>
    public static string SuggestedFileName =>
        $"TouchMappingAgent-Diagnose_{DateTime.Now:yyyyMMdd_HHmmss}.log";

    /// <summary>
    /// Writes the diagnostics report to <paramref name="targetPath"/>.
    /// </summary>
    /// <returns>True when the file was written.</returns>
    public async Task<bool> ExportAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var report = await BuildReportAsync(cancellationToken);
            await File.WriteAllTextAsync(targetPath, report, new UTF8Encoding(false), cancellationToken);

            _logger.LogInformation("Diagnostics exported to {Path}", targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not export diagnostics to {Path}", targetPath);
            return false;
        }
    }

    private async Task<string> BuildReportAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=======================================================================");
        sb.AppendLine(" TouchMappingAgent — Diagnosebericht");
        sb.AppendLine($" Erstellt: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($" Rechner : {Environment.MachineName}");
        sb.AppendLine($" Benutzer: {Environment.UserName}");
        sb.AppendLine($" OS      : {Environment.OSVersion}");
        sb.AppendLine($" Prozess : {(Environment.Is64BitProcess ? "64-Bit" : "32-Bit")}");
        sb.AppendLine("=======================================================================");
        sb.AppendLine();

        AppendMonitors(sb);
        AppendDigitizers(sb);
        await AppendServiceStateAsync(sb, cancellationToken);
        AppendLogFiles(sb);

        return sb.ToString();
    }

    private void AppendMonitors(StringBuilder sb)
    {
        sb.AppendLine("--- MONITORE (aus der interaktiven Sitzung) ---------------------------");
        try
        {
            var monitors = DisplayEnumerator.EnumerateMonitors();
            sb.AppendLine($"Anzahl: {monitors.Count}");
            sb.AppendLine();

            foreach (var m in monitors)
            {
                sb.AppendLine($"  {m.DisplayLabel}");
                sb.AppendLine($"    GDI-Name    : {m.DeviceId}");
                sb.AppendLine($"    Connector   : {m.ConnectorLabel}  TargetId={m.TargetId}  Adapter={m.AdapterId}");
                sb.AppendLine($"    Bounds      : {m.BoundsKey}  Primär={m.IsPrimary}");
                sb.AppendLine($"    Modus       : {m.Width}x{m.Height}@{m.RefreshRate}");
                sb.AppendLine($"    DevicePath  : {m.DevicePath}");
                sb.AppendLine($"    HardwareKey : {m.HardwareKey}");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FEHLER: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private void AppendDigitizers(StringBuilder sb)
    {
        sb.AppendLine("--- TOUCH-DIGITIZER ---------------------------------------------------");
        try
        {
            var devices = TouchDigitizerEnumerator.EnumerateDigitizers();
            sb.AppendLine($"Anzahl: {devices.Count}");

            var ambiguous = MappingResolver.FindAmbiguousTouchKeys(devices);
            if (ambiguous.Count > 0)
            {
                sb.AppendLine(
                    $"WARNUNG: {ambiguous.Count} Gerät(e) ohne eindeutigen Hardware-Anker — " +
                    "für diese ist keine automatische Zuordnung möglich.");
            }

            sb.AppendLine();

            foreach (var d in devices)
            {
                sb.AppendLine($"  {d.DisplayLabel}");
                sb.AppendLine($"    DevicePath      : {d.DevicePath}");
                sb.AppendLine($"    InstanceId      : {d.InstanceId}");
                sb.AppendLine($"    ParentInstanceId: {d.ParentInstanceId}");
                sb.AppendLine($"    ParentLocation  : {d.ParentLocationPath}");
                sb.AppendLine($"    HardwareKey     : {d.HardwareKey}");
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FEHLER: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine();
        }
    }

    private async Task AppendServiceStateAsync(StringBuilder sb, CancellationToken cancellationToken)
    {
        sb.AppendLine("--- DIENST-ZUSTAND ----------------------------------------------------");
        try
        {
            var monitors = DisplayEnumerator.EnumerateMonitors();

            var mappings = await _pipeClient.SendAsync<GetMappingsResponse>(new GetMappingsRequest());
            sb.AppendLine($"Gespeicherte Zuordnungen: {mappings.Mappings?.Count ?? 0}");
            foreach (var m in mappings.Mappings ?? Array.Empty<Shared.Models.TouchMapping>())
            {
                sb.AppendLine($"  Touch  : {m.TouchHardwareKey}");
                sb.AppendLine($"  Monitor: {m.MonitorHardwareKey}");
                sb.AppendLine($"           {m.MonitorConnectorLabel} / Target {m.MonitorTargetId} / {m.MonitorBoundsKey}");
                sb.AppendLine($"  Gelernt: {m.LearnedUtc}   zuletzt angewendet: {m.LastAppliedUtc}");
                sb.AppendLine();
            }

            var status = await _pipeClient.SendAsync<GetHardwareStatusResponse>(
                new GetPendingReapplyRequest(monitors));
            sb.AppendLine($"Digitizer laut Dienst   : {status.DigitizerCount}");
            sb.AppendLine($"Zuordnungen aufgelöst   : {status.ResolvedMappingCount}/{status.MappingCount}");
            sb.AppendLine($"Mehrdeutige Digitizer   : {status.AmbiguousTouchDeviceCount}");
            // Both forms: the localised sentence for whoever exported the report, and the raw key
            // for whoever receives it. A support engineer reading a report exported in a language
            // they do not speak can still match the key against the source.
            foreach (var finding in status.Findings ?? Array.Empty<LocalizableText>())
                sb.AppendLine($"  ! {_localizer.Format(finding)}   [{finding.ToDiagnosticString()}]");
            sb.AppendLine();

            var edid = await _pipeClient.SendAsync<GetEdidStatusResponse>(new GetEdidStatusRequest());
            sb.AppendLine($"EDID-Kollisionen (voll)     : {edid.FullIdentityCollisions}");
            sb.AppendLine($"EDID-Kollisionen (numerisch): {edid.NumericOnlyCollisions}");
            foreach (var m in edid.Monitors ?? Array.Empty<EdidMonitorStatus>())
            {
                sb.AppendLine($"  {m.PnpInstanceId}");
                sb.AppendLine($"    {m.ManufacturerCode} {m.ProductCode:X4} '{m.MonitorName}' " +
                              $"Serial={m.SerialNumber} Text='{m.SerialText}' " +
                              $"Override={m.HasOverride} Kollision={m.CollidesWithAnother}");
            }
            sb.AppendLine();
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  Dienst nicht erreichbar: {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine("  (Der Dienst muss laufen, damit gespeicherte Zuordnungen exportiert werden.)");
            sb.AppendLine();
        }
    }

    private void AppendLogFiles(StringBuilder sb)
    {
        sb.AppendLine("--- PROTOKOLLE --------------------------------------------------------");
        sb.AppendLine($"Verzeichnis: {LogDirectory}");
        sb.AppendLine();

        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                sb.AppendLine("  Verzeichnis existiert nicht — es wurde noch nichts protokolliert.");
                return;
            }

            // Newest first, and only the most recent few: a full 14-day retention would make
            // the export unwieldy without adding anything to a current fault.
            var files = new DirectoryInfo(LogDirectory)
                .GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(4)
                .ToList();

            if (files.Count == 0)
            {
                sb.AppendLine("  Keine Logdateien gefunden.");
                return;
            }

            foreach (var file in files)
            {
                sb.AppendLine("=======================================================================");
                sb.AppendLine($" {file.Name}  ({file.Length:N0} Bytes, {file.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
                sb.AppendLine("=======================================================================");

                try
                {
                    // FileShare.ReadWrite: both processes hold their log open, so an exclusive
                    // read would fail on exactly the files that matter.
                    using var stream = new FileStream(
                        file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    var content = reader.ReadToEnd();

                    // Cap each file so one runaway log cannot bury the rest of the report.
                    const int maxChars = 200_000;
                    if (content.Length > maxChars)
                    {
                        sb.AppendLine($"[... {content.Length - maxChars:N0} Zeichen ausgelassen, Ende der Datei folgt ...]");
                        content = content[^maxChars..];
                    }

                    sb.AppendLine(content);
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"  Konnte nicht gelesen werden: {ex.GetType().Name}: {ex.Message}");
                }

                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  FEHLER: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
