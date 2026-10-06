using Microsoft.Extensions.DependencyInjection;
using TouchMappingAgent.WPF.Views;

namespace TouchMappingAgent.WPF.Services;

/// <summary>
/// Creates the agent's secondary windows and the diagnostics exporter on demand.
///
/// The tray manager needs these but must not depend on the container directly, and it must not
/// hold them as constructor dependencies either: the EDID window is opened rarely, and
/// constructing it eagerly would pull a ViewModel — and a service round trip — into every
/// application start, silent ones included.
/// </summary>
public interface IAgentWindowFactory
{
    /// <summary>Creates a fresh EDID and display manager window.</summary>
    EdidManagerWindow CreateEdidManagerWindow();

    /// <summary>Creates a fresh setup wizard window.</summary>
    SetupWizardWindow CreateSetupWizardWindow();

    /// <summary>Creates the diagnostics exporter.</summary>
    DiagnosticsExporter CreateDiagnosticsExporter();
}

/// <summary>Container-backed implementation.</summary>
public sealed class AgentWindowFactory : IAgentWindowFactory
{
    private readonly IServiceProvider _services;

    /// <summary>Initializes a new instance of <see cref="AgentWindowFactory"/>.</summary>
    /// <param name="services">Service provider used to resolve the window's dependencies.</param>
    public AgentWindowFactory(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc/>
    public EdidManagerWindow CreateEdidManagerWindow() =>
        new(_services.GetRequiredService<ViewModels.EdidManagerViewModel>());

    /// <inheritdoc/>
    public SetupWizardWindow CreateSetupWizardWindow() =>
        new(_services.GetRequiredService<ViewModels.SetupWizardViewModel>());

    /// <inheritdoc/>
    public DiagnosticsExporter CreateDiagnosticsExporter() =>
        _services.GetRequiredService<DiagnosticsExporter>();
}

