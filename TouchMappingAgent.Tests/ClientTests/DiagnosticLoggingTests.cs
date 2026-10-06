using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Services;
using TouchMappingAgent.WPF.ViewModels;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Regression tests for the client's diagnostic logging path.
///
/// MonitorMappingViewModel.LogError used to run its message through string.Format. Every
/// message in this codebase uses structured-logging placeholders ("{MonitorId}"), which
/// string.Format rejects — it expects an ASCII digit after '{' and throws FormatException.
/// Because every LogError call site sits inside a catch block, that throw escaped the
/// handler; with no DispatcherUnhandledException handler installed, CommunityToolkit's
/// AsyncRelayCommand rethrow then terminated the whole application. Clicking "Bildschirm
/// anlernen" while the background service was stopped was enough to trigger it.
/// </summary>
public class DiagnosticLoggingTests
{
    private static MonitorMappingViewModel CreateViewModel(
        Mock<INamedPipeClient> pipeClient,
        ITouchAlignmentService? alignment = null) =>
        new(pipeClient.Object, NullLogger<MonitorMappingViewModel>.Instance, new TestLocalizer(),
            alignment ?? new FakeTouchAlignmentService());

    [Theory]
    [InlineData("MapTouchDevice failed for MonitorId={MonitorId}, DeviceId={DeviceId}")]
    [InlineData("ConfirmLocalMapping failed for MonitorId={MonitorId}, DeviceId={DeviceId}")]
    public void RenderTemplate_WithNamedPlaceholders_DoesNotThrow(string template)
    {
        var exception = Record.Exception(() =>
            MonitorMappingViewModel.RenderTemplate(template, @"\\.\DISPLAY2", "device-path"));

        Assert.Null(exception);
    }

    [Fact]
    public void RenderTemplate_SubstitutesArgumentsPositionally()
    {
        var rendered = MonitorMappingViewModel.RenderTemplate(
            "MapTouchDevice failed for MonitorId={MonitorId}, DeviceId={DeviceId}",
            @"\\.\DISPLAY2",
            @"\\?\HID#VID_14E1&PID_3508");

        Assert.Equal(
            @"MapTouchDevice failed for MonitorId=\\.\DISPLAY2, DeviceId=\\?\HID#VID_14E1&PID_3508",
            rendered);
    }

    [Fact]
    public void RenderTemplate_WithNoArguments_ReturnsTemplateUnchanged()
    {
        const string template = "LoadTouchDevices failed";

        Assert.Equal(template, MonitorMappingViewModel.RenderTemplate(template));
    }

    [Fact]
    public void RenderTemplate_WithEscapedBraces_EmitsLiteralBraces()
    {
        var rendered = MonitorMappingViewModel.RenderTemplate("{{literal}} {Value}", 42);

        Assert.Equal("{literal} 42", rendered);
    }

    [Fact]
    public void RenderTemplate_WithMissingArgument_DoesNotThrow()
    {
        var rendered = MonitorMappingViewModel.RenderTemplate("{First} {Second}", "only-one");

        Assert.Equal("only-one (missing)", rendered);
    }

    [Fact]
    public void RenderTemplate_WithNullArgument_DoesNotThrow()
    {
        var rendered = MonitorMappingViewModel.RenderTemplate("MonitorId={MonitorId}", new object?[] { null });

        Assert.Equal("MonitorId=(null)", rendered);
    }

    /// <summary>
    /// The end-to-end shape of the original crash: the service is unreachable, the IPC call
    /// throws, and the failure is logged. That must leave the ViewModel in a reportable state
    /// instead of letting an exception escape the command.
    ///
    /// Uses <see cref="FakeTouchAlignmentService"/>, not the real <see cref="TouchAlignmentService"/>,
    /// for the identify half — its identify phase calls a real, unmockable SetupAPI enumeration
    /// (see the fake's doc comment) — but proves the exact mechanism that replaced this
    /// ViewModel's own try/catch around the IPC call: <see cref="TouchSaveResult.ExceptionDetail"/>
    /// reaching the operator-visible diagnostics log.
    /// </summary>
    [Fact]
    public async Task LearnSelectedMonitor_WhenServiceUnreachable_ReportsInsteadOfThrowing()
    {
        var pipeClient = new Mock<INamedPipeClient>();
        var alignment = new FakeTouchAlignmentService
        {
            SaveResult = TouchSaveResult.Faulted(
                "InvalidOperationException: Could not connect to service. Is the service running?")
        };

        var viewModel = CreateViewModel(pipeClient, alignment);
        viewModel.SelectedMonitor = new MonitorInfo(
            DeviceId: @"\\.\DISPLAY2", DisplayName: "DM7000", Width: 1920, Height: 1080,
            RefreshRate: 59, X: 0, Y: 0, ConnectorLabel: "DP-2",
            AdapterId: "00000000-0001F416", TargetId: 250116);

        var exception = await Record.ExceptionAsync(() => viewModel.LearnSelectedMonitorCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.False(viewModel.IsLoading);
        Assert.Contains("Could not connect to service", viewModel.LogText);
    }

    /// <summary>
    /// The hook the application-level DispatcherUnhandledException handler calls, so an
    /// unhandled UI error still reaches the operator-visible diagnostics log.
    /// </summary>
    [Fact]
    public void ReportUnhandledException_AppendsToDiagnosticLog()
    {
        var viewModel = CreateViewModel(new Mock<INamedPipeClient>());

        viewModel.ReportUnhandledException(
            new InvalidOperationException("outer", new FormatException("inner")));

        Assert.Contains("UNHANDLED", viewModel.LogText);
        Assert.Contains("InvalidOperationException: outer", viewModel.LogText);
        Assert.Contains("FormatException: inner", viewModel.LogText);
    }
}
