using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.HardwareTests;

/// <summary>
/// Tests for the hardware identity model.
///
/// The values used here are the REAL ones measured on the target installation: two DM7000
/// panels behind DisplayPort-to-HDMI range extenders on DP-2 and DP-3, and two PM1715
/// digitizers on distinct USB controller ports. What makes the case hard is captured in the
/// first test: both monitors report the same emulated EDID (manufacturer CHR, product 35088,
/// serial 880) and the same block hash, so monitor identity alone cannot separate them.
/// </summary>
public class HardwareIdentityTests
{
    // Measured on the target machine (see the hardware survey).
    private const string Dp2DevicePath =
        @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Dp3DevicePath =
        @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string TouchParentA = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";
    private const string TouchParentB = @"USB\VID_14E1&PID_3508\7&235bf858&0&6";

    private static MonitorInfo Dp2() => new(
        DeviceId: @"\\.\DISPLAY2", DisplayName: "DM7000", Width: 1920, Height: 1080, RefreshRate: 59,
        X: 0, Y: 0, ConnectorLabel: "DP-2", AdapterId: "00000000-0001F416", TargetId: 250116,
        DevicePath: Dp2DevicePath, IsPrimary: true);

    private static MonitorInfo Dp3() => new(
        DeviceId: @"\\.\DISPLAY3", DisplayName: "DM7000", Width: 1920, Height: 1080, RefreshRate: 59,
        X: 1920, Y: 0, ConnectorLabel: "DP-3", AdapterId: "00000000-0001F416", TargetId: 250118,
        DevicePath: Dp3DevicePath, IsPrimary: false);

    private static HidDeviceInfo TouchA() => new(
        DevicePath: @"\\?\HID#VID_14E1&PID_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: @"HID\VID_14E1&PID_3508\8&25188e6b&0&0000",
        ParentInstanceId: TouchParentA);

    private static HidDeviceInfo TouchB() => new(
        DevicePath: @"\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
        ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: @"HID\VID_14E1&PID_3508\8&19abaff7&0&0000",
        ParentInstanceId: TouchParentB);

    /// <summary>
    /// The premise of the whole design: the two panels are identical in every respect the
    /// monitor itself reports, and differ only in where they are plugged in.
    /// </summary>
    [Fact]
    public void IdenticalPanels_DifferOnlyInConnectorAndPosition()
    {
        var left = Dp2();
        var right = Dp3();

        Assert.Equal(left.DisplayName, right.DisplayName);
        Assert.Equal(left.Width, right.Width);
        Assert.Equal(left.Height, right.Height);

        Assert.NotEqual(left.ConnectorLabel, right.ConnectorLabel);
        Assert.NotEqual(left.HardwareKey, right.HardwareKey);
        Assert.NotEqual(left.BoundsKey, right.BoundsKey);
    }

    [Fact]
    public void MonitorHardwareKey_UsesDevicePath_AndStripsInterfaceGuid()
    {
        var monitor = Dp3();

        Assert.Equal(@"\\?\DISPLAY#CHR8910#5&2C72B841&0&UID250118", monitor.HardwareKey);
        Assert.DoesNotContain("{", monitor.HardwareKey);
        Assert.True(monitor.HasHardwareIdentity);
    }

    /// <summary>
    /// The adapter LUID is regenerated on every boot. Including it in the key would invalidate
    /// every stored assignment at the next restart — the exact failure mode this phase exists
    /// to remove — so it must stay out.
    /// </summary>
    [Fact]
    public void MonitorHardwareKey_DoesNotIncludeAdapterLuid()
    {
        var monitor = Dp2();

        Assert.NotEmpty(monitor.AdapterId);
        Assert.DoesNotContain(monitor.AdapterId, monitor.HardwareKey);
    }

    [Fact]
    public void MonitorHardwareKey_FallsBackToConnectorAndTarget_WhenDevicePathMissing()
    {
        var monitor = Dp3() with { DevicePath = "" };

        Assert.Equal("DP-3#250118", monitor.HardwareKey);
        Assert.True(monitor.HasHardwareIdentity);
    }

    [Fact]
    public void MonitorWithoutAnyAnchor_HasNoHardwareIdentity()
    {
        var monitor = new MonitorInfo(@"\\.\DISPLAY1", "Generic", 1920, 1080, 60);

        Assert.Equal(string.Empty, monitor.HardwareKey);
        Assert.False(monitor.HasHardwareIdentity);
    }

    [Fact]
    public void MonitorBounds_SupportNegativeOrigin()
    {
        // A monitor left of the primary has a negative X. Rejecting that would break any
        // desktop where the second panel is arranged to the left.
        var monitor = Dp3() with { X = -2560, Y = 8, Width = 2560, Height = 1080 };

        Assert.Equal("-2560,8,2560,1080", monitor.BoundsKey);
        Assert.Equal(0, monitor.Right);
        Assert.Equal(1088, monitor.Bottom);
    }

    [Fact]
    public void MonitorDisplayLabel_DistinguishesIdenticallyNamedPanels()
    {
        var left = Dp2().DisplayLabel;
        var right = Dp3().DisplayLabel;

        Assert.NotEqual(left, right);
        Assert.Contains("DP-2", left);
        Assert.Contains("DP-3", right);
    }

    [Fact]
    public void TouchHardwareKey_PrefersUsbParentInstance()
    {
        var device = TouchA();

        Assert.Equal(TouchParentA.ToUpperInvariant(), device.HardwareKey);
        Assert.True(device.HasHardwareIdentity);
    }

    [Fact]
    public void TouchHardwareKey_FallsBackToInstanceId_WhenParentMissing()
    {
        var device = TouchA() with { ParentInstanceId = "" };

        Assert.Equal(@"HID\VID_14E1&PID_3508\8&25188E6B&0&0000", device.HardwareKey);
        Assert.True(device.HasHardwareIdentity);
    }

    [Fact]
    public void TouchWithoutAnyAnchor_HasNoHardwareIdentity()
    {
        var device = new HidDeviceInfo(@"\\?\HID#X", "PM1715", 0x14E1, 0x3508);

        Assert.Equal(string.Empty, device.HardwareKey);
        Assert.False(device.HasHardwareIdentity);
    }

    /// <summary>
    /// Identical digitizers on different USB ports must produce different anchors — otherwise
    /// there is nothing to bind an assignment to.
    /// </summary>
    [Fact]
    public void IdenticalDigitizers_OnDifferentPorts_HaveDifferentAnchors()
    {
        var a = TouchA();
        var b = TouchB();

        Assert.Equal(a.ProductName, b.ProductName);
        Assert.Equal(a.VendorId, b.VendorId);
        Assert.Equal(a.ProductId, b.ProductId);

        Assert.NotEqual(a.HardwareKey, b.HardwareKey);
        Assert.NotEqual(a.DisplayLabel, b.DisplayLabel);
    }

    [Theory]
    [InlineData(@"usb\vid_14e1&pid_3508\7&1b9afb93&0&6")]
    [InlineData(@"USB\VID_14E1&PID_3508\7&1B9AFB93&0&6")]
    [InlineData(@"  USB\VID_14E1&PID_3508\7&1b9afb93&0&6  ")]
    public void InstanceIdComparison_IsCaseAndWhitespaceInsensitive(string variant)
    {
        // SetupAPI and CfgMgr disagree about casing for the same device in practice, so the
        // comparison has to normalise or a stored mapping stops matching itself.
        Assert.Equal(
            HidDeviceInfo.NormalizeInstanceId(TouchParentA),
            HidDeviceInfo.NormalizeInstanceId(variant));
    }

    [Fact]
    public void DevicePathComparison_IsCaseInsensitive()
    {
        // Raw input reports lower-case paths; SetupAPI reports mixed case for the same device.
        var fromRawInput = @"\\?\hid#vid_14e1&pid_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        Assert.Equal(
            HidDeviceInfo.NormalizeDevicePath(TouchA().DevicePath),
            HidDeviceInfo.NormalizeDevicePath(fromRawInput));
    }
}
