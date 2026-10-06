using Microsoft.Extensions.Logging;
using TouchMappingAgent.Service.IPC;
using TouchMappingAgent.Service.Logging;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Service.Validation;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Service.Integration;

/// <summary>
/// Compliance integration handler for all IPC requests.
/// Implements security patterns: validation → processing → logging.
/// </summary>
public class ComplianceRequestHandler
{
    private readonly ILogger<ComplianceRequestHandler> _logger;
    private readonly BackupService _backupService;
    private readonly AdvancedRepairService _repairService;
    private readonly MappingStore _mappingStore;
    private readonly ReapplyCoordinator _reapplyCoordinator;
    private readonly Evolved.EdidManager.EdidManagerService _edidManager;
    private readonly Evolved.EdidManager.Templates.IEdidTemplateStore _templateStore;

    /// <summary>Initializes a new instance of <see cref="ComplianceRequestHandler"/>.</summary>
    /// <param name="logger">Logger for audit and diagnostic output.</param>
    /// <param name="backupService">Service for creating registry backups.</param>
    /// <param name="repairService">Service for executing multi-phase repair.</param>
    /// <param name="mappingStore">Persistent, hardware-anchored mapping store.</param>
    /// <param name="reapplyCoordinator">Decides which assignments need re-applying.</param>
    /// <param name="edidManager">Resolves EDID serial collisions between identical monitors.</param>
    /// <param name="templateStore">Provides EDID templates for extender fallback.</param>
    public ComplianceRequestHandler(
        ILogger<ComplianceRequestHandler> logger,
        BackupService backupService,
        AdvancedRepairService repairService,
        MappingStore mappingStore,
        ReapplyCoordinator reapplyCoordinator,
        Evolved.EdidManager.EdidManagerService edidManager,
        Evolved.EdidManager.Templates.IEdidTemplateStore templateStore)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
        _repairService = repairService ?? throw new ArgumentNullException(nameof(repairService));
        _mappingStore = mappingStore ?? throw new ArgumentNullException(nameof(mappingStore));
        _reapplyCoordinator = reapplyCoordinator ?? throw new ArgumentNullException(nameof(reapplyCoordinator));
        _edidManager = edidManager ?? throw new ArgumentNullException(nameof(edidManager));
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
    }

    /// <summary>
    /// Handles a MapTouchRequest with full compliance checks (input validation, audit logging).
    /// </summary>
    public async Task<MapTouchResponse> HandleMapTouchRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        const string action = AuditActions.SaveMapping;
        string deviceId = "UNKNOWN";

        try
        {
            // Step 1: SECURE DESERIALIZATION (CRA / IEC 62443)
            var request = SecureJsonDeserializer.DeserializeSecure<MapTouchRequest>(requestJson);
            deviceId = request.DeviceId;

            // Step 2: INPUT VALIDATION (IEC 62443-4-2)
            MappingValidator.ValidateMapRequest(request);

            // Step 3: PROCESS REQUEST (Business Logic)
            var result = await ProcessMappingAsync(request, cancellationToken);

            // Step 4: AUDIT LOG SUCCESS (ISO 27001 A.8.15)
            ComplianceAuditLogger.LogCriticalAction(action, deviceId, success: true);

            return result;
        }
        catch (ArgumentException ex)
        {
            // Input validation failed
            _logger.LogWarning(ex, "Input validation failed for MapTouchRequest");

            ComplianceAuditLogger.LogCriticalAction(
                action,
                deviceId,
                success: false,
                errorMessage: "Input validation failed");

            return new MapTouchResponse(false, "Invalid request parameters.");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Device not in whitelist
            _logger.LogWarning(ex, "Unauthorized device access attempt");

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.UnauthorizedAccess,
                deviceId,
                success: false,
                errorMessage: "Device not whitelisted");

            return new MapTouchResponse(false, "Device not authorized.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MapTouchRequest cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected error
            _logger.LogError(ex, "Unexpected error in MapTouchRequest");

            ComplianceAuditLogger.LogCriticalAction(
                action,
                deviceId,
                success: false,
                errorMessage: "Internal error");

            // OWASP: Never expose internal details to client
            return new MapTouchResponse(false, "An error occurred. Please contact support.");
        }
    }

    /// <summary>
    /// Persists a learned touch-to-monitor assignment. The OS-level routing lives in Windows'
    /// own table (HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon); the service writes it through
    /// <see cref="ReapplyCoordinator"/> / <see cref="WindowsTouchMapApplier"/> once the client's
    /// next poll supplies the monitor list. No tabcal.exe command is returned any more.
    /// </summary>
    private async Task<MapTouchResponse> ProcessMappingAsync(
        MapTouchRequest request,
        CancellationToken cancellationToken)
    {
        // PHASE 2: persist the HARDWARE ANCHORS, not the runtime handles. Storing the HID
        // interface path against "\\.\DISPLAYn" (as this did before) recorded two identifiers
        // Windows re-issues on every boot, so the record could come to describe a different
        // pair of devices after a restart — and nothing read it back in any case.
        if (MappingValidator.RequestCarriesAnchors(request))
        {
            var mapping = new TouchMapping(
                TouchHardwareKey: HidDeviceInfo.NormalizeInstanceId(request.TouchHardwareKey),
                MonitorHardwareKey: MonitorInfo.NormalizeDevicePath(request.MonitorHardwareKey),
                MonitorConnectorLabel: request.MonitorConnectorLabel,
                MonitorTargetId: request.MonitorTargetId,
                MonitorBoundsKey: request.MonitorBoundsKey,
                TouchProductName: request.TouchProductName,
                MonitorFriendlyName: request.MonitorFriendlyName,
                LastKnownTouchDevicePath: request.DeviceId,
                LastKnownMonitorDeviceId: request.MonitorId,
                LearnedUtc: DateTime.UtcNow.ToString("O"));

            MappingValidator.ValidateMapping(mapping);

            var persisted = await Task.Run(() => _mappingStore.Save(mapping), cancellationToken);
            if (!persisted)
            {
                _logger.LogError(
                    "Could not persist assignment {TouchKey} -> {MonitorConnector}; " +
                    "calibration will still be applied but will not survive a restart",
                    mapping.TouchHardwareKey, mapping.MonitorConnectorLabel);
            }
            else
            {
                // A fresh assignment supersedes whatever was applied before, so make sure the
                // coordinator re-asserts everything on the next poll instead of assuming the
                // previous state still holds.
                _reapplyCoordinator.InvalidateAppliedState("assignment learned or changed");
            }
        }
        else
        {
            _logger.LogInformation(
                "Mapping request for {DeviceId} carries no hardware anchors; applying once " +
                "without persisting it", request.DeviceId);
        }

        _logger.LogInformation(
            "Touch mapping requested: device={DeviceId} -> monitor={MonitorId}. The routing is written " +
            "by the service on the client's next poll (no tabcal.exe)",
            request.DeviceId, request.MonitorId);

        // No local execution: the service applies the routing itself (WindowsTouchMapApplier)
        // as soon as the client's next poll supplies the monitor list. tabcal.exe refuses to run
        // with two touch screens attached, so handing the client a tabcal command only ever
        // produced a modal error dialog on the target.
        return new MapTouchResponse(
            Success: true,
            ErrorMessage: null,
            LocalExecutablePath: null,
            LocalArguments: null,
            RequiresLocalExecution: false);
    }

    /// <summary>
    /// Returns every persisted, hardware-anchored assignment.
    /// </summary>
    public async Task<GetMappingsResponse> HandleGetMappingsRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mappings = await Task.Run(() => _mappingStore.GetAll(), cancellationToken);
            return new GetMappingsResponse(mappings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading persisted mappings");
            return new GetMappingsResponse(Array.Empty<TouchMapping>());
        }
    }

    /// <summary>
    /// Removes one persisted assignment.
    /// </summary>
    public async Task<DeleteMappingResponse> HandleDeleteMappingRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<DeleteMappingRequest>(requestJson);
            var removed = await Task.Run(() => _mappingStore.Delete(request.TouchHardwareKey), cancellationToken);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"DELETE:{request.TouchHardwareKey}",
                success: removed);

            return new DeleteMappingResponse(removed, removed ? null : "No such mapping.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting mapping");
            return new DeleteMappingResponse(false, "Could not delete mapping.");
        }
    }

    /// <summary>
    /// Answers the interactive client's poll for assignments needing re-application.
    /// The client supplies its monitor list because Session 0 cannot see the desktop.
    /// </summary>
    public async Task<GetPendingReapplyResponse> HandleGetPendingReapplyRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<GetPendingReapplyRequest>(requestJson);
            var monitors = request.CurrentMonitors ?? (IReadOnlyList<MonitorInfo>)Array.Empty<MonitorInfo>();

            var pending = await Task.Run(() =>
            {
                var digitizers = TouchDigitizerEnumerator.EnumerateDigitizers();
                return _reapplyCoordinator.GetPending(digitizers, monitors);
            }, cancellationToken);

            return new GetPendingReapplyResponse(pending);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing pending re-applications");
            return new GetPendingReapplyResponse(Array.Empty<PendingReapply>());
        }
    }

    /// <summary>
    /// Operator action "apply mapping now". See <see cref="ReapplyCoordinator.ApplyNow"/>.
    /// </summary>
    public async Task<ApplyMappingsNowResponse> HandleApplyMappingsNowRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<ApplyMappingsNowRequest>(requestJson);
            var monitors = request.CurrentMonitors ?? (IReadOnlyList<MonitorInfo>)Array.Empty<MonitorInfo>();

            return await Task.Run(
                () => _reapplyCoordinator.ApplyNow(TouchDigitizerEnumerator.EnumerateDigitizers(), monitors),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying mappings on operator request");
            return new ApplyMappingsNowResponse(0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Records the outcome of a re-application the client executed.
    /// </summary>
    public async Task<ReportReapplyResultResponse> HandleReportReapplyResultRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<ReportReapplyResultRequest>(requestJson);

            await Task.Run(() => _reapplyCoordinator.ReportResult(
                request.TouchHardwareKey, request.Success, request.ExitCode, request.ErrorMessage),
                cancellationToken);

            return new ReportReapplyResultResponse(true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording re-application result");
            return new ReportReapplyResultResponse(false, "Could not record result.");
        }
    }

    /// <summary>
    /// Reports every monitor's EDID identity and whether an override is installed.
    /// Read-only; safe to call without elevation.
    /// </summary>
    public async Task<GetEdidStatusResponse> HandleGetEdidStatusRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                var monitors = _edidManager.ReadAllMonitors();
                var plan = Evolved.EdidManager.Edid.EdidCollisionDetector.BuildPlan(monitors);
                var numericOnly = Evolved.EdidManager.Edid.EdidCollisionDetector.FindNumericOnlyCollisions(monitors);

                var colliding = plan.Select(e => e.Monitor.PnpInstanceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var status = monitors.Select(m => new EdidMonitorStatus(
                    PnpInstanceId: m.PnpInstanceId,
                    ManufacturerCode: m.Edid.ManufacturerCode,
                    ProductCode: m.Edid.ProductCode,
                    SerialNumber: m.Edid.SerialNumber,
                    SerialText: m.Edid.SerialText,
                    MonitorName: m.Edid.MonitorName,
                    HasOverride: m.IsOverridden,
                    CollidesWithAnother: colliding.Contains(m.PnpInstanceId))).ToList();

                return new GetEdidStatusResponse(status, plan.Count, numericOnly.Count);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading EDID status");
            return new GetEdidStatusResponse(Array.Empty<EdidMonitorStatus>(), 0, 0);
        }
    }

    /// <summary>
    /// Gives monitors that share an EDID identity a unique serial number each.
    /// </summary>
    public async Task<EdidOperationResponse> HandleResolveEdidCollisionsRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        const string action = AuditActions.SaveMapping;

        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<ResolveEdidCollisionsRequest>(requestJson);

            var result = await Task.Run(
                () => _edidManager.ResolveCollisions(request.DryRun), cancellationToken);

            if (!request.DryRun)
            {
                ComplianceAuditLogger.LogCriticalAction(
                    action,
                    $"EDID_DECOLLIDE:{result.Changed}",
                    success: result.Success,
                    errorMessage: result.Success ? null : result.Message.ToDiagnosticString());
            }

            return new EdidOperationResponse(result.Success, result.Changed, result.Failed, result.Message.ToLocalizableText());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving EDID collisions");
            return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Edid_DecollisionFailed));
        }
    }

    /// <summary>
    /// Removes the EDID override from one monitor, or from all of them.
    /// </summary>
    public async Task<EdidOperationResponse> HandleRestoreEdidDefaultsRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<RestoreEdidDefaultsRequest>(requestJson);

            var result = await Task.Run(() => string.IsNullOrWhiteSpace(request.PnpId)
                ? _edidManager.RestoreAllDefaults()
                : _edidManager.RestoreDefaults(request.PnpId!), cancellationToken);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"EDID_RESTORE:{request.PnpId ?? "ALL"}",
                success: result.Success,
                errorMessage: result.Success ? null : result.Message.ToDiagnosticString());

            return new EdidOperationResponse(result.Success, result.Changed, result.Failed, result.Message.ToLocalizableText());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring EDID defaults");
            return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Edid_RestoreFailed));
        }
    }

    /// <summary>
    /// Lists the EDID templates available on the machine, seeding the built-in ones first.
    /// </summary>
    public async Task<GetEdidTemplatesResponse> HandleGetEdidTemplatesRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                _templateStore.EnsureSeeded();

                var templates = _templateStore.ListTemplates()
                    .Select(t => new EdidTemplateInfo(
                        t.Name, t.Path, t.IsBuiltIn,
                        t.Edid.ManufacturerCode, t.Edid.ProductCode,
                        t.Edid.MonitorName, t.Edid.BlockCount))
                    .ToList();

                return new GetEdidTemplatesResponse(_templateStore.TemplateDirectory, templates);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing EDID templates");
            return new GetEdidTemplatesResponse(string.Empty, Array.Empty<EdidTemplateInfo>());
        }
    }

    /// <summary>
    /// Installs a template EDID on one port, stamped with the supplied serial.
    /// </summary>
    public async Task<EdidOperationResponse> HandleApplyEdidTemplateRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<ApplyEdidTemplateRequest>(requestJson);

            byte[] template;
            try
            {
                template = Convert.FromBase64String(request.TemplateEdidBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Edid_InvalidBase64));
            }

            var result = await Task.Run(
                () => _edidManager.ApplyCustomTemplate(request.PnpId, template, request.CustomSerial),
                cancellationToken);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"EDID_TEMPLATE:{request.PnpId}",
                success: result.Success,
                errorMessage: result.Success ? null : result.Message.ToDiagnosticString());

            return new EdidOperationResponse(result.Success, result.Changed, result.Failed, result.Message.ToLocalizableText());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying an EDID template");
            return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Edid_TemplateApplyFailed));
        }
    }

    /// <summary>
    /// Synthesises an EDID from a display mode and installs it — the last-resort path for an
    /// extender that delivers no usable EDID and has no template.
    /// </summary>
    public async Task<EdidOperationResponse> HandleSynthesizeEdidRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<SynthesizeEdidRequest>(requestJson);

            byte[] synthesized;
            try
            {
                synthesized = Evolved.EdidManager.Edid.EdidSynthesizer.SynthesizeFromDisplayMode(
                    request.Width, request.Height, request.RefreshRate, request.MonitorName);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.LogWarning(ex, "Refused to synthesise an EDID for an unrepresentable mode");
                return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(
                    MessageKeys.Edid_ModeNotRepresentable,
                    request.Width, request.Height, request.RefreshRate));
            }

            _logger.LogWarning(
                "Synthesising a fallback EDID for {PnpId} ({Width}x{Height}@{Refresh}). This is NOT " +
                "the panel's real EDID — colour handling and DPI scaling will be approximate, and " +
                "modes beyond this one stay invisible to Windows.",
                request.PnpId, request.Width, request.Height, request.RefreshRate);

            var result = await Task.Run(
                () => _edidManager.ApplyCustomTemplate(request.PnpId, synthesized, request.CustomSerial),
                cancellationToken);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"EDID_SYNTHESIZE:{request.PnpId}",
                success: result.Success,
                errorMessage: result.Success ? null : result.Message.ToDiagnosticString());

            return new EdidOperationResponse(result.Success, result.Changed, result.Failed, result.Message.ToLocalizableText());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error synthesising an EDID");
            return new EdidOperationResponse(false, 0, 0, LocalizableText.Of(MessageKeys.Edid_SynthesisFailed));
        }
    }

    /// <summary>
    /// Summarises hardware health for the tray icon and operator alarms.
    /// </summary>
    public async Task<GetHardwareStatusResponse> HandleGetHardwareStatusRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            // The status request reuses the pending-reapply envelope shape: the client has to
            // supply its monitor list for the same Session-0 reason.
            var request = SecureJsonDeserializer.DeserializeSecure<GetPendingReapplyRequest>(requestJson);
            var monitors = request.CurrentMonitors ?? (IReadOnlyList<MonitorInfo>)Array.Empty<MonitorInfo>();

            return await Task.Run(() =>
            {
                var digitizers = TouchDigitizerEnumerator.EnumerateDigitizers();
                return _reapplyCoordinator.DescribeStatus(digitizers, monitors);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing hardware status");
            return new GetHardwareStatusResponse(0, 0, 0, 0,
                new[] { LocalizableText.Of(MessageKeys.Status_Unavailable) });
        }
    }

    /// <summary>
    /// Records the outcome of a tabcal.exe mapping run that the interactive WPF client
    /// executed locally on the service's behalf (ISO 27001 A.8.15 audit trail).
    /// </summary>
    public async Task<ConfirmLocalMappingResponse> HandleConfirmLocalMappingRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = SecureJsonDeserializer.DeserializeSecure<ConfirmLocalMappingRequest>(requestJson);

            ComplianceAuditLogger.LogCriticalAction(
                AuditActions.SaveMapping,
                $"{request.DeviceId}=>{request.MonitorId}",
                success: request.ExecutionSuccess,
                errorMessage: request.ExecutionSuccess
                    ? null
                    : $"Local tabcal.exe execution failed (exit code {request.ExitCode?.ToString() ?? "unknown"}).");

            _logger.LogInformation(
                "Local mapping execution confirmed: device={DeviceId} -> monitor={MonitorId}, success={Success}",
                request.DeviceId, request.MonitorId, request.ExecutionSuccess);

            await Task.CompletedTask;
            return new ConfirmLocalMappingResponse(true, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("ConfirmLocalMappingRequest cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HandleConfirmLocalMappingRequestAsync");
            return new ConfirmLocalMappingResponse(false, "Failed to record mapping confirmation.");
        }
    }

    /// <summary>
    /// Handles a CreateBackupRequest for configuration resilience (MVO 2023/1230).
    /// </summary>
    public async Task<CreateBackupResponse> HandleCreateBackupRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        const string action = AuditActions.SaveMapping;
        string backupId = "UNKNOWN";

        try
        {
            // Step 1: SECURE DESERIALIZATION
            var request = SecureJsonDeserializer.DeserializeSecure<CreateBackupRequest>(requestJson);

            // Step 2: CREATE BACKUP (MVO 2023/1230 Resilience)
            _logger.LogInformation("Creating backup with description: {Description}", request.BackupDescription);
            var result = await Task.Run(() => _backupService.CreateBackup(request.BackupDescription), cancellationToken);

            if (result == null)
            {
                ComplianceAuditLogger.LogCriticalAction(
                    action,
                    "BACKUP_FAILED",
                    success: false,
                    errorMessage: "Backup creation returned null");

                return new CreateBackupResponse(false, null, "Backup creation failed.");
            }

            backupId = result;

            // Step 3: LOG SUCCESS (ISO 27001 A.8.15)
            ComplianceAuditLogger.LogCriticalAction(action, backupId, success: true);

            return new CreateBackupResponse(true, backupId, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("CreateBackupRequest cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HandleCreateBackupRequestAsync");

            ComplianceAuditLogger.LogCriticalAction(
                action,
                backupId,
                success: false,
                errorMessage: "Backup operation failed");

            // OWASP: Generic error message to client
            return new CreateBackupResponse(false, null, "An error occurred during backup. Please try again.");
        }
    }

    /// <summary>
    /// Handles an AdvancedRepairRequest for system recovery (IEC 62443 / NIS2).
    /// Executes multi-phase repair sequence for touch and display systems.
    /// </summary>
    public async Task<AdvancedRepairResponse> HandleAdvancedRepairRequestAsync(
        string requestJson,
        CancellationToken cancellationToken)
    {
        const string action = AuditActions.StartAdvancedRepair;
        string repairId = "UNKNOWN";

        try
        {
            // Step 1: SECURE DESERIALIZATION
            var request = SecureJsonDeserializer.DeserializeSecure<AdvancedRepairRequest>(requestJson);

            // Step 2: EXECUTE REPAIR (IEC 62443 / NIS2 Recovery)
            _logger.LogInformation("Starting advanced repair sequence");
            var (success, recoveryDetails) = await _repairService.ExecuteAdvancedRepairAsync(cancellationToken);

            // Step 3: LOG RESULT (ISO 27001 A.8.15)
            ComplianceAuditLogger.LogCriticalAction(
                action,
                "REPAIR_SEQUENCE",
                success: success,
                errorMessage: success ? null : recoveryDetails);

            return new AdvancedRepairResponse(success, recoveryDetails, null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AdvancedRepairRequest cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HandleAdvancedRepairRequestAsync");

            ComplianceAuditLogger.LogCriticalAction(
                action,
                repairId,
                success: false,
                errorMessage: "Repair operation failed");

            // OWASP: Generic error message to client
            return new AdvancedRepairResponse(
                false,
                "Repair sequence encountered an error.",
                "Please check Windows Event Log for details.");
        }
    }
}

