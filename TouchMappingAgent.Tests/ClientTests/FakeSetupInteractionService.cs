using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Services;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// A fake <see cref="ISetupInteractionService"/> for tests that need to drive the touch-identify
/// prompt, a confirmation dialog, or a file picker without a screen.
///
/// Shared between <c>SetupWizardTests</c> (which drives the wizard's own steps through it) and
/// <c>TouchAlignmentServiceTests</c> (which drives <see cref="TouchAlignmentService"/> — the
/// service the wizard's touch-assignment step now delegates to — through the same seam).
/// </summary>
internal sealed class FakeSetupInteractionService : ISetupInteractionService
{
    /// <summary>Device path returned per monitor connector; null means "no touch arrived".</summary>
    public Dictionary<string, string?> TouchByConnector { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Answer <see cref="ConfirmAsync"/> returns.</summary>
    public bool ConfirmResult { get; set; } = true;

    /// <summary>Every confirmation dialog title asked, in order.</summary>
    public List<string> ConfirmationsAsked { get; } = new();

    /// <summary>Path <see cref="PickTemplateFileAsync"/> returns.</summary>
    public string? TemplateToReturn { get; set; }

    /// <summary>Path <see cref="PickReportTargetAsync"/> returns.</summary>
    public string? ReportTargetToReturn { get; set; }

    /// <inheritdoc/>
    public Task<string?> IdentifyTouchDeviceAsync(MonitorInfo monitor) =>
        Task.FromResult(TouchByConnector.TryGetValue(monitor.ConnectorLabel, out var path) ? path : null);

    /// <inheritdoc/>
    public Task<bool> ConfirmAsync(string message, string title)
    {
        ConfirmationsAsked.Add(title);
        return Task.FromResult(ConfirmResult);
    }

    /// <inheritdoc/>
    public Task ShowMessageAsync(string message, string title) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<string?> PickTemplateFileAsync(string? initialDirectory) =>
        Task.FromResult(TemplateToReturn);

    /// <inheritdoc/>
    public Task<string?> PickReportTargetAsync(string suggestedFileName) =>
        Task.FromResult(ReportTargetToReturn);
}
