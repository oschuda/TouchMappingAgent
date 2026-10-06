using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Identifies which digitizer is physically wired to a monitor, and persists that pairing with
/// the service.
///
/// WHY THIS EXISTS: the main window's "Bildschirm anlernen" button and the setup wizard's
/// touch-assignment step used to carry two independent, hand-copied implementations of the same
/// sequence — show a full-screen touch prompt, match the raw-input answer against an enumerated
/// digitizer, reject one with no stable hardware anchor, then send the nine-field
/// <see cref="MapTouchRequest"/> that anchors the mapping to the digitizer's USB port and the
/// monitor's graphics connector. One of the two copies (the main window's "Zuordnung manuell
/// setzen") had drifted into a materially weaker variant that skipped the anchor check entirely.
/// This type is the one place that sequence now lives.
///
/// WHAT DELIBERATELY STAYS OUTSIDE: whether to run tabcal.exe immediately after a successful
/// save. The main window's single-shot learn action needs to (there is no separate calibration
/// step in that UI), while the wizard's step 4 must NOT (it defers every calibration to its own
/// step 5, which calibrates every learned monitor in one pass and lets the operator confirm the
/// pointer visually). That decision belongs to the caller, not to this service.
///
/// Likewise, "was this digitizer already claimed by another monitor in this run" — the wizard's
/// duplicate-anchor check when learning several monitors in a loop — stays the wizard's own
/// concern: it has to run between <see cref="IdentifyAsync"/> and <see cref="SaveAsync"/>, and
/// the main window's single-monitor action has no use for it at all.
/// </summary>
public interface ITouchAlignmentService
{
    /// <summary>
    /// Shows the full-screen touch prompt for one monitor and validates whatever digitizer
    /// answers. Persists nothing.
    /// </summary>
    Task<TouchIdentifyResult> IdentifyAsync(MonitorInfo monitor);

    /// <summary>
    /// Persists an already-identified monitor/digitizer pairing with the service. Never throws
    /// for an IPC failure — see <see cref="TouchSaveResult.ExceptionDetail"/> — so a caller
    /// looping over several monitors can let one failure skip just that monitor.
    /// </summary>
    Task<TouchSaveResult> SaveAsync(MonitorInfo monitor, HidDeviceInfo device);
}

/// <summary>Why <see cref="ITouchAlignmentService.IdentifyAsync"/> did or did not produce a device.</summary>
public enum TouchIdentifyOutcome
{
    /// <summary>A digitizer answered and has a stable hardware anchor.</summary>
    Success,

    /// <summary>No touch arrived before the prompt was dismissed (Esc, or the operator gave up).</summary>
    Cancelled,

    /// <summary>Raw input reported a device path no currently enumerated digitizer has.</summary>
    DeviceUnmatched,

    /// <summary>The digitizer answered but has neither a parent instance id nor an instance id.</summary>
    UnstableAnchor
}

/// <summary>Result of <see cref="ITouchAlignmentService.IdentifyAsync"/>.</summary>
/// <param name="Outcome">Which of the four cases occurred.</param>
/// <param name="Device">The identified digitizer; only set when <paramref name="Outcome"/> is
/// <see cref="TouchIdentifyOutcome.Success"/> or <see cref="TouchIdentifyOutcome.UnstableAnchor"/>
/// (the latter so a caller can still name the offending product in its own message).</param>
/// <param name="DiagnosticDetail">Unlocalized detail for the log; never shown to the operator.</param>
public sealed record TouchIdentifyResult(
    TouchIdentifyOutcome Outcome,
    HidDeviceInfo? Device,
    string? DiagnosticDetail)
{
    /// <summary>A digitizer answered and is safe to save.</summary>
    public static TouchIdentifyResult Success(HidDeviceInfo device) =>
        new(TouchIdentifyOutcome.Success, device, null);

    /// <summary>No touch arrived.</summary>
    public static TouchIdentifyResult Cancelled() =>
        new(TouchIdentifyOutcome.Cancelled, null, null);

    /// <summary>The touch did not match any enumerated digitizer.</summary>
    public static TouchIdentifyResult DeviceUnmatched(string detail) =>
        new(TouchIdentifyOutcome.DeviceUnmatched, null, detail);

    /// <summary>The digitizer that answered has no anchor a mapping could survive a reboot on.</summary>
    public static TouchIdentifyResult UnstableAnchor(HidDeviceInfo device, string detail) =>
        new(TouchIdentifyOutcome.UnstableAnchor, device, detail);
}

/// <summary>Result of <see cref="ITouchAlignmentService.SaveAsync"/>.</summary>
/// <param name="Success">True only when the service accepted the mapping.</param>
/// <param name="Response">
/// The service's response. Null only when the IPC call itself threw; otherwise present even on
/// rejection, so a caller can still read <see cref="MapTouchResponse.ErrorMessage"/>.
/// </param>
/// <param name="ExceptionDetail">
/// Unlocalized exception detail when the IPC call threw (e.g. the service is unreachable);
/// null otherwise. Callers that keep their own operator-visible diagnostics log (the main
/// window's <c>LogText</c>) should append this themselves — it is deliberately not localized
/// and not something an operator should see in a status line.
/// </param>
public sealed record TouchSaveResult(bool Success, MapTouchResponse? Response, string? ExceptionDetail)
{
    /// <summary>Wraps a response the service actually returned, successful or not.</summary>
    public static TouchSaveResult FromResponse(MapTouchResponse response) =>
        new(response.Success, response, null);

    /// <summary>The IPC call itself threw before any response arrived.</summary>
    public static TouchSaveResult Faulted(string detail) =>
        new(false, null, detail);
}
