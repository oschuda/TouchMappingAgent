using TouchMappingAgent.Shared.Models;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Every point where the setup wizard needs a human or a window.
///
/// Exists so the wizard's state machine can be tested. Without it the ViewModel would have to
/// construct an <c>IdentifyWindow</c>, block on a real touch and open file dialogs — none of
/// which a test can drive, which would leave the step sequencing and the failure paths
/// (operator cancels, no touch arrives, tabcal fails) covered by nothing at all.
/// </summary>
public interface ISetupInteractionService
{
    /// <summary>
    /// Shows the full-screen prompt on one monitor and returns the HID interface path of the
    /// digitizer that was touched, or null when the operator cancelled or nothing arrived.
    /// </summary>
    Task<string?> IdentifyTouchDeviceAsync(MonitorInfo monitor);

    /// <summary>Asks a yes/no question. Returns true for yes.</summary>
    Task<bool> ConfirmAsync(string message, string title);

    /// <summary>Shows an informational message.</summary>
    Task ShowMessageAsync(string message, string title);

    /// <summary>
    /// Asks for an EDID template file. Returns the chosen path, or null when cancelled.
    /// </summary>
    Task<string?> PickTemplateFileAsync(string? initialDirectory);

    /// <summary>
    /// Asks where to save the commissioning report. Returns the path, or null when cancelled.
    /// </summary>
    Task<string?> PickReportTargetAsync(string suggestedFileName);
}
