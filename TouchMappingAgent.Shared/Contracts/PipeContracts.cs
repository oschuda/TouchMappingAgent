using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.Shared.Contracts;

/// <summary>
/// Request to get all available monitors.
/// </summary>
public record GetMonitorsRequest;

/// <summary>
/// Response containing monitor information.
/// </summary>
public record GetMonitorsResponse(
    IReadOnlyList<MonitorInfo> Monitors);

/// <summary>
/// Request to get all connected HID devices.
/// </summary>
public record GetHidDevicesRequest;

/// <summary>
/// Response containing HID device information.
/// </summary>
public record GetHidDevicesResponse(
    IReadOnlyList<HidDeviceInfo> Devices);

/// <summary>
/// Request to map a HID device to a monitor.
///
/// Carries BOTH kinds of identifier, and the distinction matters:
///
/// * <see cref="MonitorId"/> and <see cref="DeviceId"/> are the volatile RUNTIME handles —
///   the "\\.\DISPLAYn" GDI name and the HID interface path. tabcal.exe needs exactly these,
///   so they must travel with the request, but they are re-resolved on every run.
///
/// * <see cref="TouchHardwareKey"/> and the Monitor* anchors are the STABLE identity that
///   gets persisted. Storing the runtime handles as the key was the original defect: after a
///   reboot in which the range extenders completed their handshake in a different order,
///   "\\.\DISPLAY2" and "\\.\DISPLAY3" could swap, silently pointing a learned assignment at
///   the wrong panel.
/// </summary>
public record MapTouchRequest(
    string MonitorId,
    string DeviceId,
    string TouchHardwareKey = "",
    string MonitorHardwareKey = "",
    string MonitorConnectorLabel = "",
    uint MonitorTargetId = 0,
    string MonitorBoundsKey = "",
    string TouchProductName = "",
    string MonitorFriendlyName = "");

/// <summary>
/// Response indicating the result of a mapping operation.
/// Windows Services run in Session 0 (non-interactive) and cannot show the physical
/// touch-confirmation UI that tabcal.exe requires, so the actual OS-level calibration
/// cannot be executed by the service. Instead, the service instructs the interactive
/// WPF client to run it locally via <see cref="LocalExecutablePath"/>/<see cref="LocalArguments"/>.
/// </summary>
public record MapTouchResponse(
    bool Success,
    string? ErrorMessage,
    string? LocalExecutablePath = null,
    string? LocalArguments = null,
    bool RequiresLocalExecution = false);

/// <summary>
/// Sent by the WPF client after it has locally executed the tabcal.exe calibration/mapping
/// command (in the interactive user session) requested via <see cref="MapTouchResponse"/>.
/// Allows the service to record the real outcome in the compliance audit trail.
/// </summary>
public record ConfirmLocalMappingRequest(
    string MonitorId,
    string DeviceId,
    bool ExecutionSuccess,
    int? ExitCode);

/// <summary>
/// Response acknowledging receipt of a local mapping execution confirmation.
/// </summary>
public record ConfirmLocalMappingResponse(
    bool Success,
    string? ErrorMessage);

/// <summary>
/// Request to get all connected touch devices (HID/USB).
/// </summary>
public record GetTouchDevicesRequest;

/// <summary>
/// Response containing touch device information.
/// </summary>
public record GetTouchDevicesResponse(
    IReadOnlyList<HidDeviceInfo> Devices);

// =========================================================================================
// PHASE 2: hardware-anchored persistence and automatic re-application
// =========================================================================================

/// <summary>
/// Request for all persisted mappings (hardware-anchored, as stored).
/// </summary>
public record GetMappingsRequest;

/// <summary>
/// Response carrying every persisted mapping.
/// </summary>
public record GetMappingsResponse(
    IReadOnlyList<TouchMapping> Mappings);

/// <summary>
/// Request to delete a persisted mapping by its touch hardware anchor.
/// </summary>
public record DeleteMappingRequest(
    string TouchHardwareKey);

/// <summary>
/// Response indicating whether the mapping was removed.
/// </summary>
public record DeleteMappingResponse(
    bool Success,
    string? ErrorMessage);

/// <summary>
/// Polled by the interactive client to learn which assignments the service wants re-applied.
///
/// WHY POLLING AND NOT A PUSH: the service owns the Named Pipe SERVER and the client is the
/// pipe CLIENT, so the service cannot initiate a call. It also cannot execute tabcal.exe
/// itself — a Windows Service lives in the non-interactive Session 0 and can neither show
/// calibration UI nor receive a touch confirmation. So the service queues the work and the
/// client, which does live in the interactive session, collects it.
///
/// WHY THE CLIENT SENDS ITS MONITOR LIST: Session 0 also cannot see the desktop. Running
/// EnumDisplayDevices from the SYSTEM service process returns an EMPTY monitor list even on a
/// machine with monitors attached (confirmed empirically, see DisplayEnumerator's remarks).
/// The service therefore cannot resolve the monitor side of a stored mapping on its own; the
/// client, which does have the correct view, supplies it. The service stays the authority on
/// what is stored and what has already been applied.
/// </summary>
public record GetPendingReapplyRequest(
    IReadOnlyList<MonitorInfo> CurrentMonitors);

/// <summary>
/// One queued re-application: the stored assignment resolved against present hardware, with
/// the runtime identifiers and the ready-to-run tabcal.exe command line.
/// </summary>
public record PendingReapply(
    string TouchHardwareKey,
    string MonitorHardwareKey,
    string MonitorConnectorLabel,
    string TouchDevicePath,
    string MonitorDeviceId,
    string MonitorFriendlyName,
    string LocalExecutablePath,
    string LocalArguments,
    MappingMatchQuality MatchQuality,
    LocalizableText Reason);

/// <summary>
/// Response carrying the queued re-applications (empty when nothing is pending).
/// </summary>
public record GetPendingReapplyResponse(
    IReadOnlyList<PendingReapply> Pending);

/// <summary>
/// Reports the outcome of a re-application the client executed, so the service can clear the
/// queue entry and record it in the audit trail.
/// </summary>
public record ReportReapplyResultRequest(
    string TouchHardwareKey,
    bool Success,
    int? ExitCode,
    string? ErrorMessage = null);

/// <summary>
/// Response acknowledging a re-application result.
/// </summary>
public record ReportReapplyResultResponse(
    bool Success,
    string? ErrorMessage);

/// <summary>
/// Operator action "apply mapping now": the service applies every stored assignment at once and
/// restarts the digitizers so Windows picks the routing up immediately. Carries the client's
/// monitor list for the same reason as <see cref="GetPendingReapplyRequest"/>.
/// </summary>
public record ApplyMappingsNowRequest(
    IReadOnlyList<MonitorInfo> CurrentMonitors);

/// <summary>
/// Outcome of <see cref="ApplyMappingsNowRequest"/>. <see cref="Unresolvable"/> counts stored
/// assignments whose digitizer or monitor is not present (or uses the former, unstable anchor
/// and must be re-learned).
/// </summary>
public record ApplyMappingsNowResponse(
    int MappingCount,
    int Applied,
    int Unresolvable,
    int Failed);

/// <summary>
/// Request for the service's view of current hardware health: how many displays and
/// digitizers are present, which mappings resolve, and which do not.
/// </summary>
public record GetHardwareStatusRequest;

/// <summary>
/// Response describing hardware health, used by the tray icon and for operator alarms.
/// </summary>
public record GetHardwareStatusResponse(
    int DigitizerCount,
    int MappingCount,
    int ResolvedMappingCount,
    int AmbiguousTouchDeviceCount,
    IReadOnlyList<LocalizableText> Findings);

// =========================================================================================
// EDID management (Evolved.EdidManager)
// =========================================================================================

/// <summary>
/// Reports each monitor's EDID identity and whether an override is installed.
/// Read-only and safe without elevation.
/// </summary>
public record GetEdidStatusRequest;

/// <summary>One monitor's EDID identity as Windows currently presents it.</summary>
public record EdidMonitorStatus(
    string PnpInstanceId,
    string ManufacturerCode,
    ushort ProductCode,
    uint SerialNumber,
    string? SerialText,
    string? MonitorName,
    bool HasOverride,
    bool CollidesWithAnother);

/// <summary>Response carrying the EDID status of every monitor that reports one.</summary>
public record GetEdidStatusResponse(
    IReadOnlyList<EdidMonitorStatus> Monitors,
    int FullIdentityCollisions,
    int NumericOnlyCollisions);

/// <summary>
/// Requests that monitors sharing an EDID identity be given unique serial numbers.
/// </summary>
public record ResolveEdidCollisionsRequest(bool DryRun = false);

/// <summary>
/// Requests removal of the EDID override from one monitor, or from all when
/// <see cref="PnpId"/> is null or empty.
/// </summary>
public record RestoreEdidDefaultsRequest(string? PnpId = null);

/// <summary>Outcome of an EDID management operation.</summary>
public record EdidOperationResponse(
    bool Success,
    int Changed,
    int Failed,
    LocalizableText Message);

/// <summary>Requests the list of EDID templates available on the machine.</summary>
public record GetEdidTemplatesRequest;

/// <summary>One template offered to the operator.</summary>
public record EdidTemplateInfo(
    string Name,
    string? Path,
    bool IsBuiltIn,
    string ManufacturerCode,
    ushort ProductCode,
    string? MonitorName,
    int BlockCount);

/// <summary>Response listing the available templates.</summary>
public record GetEdidTemplatesResponse(
    string TemplateDirectory,
    IReadOnlyList<EdidTemplateInfo> Templates);

/// <summary>
/// Applies a template EDID to one port. The bytes travel base64-encoded so the request stays a
/// plain JSON document.
/// </summary>
public record ApplyEdidTemplateRequest(
    string PnpId,
    string TemplateEdidBase64,
    string CustomSerial);

/// <summary>
/// Builds an EDID from a display mode and installs it on one port — the fallback for an
/// extender that passes no DDC through and for which no template exists.
/// </summary>
public record SynthesizeEdidRequest(
    string PnpId,
    int Width,
    int Height,
    int RefreshRate,
    string MonitorName,
    string CustomSerial);

// =========================================================================================
// CONSOLE SESSION WATCHDOG (Session 0 cannot see the desktop — see
// TouchMappingAgent.Service.HealthMonitoring.ConsoleDisplayHeartbeatState's remarks)
// =========================================================================================

/// <summary>
/// Sent periodically by the interactive WPF client (piggy-backed on the existing re-application
/// poll in TouchMappingAgent.WPF.Services.ReapplyAgent) so the service's console session
/// watchdog can tell a genuinely healthy "0 displays" moment (nobody is at the console;
/// harmless) apart from a hung DWM in an actually blind Session 0 — the service has no way to
/// measure its own display count.
///
/// Carries the sending process's own Windows session id: the client's HKLM Run-key autostart
/// launches one instance per interactive logon session (console AND every RDP session alike),
/// so the service must keep one heartbeat per session rather than a single shared slot — see
/// TouchMappingAgent.Service.HealthMonitoring.ConsoleDisplayHeartbeatState's remarks.
/// </summary>
public record ConsoleDisplayHeartbeatRequest(int MonitorCount, int SessionId);

/// <summary>Acknowledges receipt of a display heartbeat.</summary>
public record ConsoleDisplayHeartbeatResponse(bool Acknowledged);

/// <summary>
/// Manual operator override: immediately runs the same guarded console-session reset the
/// watchdog would run automatically (see "Diagnose &gt; Konsolen-Sitzung zurücksetzen" in the
/// tray menu), bypassing the boot hysteresis and debounce timers but NOT the underlying safety
/// checks — it will still refuse to touch anything but the physical console.
/// </summary>
public record ForceConsoleSessionResetRequest;

/// <summary>Result of a manual console-session reset request.</summary>
public record ForceConsoleSessionResetResponse(
    bool Success,
    string Detail);
