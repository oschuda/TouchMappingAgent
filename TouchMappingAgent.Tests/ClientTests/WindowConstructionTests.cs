using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.ViewModels;
using TouchMappingAgent.WPF.Views;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Constructs the agent's windows for real.
///
/// A XAML fault does not fail the build — a missing StaticResource, a typo'd pack URI, a
/// converter that was never registered. It throws when the window is constructed, i.e. the
/// first time an operator opens the dialog on the target machine. These tests are the only
/// thing standing between that and a shipped release.
/// </summary>
[Collection(WpfCollection.Name)]
public class WindowConstructionTests
{
    private readonly WpfApplicationFixture _wpf;

    public WindowConstructionTests(WpfApplicationFixture wpf) => _wpf = wpf;

    private static EdidManagerViewModel CreateEdidViewModel()
    {
        var pipeClient = new Mock<INamedPipeClient>();

        // Every call fails: the window must construct with no running service, which is exactly
        // the situation an operator is in when they open it to diagnose one.
        pipeClient
            .Setup(c => c.SendAsync<GetEdidStatusResponse>(It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("service offline"));

        return new EdidManagerViewModel(
            pipeClient.Object, NullLogger<EdidManagerViewModel>.Instance, new TestLocalizer());
    }

    /// <summary>
    /// App.xaml gained a resource dictionary, which makes InitializeComponent() mandatory in the
    /// App constructor. Without it the merged styles never load and every window below throws.
    /// </summary>
    [Theory]
    [InlineData("Evolved.Background")]
    [InlineData("Evolved.Surface")]
    [InlineData("Evolved.Button")]
    [InlineData("Evolved.Button.Primary")]
    [InlineData("Evolved.Button.Danger")]
    [InlineData("Evolved.Card")]
    [InlineData("Evolved.TextBox")]
    [InlineData("Evolved.ListView")]
    [InlineData("Evolved.GridViewColumnHeader")]
    [InlineData("Evolved.Text")]
    [InlineData("Evolved.Text.Muted")]
    [InlineData("Evolved.Text.Heading")]
    [InlineData("Evolved.Text.Title")]
    [InlineData("InverseBool")]
    [InlineData("NullToCollapsed")]
    [InlineData("StepVisibility")]
    [InlineData("EnumToBool")]
    [InlineData("BoolToYesNo")]
    [InlineData("BoolToElevation")]
    [InlineData("BoolToOkFail")]
    public void Application_ExposesEveryResourceTheWindowsReference(string key)
    {
        Assert.NotNull(_wpf.FindResource(key));
    }

    /// <summary>
    /// The wizard's XAML is the largest in the product and binds against five converters; a
    /// typo in any of them only surfaces when a technician opens it on the machine.
    /// </summary>
    [Fact]
    public void SetupWizardWindow_Constructs()
    {
        _wpf.Invoke(() =>
        {
            var pipeClient = new Mock<INamedPipeClient>();
            pipeClient
                .Setup(c => c.SendAsync<GetEdidStatusResponse>(It.IsAny<object>()))
                .ThrowsAsync(new InvalidOperationException("service offline"));

            var elevation = new Mock<TouchMappingAgent.WPF.Services.IElevationService>();
            var interaction = new Mock<TouchMappingAgent.WPF.Services.ISetupInteractionService>();
            var alignment = new Mock<TouchMappingAgent.WPF.Services.ITouchAlignmentService>();

            var viewModel = new SetupWizardViewModel(
                pipeClient.Object, elevation.Object, interaction.Object,
                NullLogger<SetupWizardViewModel>.Instance, new TestLocalizer(),
                new FakeTabcalRunner(), alignment.Object);

            var window = new SetupWizardWindow(viewModel);
            Assert.NotNull(window.DataContext);
            window.Close();
        });
    }

    [Fact]
    public void EdidManagerWindow_Constructs()
    {
        _wpf.Invoke(() =>
        {
            var window = new EdidManagerWindow(CreateEdidViewModel());
            Assert.NotNull(window.DataContext);
            window.Close();
        });
    }

    [Fact]
    public void HelpWindow_Constructs()
    {
        _wpf.Invoke(() =>
        {
            var window = new HelpWindow();
            window.Close();
        });
    }

    [Fact]
    public void MainWindow_Constructs()
    {
        _wpf.Invoke(() =>
        {
            var window = new TouchMappingAgent.WPF.MainWindow();
            window.Close();
        });
    }

    [Fact]
    public void LogWindow_Constructs()
    {
        _wpf.Invoke(() =>
        {
            var pipeClient = new Mock<INamedPipeClient>();
            var alignment = new Mock<TouchMappingAgent.WPF.Services.ITouchAlignmentService>();
            var viewModel = new MonitorMappingViewModel(
                pipeClient.Object, NullLogger<MonitorMappingViewModel>.Instance, new TestLocalizer(),
                alignment.Object);

            var window = new LogWindow(viewModel);
            window.Close();
        });
    }

    /// <summary>
    /// The identify window positions itself with SetWindowPos; it must survive a monitor record
    /// that carries no bounds rather than throwing on the division that implies.
    /// </summary>
    [Fact]
    public void IdentifyWindow_ConstructsForAMonitorWithoutBounds()
    {
        _wpf.Invoke(() =>
        {
            var monitor = new Shared.Models.MonitorInfo(@"\\.\DISPLAY1", "DM7000", 0, 0, 60);

            var window = new IdentifyWindow(monitor);
            window.Close();
        });
    }

    /// <summary>
    /// The EDID window must render its port list without a service: an operator opening it to
    /// find out why nothing works would otherwise be met with a crash.
    /// </summary>
    [Fact]
    public async Task EdidManagerViewModel_SurvivesAnUnreachableService()
    {
        var pipeClient = new Mock<INamedPipeClient>();
        pipeClient
            .Setup(c => c.SendAsync<GetEdidStatusResponse>(It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("service offline"));

        var viewModel = new EdidManagerViewModel(
            pipeClient.Object, NullLogger<EdidManagerViewModel>.Instance, new TestLocalizer());

        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsBusy);
        Assert.Equal(LocalizationKeys.EdidUi_ServiceUnreachable, viewModel.StatusMessage);
    }
}
