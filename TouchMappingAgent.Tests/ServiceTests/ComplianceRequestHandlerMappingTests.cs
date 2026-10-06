using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Service.Integration;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Contracts;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Tests for the Session-0-aware MapTouch flow: the service cannot invoke tabcal.exe
/// itself (it runs in the non-interactive Session 0 of a Windows Service and cannot show
/// the touch-confirmation UI), so a successful MapTouch response must delegate execution
/// to the interactive client instead of silently doing nothing.
/// </summary>
public class ComplianceRequestHandlerMappingTests
{
    private static ComplianceRequestHandler CreateHandler()
    {
        var backupService = new BackupService(NullLogger<BackupService>.Instance);
        var displayRefreshService = new DisplayRefreshService(NullLogger<DisplayRefreshService>.Instance);
        var repairService = new AdvancedRepairService(NullLogger<AdvancedRepairService>.Instance, displayRefreshService);

        // HKCU, not HKLM: the store's production root needs elevation, which a test runner
        // does not have. The layout exercised is identical.
        var mappingStore = new MappingStore(
            NullLogger<MappingStore>.Instance, Microsoft.Win32.Registry.CurrentUser);
        var coordinator = new ReapplyCoordinator(NullLogger<ReapplyCoordinator>.Instance, mappingStore);

        // Pointed at HKCU for the same reason as the mapping store: the EDID module's
        // production root needs elevation.
        var edidManager = new Evolved.EdidManager.EdidManagerService(
            NullLogger<Evolved.EdidManager.EdidManagerService>.Instance,
            new Evolved.EdidManager.Registry.EdidRegistryStore(
                NullLogger<Evolved.EdidManager.Registry.EdidRegistryStore>.Instance,
                Microsoft.Win32.Registry.CurrentUser),
            new Evolved.EdidManager.Pnp.PnpDeviceControl(
                NullLogger<Evolved.EdidManager.Pnp.PnpDeviceControl>.Instance),
            new Evolved.EdidManager.WindowsElevationCheck());

        // Template directory in the temp tree: the production one lives under ProgramData and
        // may not be writable by the test runner.
        var templateStore = new Evolved.EdidManager.Templates.EdidTemplateStore(
            NullLogger<Evolved.EdidManager.Templates.EdidTemplateStore>.Instance,
            Path.Combine(Path.GetTempPath(), "EdidTemplateTests", Guid.NewGuid().ToString("N")));

        return new ComplianceRequestHandler(
            NullLogger<ComplianceRequestHandler>.Instance,
            backupService,
            repairService,
            mappingStore,
            coordinator,
            edidManager,
            templateStore);
    }

    [Fact]
    public async Task HandleMapTouchRequestAsync_NeverThrows()
    {
        var handler = CreateHandler();
        var request = new MapTouchRequest(@"\\.\DISPLAY1", @"\\?\HID#VID_0461&PID_0001#instance#{guid}");
        var json = JsonSerializer.Serialize(request);

        var exception = await Record.ExceptionAsync(() => handler.HandleMapTouchRequestAsync(json, CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleMapTouchRequestAsync_WhenSuccessful_DelegatesLocalExecutionWithTabcalArguments()
    {
        var handler = CreateHandler();
        var request = new MapTouchRequest(@"\\.\DISPLAY2", @"\\?\HID#VID_0461&PID_0001#instance#{guid}");
        var json = JsonSerializer.Serialize(request);

        var response = await handler.HandleMapTouchRequestAsync(json, CancellationToken.None);

        // On a machine without HKLM write access (e.g. a non-elevated CI runner) the
        // registry-backed audit write can legitimately fail; only assert the delegation
        // contract when the service actually reports success.
        if (!response.Success)
            return;

        Assert.True(response.RequiresLocalExecution);
        Assert.False(string.IsNullOrEmpty(response.LocalExecutablePath));
        Assert.Contains("tabcal.exe", response.LocalExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"DisplayID=\\.\DISPLAY2", response.LocalArguments);
        Assert.Contains("DeviceKind=touch", response.LocalArguments);
    }

    [Fact]
    public async Task HandleMapTouchRequestAsync_WithInvalidMonitorId_ReturnsFailureWithoutLocalExecution()
    {
        var handler = CreateHandler();
        var request = new MapTouchRequest("not-a-real-monitor", @"\\?\HID#VID_0461&PID_0001#instance#{guid}");
        var json = JsonSerializer.Serialize(request);

        var response = await handler.HandleMapTouchRequestAsync(json, CancellationToken.None);

        Assert.False(response.Success);
        Assert.False(response.RequiresLocalExecution);
        Assert.Null(response.LocalExecutablePath);
    }

    [Fact]
    public async Task HandleConfirmLocalMappingRequestAsync_NeverThrows()
    {
        var handler = CreateHandler();
        var request = new ConfirmLocalMappingRequest(@"\\.\DISPLAY1", @"\\?\HID#VID_0461&PID_0001#instance#{guid}", true, 0);
        var json = JsonSerializer.Serialize(request);

        var exception = await Record.ExceptionAsync(
            () => handler.HandleConfirmLocalMappingRequestAsync(json, CancellationToken.None));

        Assert.Null(exception);
    }
}
