using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Tests for the automatic re-application decision.
///
/// The behaviour that matters operationally: after a hardware change (reboot, extender
/// power-cycle, cable reseat) every stored assignment must be queued again, and once applied
/// it must NOT be queued again until the hardware changes once more — otherwise the client
/// would re-run tabcal.exe on every poll, several times a minute, forever.
/// </summary>
public class ReapplyCoordinatorTests : IDisposable
{
    private const string TouchParentA = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";
    private const string Dp3Path = @"\\?\DISPLAY#CHR8910#5&2C72B841&0&UID250118";

    private readonly RegistryKey _testRoot;
    private readonly string _testRootName;
    private readonly MappingStore _store;
    private readonly ReapplyCoordinator _coordinator;

    public ReapplyCoordinatorTests()
    {
        _testRootName = $@"Software\PadaLumaTests\{Guid.NewGuid():N}";
        _testRoot = Registry.CurrentUser.CreateSubKey(_testRootName, writable: true)!;
        _store = new MappingStore(NullLogger<MappingStore>.Instance, _testRoot);
        _coordinator = new ReapplyCoordinator(NullLogger<ReapplyCoordinator>.Instance, _store);
    }

    public void Dispose()
    {
        _testRoot.Dispose();
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_testRootName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best effort.
        }

        GC.SuppressFinalize(this);
    }

    private static MonitorInfo Dp3() => new(
        @"\\.\DISPLAY3", "DM7000", 1920, 1080, 59,
        X: 1920, Y: 0, ConnectorLabel: "DP-3", AdapterId: "00000000-0001F416", TargetId: 250118,
        DevicePath: Dp3Path + "#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");

    private static HidDeviceInfo TouchA() => new(
        DevicePath: @"\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: @"HID\VID_14E1&PID_3508\8&19abaff7&0&0000",
        ParentInstanceId: TouchParentA);

    private void SeedMapping() => _store.Save(new TouchMapping(
        TouchHardwareKey: HidDeviceInfo.NormalizeInstanceId(TouchParentA),
        MonitorHardwareKey: Dp3Path,
        MonitorConnectorLabel: "DP-3",
        MonitorTargetId: 250118,
        MonitorBoundsKey: "1920,0,1920,1080",
        TouchProductName: "PM1715",
        MonitorFriendlyName: "DM7000"));

    [Fact]
    public void GetPending_QueuesStoredAssignment_WithRuntimeIdentifiers()
    {
        SeedMapping();

        var pending = _coordinator.GetPending(new[] { TouchA() }, new[] { Dp3() });

        var item = Assert.Single(pending);
        Assert.Equal("DP-3", item.MonitorConnectorLabel);
        Assert.Equal(@"\\.\DISPLAY3", item.MonitorDeviceId);
        Assert.Equal(TouchA().DevicePath, item.TouchDevicePath);
        Assert.Equal(MappingMatchQuality.Exact, item.MatchQuality);
        Assert.Contains(@"DisplayID=\\.\DISPLAY3", item.LocalArguments);
    }

    [Fact]
    public void GetPending_ReturnsEmpty_WhenNothingStored()
    {
        Assert.Empty(_coordinator.GetPending(new[] { TouchA() }, new[] { Dp3() }));
    }

    [Fact]
    public void GetPending_StopsQueueing_AfterSuccessfulApplication()
    {
        SeedMapping();
        var digitizers = new[] { TouchA() };
        var monitors = new[] { Dp3() };

        Assert.Single(_coordinator.GetPending(digitizers, monitors));

        _coordinator.ReportResult(HidDeviceInfo.NormalizeInstanceId(TouchParentA), true, 0, null);

        Assert.Empty(_coordinator.GetPending(digitizers, monitors));
    }

    /// <summary>
    /// A failed run must stay queued: tabcal.exe failing because the client was not elevated
    /// is a transient condition, and silently dropping the work would leave the operator with
    /// touches on the wrong screen and nothing retrying.
    /// </summary>
    [Fact]
    public void GetPending_KeepsQueueing_AfterFailedApplication()
    {
        SeedMapping();
        var digitizers = new[] { TouchA() };
        var monitors = new[] { Dp3() };

        _coordinator.GetPending(digitizers, monitors);
        _coordinator.ReportResult(HidDeviceInfo.NormalizeInstanceId(TouchParentA), false, 1, "tabcal failed");

        Assert.Single(_coordinator.GetPending(digitizers, monitors));
    }

    /// <summary>
    /// The core recovery behaviour: the monitor arrangement changed (the extenders came back
    /// with a different layout), so the assignment must be asserted again even though it was
    /// already applied in the previous configuration.
    /// </summary>
    [Fact]
    public void GetPending_RequeuesEverything_WhenHardwareGenerationChanges()
    {
        SeedMapping();
        var digitizers = new[] { TouchA() };

        _coordinator.GetPending(digitizers, new[] { Dp3() });
        _coordinator.ReportResult(HidDeviceInfo.NormalizeInstanceId(TouchParentA), true, 0, null);
        Assert.Empty(_coordinator.GetPending(digitizers, new[] { Dp3() }));

        // Same panel, moved in the desktop arrangement.
        var moved = Dp3() with { X = 0, Y = 0 };

        Assert.Single(_coordinator.GetPending(digitizers, new[] { moved }));
    }

    [Fact]
    public void InvalidateAppliedState_RequeuesWithoutAHardwareChange()
    {
        SeedMapping();
        var digitizers = new[] { TouchA() };
        var monitors = new[] { Dp3() };

        _coordinator.GetPending(digitizers, monitors);
        _coordinator.ReportResult(HidDeviceInfo.NormalizeInstanceId(TouchParentA), true, 0, null);
        Assert.Empty(_coordinator.GetPending(digitizers, monitors));

        _coordinator.InvalidateAppliedState("test");

        Assert.Single(_coordinator.GetPending(digitizers, monitors));
    }

    [Fact]
    public void GetPending_SkipsAssignmentsWhoseDigitizerIsAbsent()
    {
        SeedMapping();

        Assert.Empty(_coordinator.GetPending(Array.Empty<HidDeviceInfo>(), new[] { Dp3() }));
    }

    [Fact]
    public void GetPending_SkipsAssignmentsWhoseMonitorIsAbsent()
    {
        SeedMapping();

        Assert.Empty(_coordinator.GetPending(new[] { TouchA() }, Array.Empty<MonitorInfo>()));
    }

    [Fact]
    public void ComputeGeneration_IsStableForTheSameHardware()
    {
        var a = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { Dp3() });
        var b = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { Dp3() });

        Assert.Equal(a, b);
    }

    [Fact]
    public void ComputeGeneration_IsOrderIndependent()
    {
        var monitorA = Dp3();
        var monitorB = Dp3() with { DeviceId = @"\\.\DISPLAY2", ConnectorLabel = "DP-2", X = 0 };

        var forward = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { monitorA, monitorB });
        var reversed = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { monitorB, monitorA });

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void ComputeGeneration_ChangesWhenBoundsChange()
    {
        var before = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { Dp3() });
        var after = ReapplyCoordinator.ComputeGeneration(new[] { TouchA() }, new[] { Dp3() with { X = 0 } });

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DescribeStatus_ReportsMissingMappingsAsAFinding()
    {
        var status = _coordinator.DescribeStatus(new[] { TouchA() }, new[] { Dp3() });

        Assert.Equal(0, status.MappingCount);
        Assert.Equal(1, status.DigitizerCount);
        Assert.NotEmpty(status.Findings);
    }

    [Fact]
    public void DescribeStatus_ReportsUnresolvableMappings()
    {
        SeedMapping();

        var status = _coordinator.DescribeStatus(new[] { TouchA() }, Array.Empty<MonitorInfo>());

        Assert.Equal(1, status.MappingCount);
        Assert.Equal(0, status.ResolvedMappingCount);
        Assert.Contains(status.Findings, f => f.Key == MessageKeys.Status_MappingsUnresolvable);
    }

    [Fact]
    public void DescribeStatus_IsCleanWhenEverythingResolves()
    {
        SeedMapping();

        var status = _coordinator.DescribeStatus(new[] { TouchA() }, new[] { Dp3() });

        Assert.Equal(1, status.MappingCount);
        Assert.Equal(1, status.ResolvedMappingCount);
        Assert.Equal(0, status.AmbiguousTouchDeviceCount);
        Assert.Empty(status.Findings);
    }
}
