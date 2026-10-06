using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Covers <see cref="TouchAlignmentService"/> — the single place "identify which digitizer is on
/// this monitor, then save it" now lives, extracted out of both the main window's
/// "Bildschirm anlernen" and the setup wizard's touch-assignment step so they cannot drift apart
/// the way they did before (the main window had grown a second, weaker path — "Zuordnung manuell
/// setzen" — that skipped the hardware-anchor check entirely; see <see cref="MapTouchResponse"/>-
/// era history for that story).
///
/// NOT covered here: <see cref="TouchIdentifyOutcome.Success"/>,
/// <see cref="TouchIdentifyOutcome.DeviceUnmatched"/> and <see cref="TouchIdentifyOutcome.UnstableAnchor"/>.
/// <see cref="TouchAlignmentService.IdentifyAsync"/> calls
/// <c>TouchDigitizerEnumerator.EnumerateDigitizers()</c> — a real SetupAPI enumeration with no
/// seam to mock — so which of those three outcomes a given device path produces depends on what
/// is physically plugged into the machine running the test. This is the same, already-accepted
/// limitation documented on <c>SetupWizardTests.TouchAssignment_CompletesOnlyWhenEveryMonitorIsLearned</c>.
/// Only <see cref="TouchIdentifyOutcome.Cancelled"/> is reachable without hardware, because it
/// returns before the enumeration ever runs.
/// </summary>
public class TouchAlignmentServiceTests
{
    private static readonly MonitorInfo Monitor = new(
        DeviceId: @"\\.\DISPLAY2", DisplayName: "DM7000", Width: 1920, Height: 1080,
        RefreshRate: 59, ConnectorLabel: "DP-2", AdapterId: "00000000-0001F416", TargetId: 250116,
        DevicePath: @"\\?\DISPLAY#CHR8910#5&a&0&UID250116#{guid}");

    private static readonly HidDeviceInfo Device = new(
        DevicePath: @"\\?\HID#VID_14E1&PID_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        ProductName: "PM1715",
        VendorId: 0x14E1,
        ProductId: 0x3508,
        ParentInstanceId: "7&1b9afb93&0&6");

    private static TouchAlignmentService CreateService(
        FakeSetupInteractionService interaction, Mock<INamedPipeClient> pipe) =>
        new(interaction, pipe.Object, NullLogger<TouchAlignmentService>.Instance);

    // ---------------------------------------------------------------- IdentifyAsync

    [Fact]
    public async Task IdentifyAsync_WhenNoTouchArrives_ReturnsCancelled()
    {
        var interaction = new FakeSetupInteractionService();
        interaction.TouchByConnector[Monitor.ConnectorLabel] = null;
        var service = CreateService(interaction, new Mock<INamedPipeClient>());

        var result = await service.IdentifyAsync(Monitor);

        Assert.Equal(TouchIdentifyOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Device);
    }

    [Fact]
    public async Task IdentifyAsync_WhenNoTouchArrives_DoesNotEnumerateOrSave()
    {
        // The point of returning early on "no touch": a cancelled learn attempt must not touch
        // the digitizer enumeration or the pipe at all. There's no direct way to assert
        // EnumerateDigitizers() wasn't called, but SaveAsync never being reachable from this
        // path is exactly what the ViewModels rely on — this documents that contract.
        var interaction = new FakeSetupInteractionService();
        interaction.TouchByConnector[Monitor.ConnectorLabel] = string.Empty;
        var pipe = new Mock<INamedPipeClient>();
        var service = CreateService(interaction, pipe);

        await service.IdentifyAsync(Monitor);

        pipe.Verify(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>()), Times.Never);
    }

    // ---------------------------------------------------------------- SaveAsync

    [Fact]
    public async Task SaveAsync_SendsTheNineHardwareAnchorFields()
    {
        MapTouchRequest? captured = null;
        var pipe = new Mock<INamedPipeClient>();
        pipe.Setup(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>()))
            .Callback<object>(r => captured = (MapTouchRequest)r)
            .ReturnsAsync(new MapTouchResponse(true, null));

        var service = CreateService(new FakeSetupInteractionService(), pipe);

        await service.SaveAsync(Monitor, Device);

        Assert.NotNull(captured);
        Assert.Equal(Monitor.DeviceId, captured!.MonitorId);
        Assert.Equal(Device.DevicePath, captured.DeviceId);
        Assert.Equal(Device.HardwareKey, captured.TouchHardwareKey);
        Assert.Equal(Monitor.DevicePath, captured.MonitorHardwareKey);
        Assert.Equal(Monitor.ConnectorLabel, captured.MonitorConnectorLabel);
        Assert.Equal(Monitor.TargetId, captured.MonitorTargetId);
        Assert.Equal(Monitor.BoundsKey, captured.MonitorBoundsKey);
        Assert.Equal(Device.ProductName, captured.TouchProductName);
        Assert.Equal(Monitor.DisplayName, captured.MonitorFriendlyName);
    }

    [Fact]
    public async Task SaveAsync_WhenServiceAccepts_ReturnsSuccessWithTheResponse()
    {
        var pipe = new Mock<INamedPipeClient>();
        var response = new MapTouchResponse(true, null, "tabcal.exe", "LinCal", RequiresLocalExecution: true);
        pipe.Setup(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>())).ReturnsAsync(response);

        var service = CreateService(new FakeSetupInteractionService(), pipe);

        var result = await service.SaveAsync(Monitor, Device);

        Assert.True(result.Success);
        Assert.Same(response, result.Response);
        Assert.Null(result.ExceptionDetail);
    }

    [Fact]
    public async Task SaveAsync_WhenServiceRejects_ReturnsFailureWithTheResponse()
    {
        var pipe = new Mock<INamedPipeClient>();
        var response = new MapTouchResponse(false, "duplicate hardware anchor");
        pipe.Setup(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>())).ReturnsAsync(response);

        var service = CreateService(new FakeSetupInteractionService(), pipe);

        var result = await service.SaveAsync(Monitor, Device);

        Assert.False(result.Success);
        Assert.Same(response, result.Response);
        Assert.Null(result.ExceptionDetail);
    }

    /// <summary>
    /// The behaviour the wizard's original SaveAssignmentAsync already had and the main window's
    /// original SendMappingAsync did not: an IPC failure must not throw, so a caller looping over
    /// several monitors can skip just the failed one instead of aborting the whole run.
    /// </summary>
    [Fact]
    public async Task SaveAsync_WhenTheIpcCallThrows_ReturnsFaultedInsteadOfThrowing()
    {
        var pipe = new Mock<INamedPipeClient>();
        pipe.Setup(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("Could not connect to service. Is the service running?"));

        var service = CreateService(new FakeSetupInteractionService(), pipe);

        var result = await service.SaveAsync(Monitor, Device);

        Assert.False(result.Success);
        Assert.Null(result.Response);
        Assert.Contains("Could not connect to service", result.ExceptionDetail);
    }

    [Fact]
    public async Task SaveAsync_NullMonitor_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreateService(new FakeSetupInteractionService(), new Mock<INamedPipeClient>())
                .SaveAsync(null!, Device));

    [Fact]
    public async Task SaveAsync_NullDevice_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => CreateService(new FakeSetupInteractionService(), new Mock<INamedPipeClient>())
                .SaveAsync(Monitor, null!));
}
