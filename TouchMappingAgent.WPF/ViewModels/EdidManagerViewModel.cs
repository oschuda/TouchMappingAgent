using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;

namespace TouchMappingAgent.WPF.ViewModels;

/// <summary>
/// Drives the EDID &amp; Display manager window.
///
/// All privileged work happens in the service: this ViewModel only gathers what Session 0
/// cannot see (the monitor list) and renders what comes back. The one exception is reading a
/// template file the operator picked, which is plain file I/O in their own session.
/// </summary>
public partial class EdidManagerViewModel : ObservableObject
{
    private readonly INamedPipeClient _pipeClient;
    private readonly ILogger<EdidManagerViewModel> _logger;
    private readonly ILocalizer _localizer;

    /// <summary>Monitors as the service sees them, joined with the client's display data.</summary>
    public ObservableCollection<EdidPortRow> Ports { get; } = new();

    /// <summary>Templates offered by the service.</summary>
    public ObservableCollection<EdidTemplateInfo> Templates { get; } = new();

    /// <summary>Currently selected port.</summary>
    [ObservableProperty]
    private EdidPortRow? selectedPort;

    /// <summary>Currently selected template.</summary>
    [ObservableProperty]
    private EdidTemplateInfo? selectedTemplate;

    /// <summary>Serial stamped into an applied template.</summary>
    [ObservableProperty]
    private string customSerial = string.Empty;

    /// <summary>Status line shown at the bottom of the window.</summary>
    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>True while an operation is running; disables the action buttons.</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>Directory the service seeds templates into, shown so it can be opened.</summary>
    [ObservableProperty]
    private string templateDirectory = string.Empty;

    /// <summary>Path of a file the operator dropped or picked, when it is outside the store.</summary>
    [ObservableProperty]
    private string? droppedFilePath;

    /// <summary>Summary of the collision analysis.</summary>
    [ObservableProperty]
    private string collisionSummary = string.Empty;

    /// <summary>Initializes a new instance of <see cref="EdidManagerViewModel"/>.</summary>
    /// <param name="pipeClient">IPC client for talking to the service.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Resolves service messages, which arrive as keys.</param>
    public EdidManagerViewModel(
        INamedPipeClient pipeClient,
        ILogger<EdidManagerViewModel> logger,
        ILocalizer localizer)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        StatusMessage = _localizer[LocalizationKeys.Wizard_Ready];
    }

    /// <summary>{0} = dropped file path, formatted for display next to the template picker.</summary>
    public string DroppedFileDisplay => DroppedFilePath == null
        ? string.Empty
        : string.Format(_localizer[LocalizationKeys.EdidUi_DroppedFile], DroppedFilePath);

    partial void OnDroppedFilePathChanged(string? value) => OnPropertyChanged(nameof(DroppedFileDisplay));

    /// <summary>Command: reloads ports and templates from the service.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = _localizer[LocalizationKeys.EdidUi_LoadingRunning];

            // The monitor list comes from THIS process: the service runs in Session 0 and
            // cannot enumerate the desktop.
            var monitors = DisplayEnumerator.EnumerateMonitors();

            var status = await _pipeClient.SendAsync<GetEdidStatusResponse>(new GetEdidStatusRequest());
            var templates = await _pipeClient.SendAsync<GetEdidTemplatesResponse>(new GetEdidTemplatesRequest());

            TemplateDirectory = templates.TemplateDirectory ?? string.Empty;

            Templates.Clear();
            foreach (var template in templates.Templates ?? Array.Empty<EdidTemplateInfo>())
                Templates.Add(template);

            SelectedTemplate ??= Templates.FirstOrDefault();

            BuildPortRows(status, monitors);

            CollisionSummary = status.FullIdentityCollisions > 0
                ? string.Format(
                    _localizer[LocalizationKeys.EdidUi_CollisionSummaryFull], status.FullIdentityCollisions)
                : status.NumericOnlyCollisions > 0
                    ? string.Format(
                        _localizer[LocalizationKeys.EdidUi_CollisionSummaryNumericOnly],
                        status.NumericOnlyCollisions)
                    : _localizer[LocalizationKeys.EdidUi_CollisionSummaryNone];

            StatusMessage = string.Format(
                _localizer[LocalizationKeys.EdidUi_StatusSummary], Ports.Count, Templates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the EDID overview");
            StatusMessage = _localizer[LocalizationKeys.EdidUi_ServiceUnreachable];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Joins what the service knows (EDID identity, overrides) with what only this session can
    /// see (which GDI port a monitor is on, its connector and bounds).
    /// </summary>
    private void BuildPortRows(GetEdidStatusResponse status, IReadOnlyList<MonitorInfo> monitors)
    {
        Ports.Clear();

        var byInstanceId = monitors
            .Where(m => !string.IsNullOrWhiteSpace(m.DevicePath))
            .GroupBy(m => Evolved.EdidManager.Pnp.PnpDeviceInstanceId.FromDevicePath(m.DevicePath) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var monitor in status.Monitors ?? Array.Empty<EdidMonitorStatus>())
        {
            byInstanceId.TryGetValue(monitor.PnpInstanceId, out var display);

            Ports.Add(new EdidPortRow(
                PnpInstanceId: monitor.PnpInstanceId,
                Connector: display?.ConnectorLabel ?? "—",
                GdiName: display?.DeviceId ?? "—",
                MonitorName: monitor.MonitorName ?? display?.DisplayName ?? "—",
                SerialNumber: monitor.SerialNumber,
                SerialText: monitor.SerialText,
                HasOverride: monitor.HasOverride,
                Collides: monitor.CollidesWithAnother,
                IsAttached: display != null,
                Width: display?.Width ?? 0,
                Height: display?.Height ?? 0,
                RefreshRate: display?.RefreshRate ?? 0));
        }

        // Ports the desktop shows but the service found no EDID for — the extender case, and
        // the one the operator most needs to see. Without this they would simply be absent.
        foreach (var monitor in monitors)
        {
            var instanceId = Evolved.EdidManager.Pnp.PnpDeviceInstanceId.FromDevicePath(monitor.DevicePath);
            if (instanceId == null || Ports.Any(p => string.Equals(p.PnpInstanceId, instanceId, StringComparison.OrdinalIgnoreCase)))
                continue;

            Ports.Add(new EdidPortRow(
                PnpInstanceId: instanceId,
                Connector: monitor.ConnectorLabel,
                GdiName: monitor.DeviceId,
                MonitorName: monitor.DisplayName,
                SerialNumber: 0,
                SerialText: null,
                HasOverride: false,
                Collides: false,
                IsAttached: true,
                Width: monitor.Width,
                Height: monitor.Height,
                RefreshRate: monitor.RefreshRate,
                HasEdid: false));
        }
    }

    /// <summary>Command: resolves EDID serial collisions.</summary>
    [RelayCommand]
    public async Task ResolveCollisionsAsync() =>
        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(new ResolveEdidCollisionsRequest(false)),
            _localizer[LocalizationKeys.EdidUi_ResolvingRunning]);

    /// <summary>Command: previews what collision resolution would change.</summary>
    [RelayCommand]
    public async Task PreviewCollisionsAsync() =>
        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(new ResolveEdidCollisionsRequest(true)),
            _localizer[LocalizationKeys.EdidUi_PreviewRunning]);

    /// <summary>Command: removes every EDID override.</summary>
    [RelayCommand]
    public async Task RestoreDefaultsAsync() =>
        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(new RestoreEdidDefaultsRequest(null)),
            _localizer[LocalizationKeys.EdidUi_RestoringAllRunning]);

    /// <summary>Command: removes the override from the selected port only.</summary>
    [RelayCommand]
    public async Task RestoreSelectedAsync()
    {
        if (SelectedPort == null)
        {
            StatusMessage = _localizer[LocalizationKeys.EdidUi_SelectPortFirst];
            return;
        }

        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(
                new RestoreEdidDefaultsRequest(SelectedPort.PnpInstanceId)),
            string.Format(_localizer[LocalizationKeys.EdidUi_RestoringSelectedRunning], SelectedPort.Connector));
    }

    /// <summary>
    /// Command: applies the selected template — or a dropped file — to the selected port.
    /// </summary>
    [RelayCommand]
    public async Task ApplyTemplateAsync()
    {
        if (SelectedPort == null)
        {
            StatusMessage = _localizer[LocalizationKeys.EdidUi_SelectPortFirst];
            return;
        }

        byte[]? edid = await LoadSelectedEdidAsync();
        if (edid == null)
            return;

        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(new ApplyEdidTemplateRequest(
                SelectedPort.PnpInstanceId,
                Convert.ToBase64String(edid),
                CustomSerial)),
            string.Format(_localizer[LocalizationKeys.EdidUi_ApplyingTemplateRunning], SelectedPort.Connector));
    }

    /// <summary>
    /// Command: synthesises an EDID from the port's current mode and installs it.
    /// </summary>
    [RelayCommand]
    public async Task SynthesizeAsync()
    {
        if (SelectedPort == null)
        {
            StatusMessage = _localizer[LocalizationKeys.EdidUi_SelectPortFirst];
            return;
        }

        if (SelectedPort.Width <= 0 || SelectedPort.Height <= 0)
        {
            StatusMessage = _localizer[LocalizationKeys.EdidUi_NoActiveMode];
            return;
        }

        await RunOperationAsync(
            () => _pipeClient.SendAsync<EdidOperationResponse>(new SynthesizeEdidRequest(
                SelectedPort.PnpInstanceId,
                SelectedPort.Width,
                SelectedPort.Height,
                SelectedPort.RefreshRate > 0 ? SelectedPort.RefreshRate : 60,
                string.IsNullOrWhiteSpace(SelectedPort.MonitorName) ? "Synthetic" : SelectedPort.MonitorName,
                CustomSerial)),
            string.Format(_localizer[LocalizationKeys.EdidUi_SynthesizingRunning], SelectedPort.Connector));
    }

    /// <summary>
    /// Reads the EDID the operator selected: a dropped/browsed file takes precedence over the
    /// template list, because picking a file is the more explicit action.
    /// </summary>
    private async Task<byte[]?> LoadSelectedEdidAsync()
    {
        if (!string.IsNullOrWhiteSpace(DroppedFilePath))
            return await Task.Run(() => ReadEdidFile(DroppedFilePath!));

        if (SelectedTemplate == null)
        {
            StatusMessage = _localizer[LocalizationKeys.EdidUi_SelectTemplateOrFile];
            return null;
        }

        if (string.IsNullOrWhiteSpace(SelectedTemplate.Path))
        {
            // A built-in template with no file on disk yet. Asking the service to seed it is
            // simpler and safer than reconstructing the bytes here.
            StatusMessage = _localizer[LocalizationKeys.EdidUi_BuiltinTemplatePending];
            return null;
        }

        return await Task.Run(() => ReadEdidFile(SelectedTemplate.Path!));
    }

    /// <summary>
    /// Reads a .bin, .hex or .inf and returns its EDID bytes, or null with a status message.
    /// Runs entirely in this process — reading a file the operator chose needs no elevation.
    /// </summary>
    private byte[]? ReadEdidFile(string path)
    {
        try
        {
            var store = new Evolved.EdidManager.Templates.EdidTemplateStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Evolved.EdidManager.Templates.EdidTemplateStore>.Instance,
                Path.GetDirectoryName(path) ?? Path.GetTempPath());

            if (Evolved.EdidManager.Templates.EdidTemplateStore.IsInfFile(path))
            {
                var entries = store.LoadFromInf(path);

                if (entries.Count == 0)
                {
                    StatusMessage = string.Format(
                        _localizer[LocalizationKeys.EdidUi_NoEdidInFile], Path.GetFileName(path));
                    return null;
                }

                var usable = entries.FirstOrDefault(e => e.IsChecksumValid);
                if (usable == null)
                {
                    StatusMessage = string.Format(
                        _localizer[LocalizationKeys.EdidUi_InfAllChecksumsInvalid], Path.GetFileName(path));
                    return null;
                }

                if (entries.Count > 1)
                {
                    StatusMessage = string.Format(
                        _localizer[LocalizationKeys.EdidUi_InfMultipleEntries],
                        Path.GetFileName(path), entries.Count, usable.SectionName);
                }

                return usable.Edid.ToArray();
            }

            var template = store.LoadFromFile(path);
            if (template == null)
            {
                StatusMessage = string.Format(
                    _localizer[LocalizationKeys.EdidUi_FileNotValidEdid], Path.GetFileName(path));
                return null;
            }

            return template.Edid.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the EDID file {Path}", path);
            StatusMessage = string.Format(
                _localizer[LocalizationKeys.EdidUi_FileReadFailed], Path.GetFileName(path));
            return null;
        }
    }

    /// <summary>Accepts a file the operator dropped onto the window.</summary>
    public void AcceptDroppedFile(string path)
    {
        DroppedFilePath = path;
        StatusMessage = string.Format(
            _localizer[LocalizationKeys.EdidUi_FileAccepted], Path.GetFileName(path));
    }

    /// <summary>Clears a previously dropped file so the template list applies again.</summary>
    [RelayCommand]
    public void ClearDroppedFile()
    {
        DroppedFilePath = null;
        StatusMessage = _localizer[LocalizationKeys.EdidUi_FileSelectionCleared];
    }

    private async Task RunOperationAsync(Func<Task<EdidOperationResponse>> operation, string runningMessage)
    {
        try
        {
            IsBusy = true;
            StatusMessage = runningMessage;

            var result = await operation();
            StatusMessage = _localizer.Format(result.Message);

            if (!result.Success)
            {
                // The key form, not the translated one: a log read by support must not depend on
                // which language the operator happened to be running.
                _logger.LogWarning(
                    "EDID operation reported failure: {Message}", result.Message.ToDiagnosticString());
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EDID operation failed");
            StatusMessage = _localizer[LocalizationKeys.EdidUi_OperationFailed];
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>One row in the port overview.</summary>
/// <param name="PnpInstanceId">Device instance id.</param>
/// <param name="Connector">Graphics connector, e.g. "DP-2".</param>
/// <param name="GdiName">Current GDI device name.</param>
/// <param name="MonitorName">Friendly name.</param>
/// <param name="SerialNumber">Numeric EDID serial.</param>
/// <param name="SerialText">0xFF descriptor text, when present.</param>
/// <param name="HasOverride">True when an EDID override is installed.</param>
/// <param name="Collides">True when another monitor shares this EDID identity.</param>
/// <param name="IsAttached">True when the monitor is currently on the desktop.</param>
/// <param name="Width">Active horizontal pixels.</param>
/// <param name="Height">Active vertical pixels.</param>
/// <param name="RefreshRate">Vertical refresh in Hz.</param>
/// <param name="HasEdid">False when the port reports no EDID at all.</param>
public sealed record EdidPortRow(
    string PnpInstanceId,
    string Connector,
    string GdiName,
    string MonitorName,
    uint SerialNumber,
    string? SerialText,
    bool HasOverride,
    bool Collides,
    bool IsAttached,
    int Width,
    int Height,
    int RefreshRate,
    bool HasEdid = true)
{
    /// <summary>
    /// EDID status as the operator needs to read it.
    ///
    /// "Extender" is the case worth naming explicitly: a port that is driving a picture but
    /// reports no EDID is almost always a KVM/HDBaseT link that swallows DDC, and that is
    /// exactly when a template or a synthesised block is the answer.
    /// </summary>
    public string EdidStatus =>
        !HasEdid && IsAttached ? LocalizationSource.Instance[LocalizationKeys.EdidUi_LegendExtender]
        : !HasEdid ? LocalizationSource.Instance[LocalizationKeys.EdidUi_StatusNoEdid]
        : Collides ? LocalizationSource.Instance[LocalizationKeys.EdidUi_LegendCollision]
        : HasOverride ? LocalizationSource.Instance[LocalizationKeys.EdidUi_LegendOverride]
        : LocalizationSource.Instance[LocalizationKeys.EdidUi_LegendValid];

    /// <summary>Serial as shown in the list: text where the panel provides one.</summary>
    public string SerialDisplay =>
        !HasEdid ? "—"
        : string.IsNullOrWhiteSpace(SerialText) ? SerialNumber.ToString()
        : $"{SerialText} ({SerialNumber})";

    /// <summary>Current mode, or a dash when the port is not on the desktop.</summary>
    public string ModeDisplay =>
        Width > 0 && Height > 0 ? $"{Width}×{Height}@{RefreshRate}" : "—";
}
