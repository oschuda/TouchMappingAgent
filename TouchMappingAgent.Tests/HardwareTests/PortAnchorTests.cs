using TouchMappingAgent.Service.Validation;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.HardwareTests;

/// <summary>
/// Tests for the port-anchored touch identity.
///
/// The values are the ones measured on the target installation on 2026-10-06. Both range
/// extender hubs (USB\VID_0000&amp;PID_0000) report the same serial number; Windows gives the
/// serial-based instance id to whichever enumerates first, so the USB parent instance id of a
/// digitizer depends on power-up order:
///
///   port USB(1)/USB(4)/USB(6)  (touch of DP-3):  7&amp;1b9afb93&amp;0&amp;6  or  7&amp;235bf858&amp;0&amp;6
///   port USB(2)/USB(4)/USB(6)  (touch of DP-2):  7&amp;1b9afb93&amp;0&amp;6  or  7&amp;19c16684&amp;0&amp;6
///
/// "7&amp;1b9afb93&amp;0&amp;6" can therefore name either screen. The location path cannot.
/// </summary>
public class PortAnchorTests
{
    private const string LocationDp3Side = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)#USB(4)#USB(6)";
    private const string LocationDp2Side = "PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USB(4)#USB(6)";

    private const string SerialDerivedParent = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";
    private const string PortDerivedParentDp3Side = @"USB\VID_14E1&PID_3508\7&235bf858&0&6";
    private const string PortDerivedParentDp2Side = @"USB\VID_14E1&PID_3508\7&19c16684&0&6";

    private static HidDeviceInfo Touch(string hidSuffix, string parent, string location) => new(
        DevicePath: $@"\\?\HID#VID_14E1&PID_3508#{hidSuffix}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
        ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: $@"HID\VID_14E1&PID_3508\{hidSuffix}",
        ParentInstanceId: parent,
        ParentLocationPath: location);

    /// <summary>Power-up order A: the DP-3-side extender wins the serial.</summary>
    private static List<HidDeviceInfo> OrderA() => new()
    {
        Touch("8&25188e6b&0&0000", SerialDerivedParent, LocationDp3Side),
        Touch("8&290d7833&0&0000", PortDerivedParentDp2Side, LocationDp2Side),
    };

    /// <summary>Power-up order B: the DP-2-side extender wins the serial.</summary>
    private static List<HidDeviceInfo> OrderB() => new()
    {
        Touch("8&19abaff7&0&0000", PortDerivedParentDp3Side, LocationDp3Side),
        Touch("8&25188e6b&0&0000", SerialDerivedParent, LocationDp2Side),
    };

    private static MonitorInfo Dp2() => new(
        DeviceId: @"\\.\DISPLAY3", DisplayName: "DM7000", Width: 1920, Height: 1080, RefreshRate: 59,
        X: 0, Y: 0, ConnectorLabel: "DP-2", AdapterId: "00000000-0001F416", TargetId: 250116,
        DevicePath: @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
        IsPrimary: true);

    private static MonitorInfo Dp3() => new(
        DeviceId: @"\\.\DISPLAY2", DisplayName: "DM7000", Width: 1920, Height: 1080, RefreshRate: 59,
        X: 1920, Y: 0, ConnectorLabel: "DP-3", AdapterId: "00000000-0001F416", TargetId: 250118,
        DevicePath: @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
        IsPrimary: false);

    private static TouchMapping MappingToDp3(string touchKey) => new(
        TouchHardwareKey: touchKey,
        MonitorHardwareKey: Dp3().HardwareKey,
        MonitorConnectorLabel: "DP-3",
        MonitorTargetId: 250118,
        MonitorBoundsKey: "1920,0,1920,1080");

    [Fact]
    public void HardwareKey_PrefersLocationPathOverParentInstanceId()
    {
        var device = Touch("8&25188e6b&0&0000", SerialDerivedParent, LocationDp3Side);

        Assert.Equal(
            @"PORT\VID_14E1&PID_3508\PCIROOT(0)/PCI(1400)/USBROOT(0)/USB(1)/USB(4)/USB(6)",
            device.HardwareKey);
        Assert.True(HidDeviceInfo.IsPortKey(device.HardwareKey));
        Assert.Equal(HidDeviceInfo.NormalizeInstanceId(SerialDerivedParent), device.LegacyHardwareKey);
    }

    [Fact]
    public void HardwareKey_FallsBackToParentInstanceId_WithoutLocationPath()
    {
        var device = Touch("8&25188e6b&0&0000", SerialDerivedParent, location: "");

        Assert.Equal(HidDeviceInfo.NormalizeInstanceId(SerialDerivedParent), device.HardwareKey);
        Assert.False(HidDeviceInfo.IsPortKey(device.HardwareKey));
    }

    [Fact]
    public void HardwareKey_IsTheSameForOnePhysicalSide_InBothPowerUpOrders()
    {
        var dp3SideA = OrderA().Single(d => d.ParentLocationPath == LocationDp3Side);
        var dp3SideB = OrderB().Single(d => d.ParentLocationPath == LocationDp3Side);

        // The legacy anchor changes with the power-up order ...
        Assert.NotEqual(dp3SideA.LegacyHardwareKey, dp3SideB.LegacyHardwareKey);
        // ... the port anchor does not.
        Assert.Equal(dp3SideA.HardwareKey, dp3SideB.HardwareKey);
    }

    /// <summary>
    /// The field incident: learned in order A, the extenders power-cycle into order B. The
    /// legacy anchor then resolved to the DP-2-side touch screen and applied it to DP-3,
    /// reporting success. The port anchor must keep resolving to the DP-3-side screen, using
    /// its CURRENT interface path.
    /// </summary>
    [Fact]
    public void Resolve_FollowsThePhysicalSide_AcrossAnExtenderPowerCycle()
    {
        var learnedOn = OrderA().Single(d => d.ParentLocationPath == LocationDp3Side);
        var mapping = MappingToDp3(learnedOn.HardwareKey);

        var resolved = MappingResolver.Resolve(mapping, OrderB(), new[] { Dp2(), Dp3() });

        Assert.NotNull(resolved);
        Assert.Equal(LocationDp3Side, resolved!.TouchDevice.ParentLocationPath);
        Assert.Contains("8&19abaff7", resolved.TouchDevice.DevicePath);
        Assert.Equal("DP-3", resolved.Monitor.ConnectorLabel);
    }

    [Fact]
    public void Resolve_NeverAppliesALegacyInstanceAnchor_ToPortAnchoredDevices()
    {
        // Under the legacy model this resolved to whichever side currently holds the serial.
        var mapping = MappingToDp3(HidDeviceInfo.NormalizeInstanceId(SerialDerivedParent));

        Assert.Null(MappingResolver.Resolve(mapping, OrderA(), new[] { Dp2(), Dp3() }));
        Assert.Null(MappingResolver.Resolve(mapping, OrderB(), new[] { Dp2(), Dp3() }));
    }

    [Fact]
    public void DisplayLabel_ShowsTheUsbPortChain()
    {
        var device = OrderA().Single(d => d.ParentLocationPath == LocationDp2Side);

        Assert.EndsWith("USB(2)/USB(4)/USB(6)", device.DisplayLabel);
    }

    [Fact]
    public void Validator_AcceptsAPortKeyAsTouchAnchor()
    {
        var mapping = MappingToDp3(OrderA()[0].HardwareKey);

        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapping(mapping)));
    }

    [Theory]
    [InlineData(@"PORT\VID_14E1&PID_3508\PCIROOT(0)/../USB(1)")]
    [InlineData(@"PORT\VID_14E1&PID_3508\PCIROOT(0)/USB(1);DROP")]
    public void Validator_StillRejectsTraversalAndForeignCharacters(string touchKey)
    {
        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapping(MappingToDp3(touchKey)));
    }
}
