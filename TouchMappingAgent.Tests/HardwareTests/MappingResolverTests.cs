using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.HardwareTests;

/// <summary>
/// Tests for resolving stored assignments back to present hardware.
///
/// This is where the phase-1 defect would have been caught: a stored mapping keyed on
/// "\\.\DISPLAY3" resolves to whichever panel happens to hold that ordinal after a reboot.
/// The tests below pin down that the hardware anchor decides, that a swapped GDI ordinal does
/// NOT move the assignment, and — most importantly — that anything ambiguous refuses to
/// resolve rather than guessing.
/// </summary>
public class MappingResolverTests
{
    private const string Dp2Path =
        @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Dp3Path =
        @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string TouchParentA = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";
    private const string TouchParentB = @"USB\VID_14E1&PID_3508\7&235bf858&0&6";

    private static MonitorInfo Dp2(string gdiName = @"\\.\DISPLAY2") => new(
        gdiName, "DM7000", 1920, 1080, 59,
        X: 0, Y: 0, ConnectorLabel: "DP-2", AdapterId: "00000000-0001F416", TargetId: 250116,
        DevicePath: Dp2Path, IsPrimary: true);

    private static MonitorInfo Dp3(string gdiName = @"\\.\DISPLAY3") => new(
        gdiName, "DM7000", 1920, 1080, 59,
        X: 1920, Y: 0, ConnectorLabel: "DP-3", AdapterId: "00000000-0001F416", TargetId: 250118,
        DevicePath: Dp3Path, IsPrimary: false);

    private static HidDeviceInfo Touch(string parent, string suffix) => new(
        DevicePath: $@"\\?\HID#VID_14E1&PID_3508#8&{suffix}&0&0000#{{4d1e55b2-f16f-11cf-88cb-001111000030}}",
        ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: $@"HID\VID_14E1&PID_3508\8&{suffix}&0&0000",
        ParentInstanceId: parent);

    private static TouchMapping MappingToDp3() => new(
        TouchHardwareKey: HidDeviceInfo.NormalizeInstanceId(TouchParentB),
        MonitorHardwareKey: MonitorInfo.NormalizeDevicePath(Dp3Path),
        MonitorConnectorLabel: "DP-3",
        MonitorTargetId: 250118,
        MonitorBoundsKey: "1920,0,1920,1080",
        TouchProductName: "PM1715",
        MonitorFriendlyName: "DM7000");

    [Fact]
    public void Resolve_MatchesOnDevicePath_WithExactQuality()
    {
        var digitizers = new[] { Touch(TouchParentA, "25188e6b"), Touch(TouchParentB, "19abaff7") };
        var monitors = new[] { Dp2(), Dp3() };

        var resolved = MappingResolver.Resolve(MappingToDp3(), digitizers, monitors);

        Assert.NotNull(resolved);
        Assert.Equal(MappingMatchQuality.Exact, resolved!.MatchQuality);
        Assert.Equal("DP-3", resolved.Monitor.ConnectorLabel);
        Assert.Equal(TouchParentB.ToUpperInvariant(), resolved.TouchDevice.HardwareKey);
    }

    /// <summary>
    /// THE regression test for the original defect. The GDI ordinals have swapped — exactly
    /// what a reboot with a different extender handshake order produces. The assignment must
    /// still land on the physically correct panel.
    /// </summary>
    [Fact]
    public void Resolve_IsUnaffectedWhenGdiOrdinalsSwap()
    {
        var digitizers = new[] { Touch(TouchParentA, "25188e6b"), Touch(TouchParentB, "19abaff7") };

        // DP-3 now enumerates as DISPLAY2 and DP-2 as DISPLAY3.
        var monitors = new[] { Dp2(@"\\.\DISPLAY3"), Dp3(@"\\.\DISPLAY2") };

        var resolved = MappingResolver.Resolve(MappingToDp3(), digitizers, monitors);

        Assert.NotNull(resolved);
        Assert.Equal("DP-3", resolved!.Monitor.ConnectorLabel);

        // And the tabcal command must carry the ordinal the panel has NOW, not the stored one.
        Assert.Equal(@"\\.\DISPLAY2", resolved.Monitor.DeviceId);
    }

    [Fact]
    public void Resolve_FallsBackToConnector_WhenDevicePathChanged()
    {
        var digitizers = new[] { Touch(TouchParentB, "19abaff7") };
        var monitors = new[] { Dp3() with { DevicePath = @"\\?\DISPLAY#OTHER#1&abc&0&UID250118#{guid}" } };

        var resolved = MappingResolver.Resolve(MappingToDp3(), digitizers, monitors);

        Assert.NotNull(resolved);
        Assert.Equal(MappingMatchQuality.Connector, resolved!.MatchQuality);
    }

    [Fact]
    public void Resolve_FallsBackToBounds_WhenConnectorAlsoChanged()
    {
        var digitizers = new[] { Touch(TouchParentB, "19abaff7") };
        var monitors = new[]
        {
            Dp3() with { DevicePath = "", ConnectorLabel = "DP-5", TargetId = 999 }
        };

        var resolved = MappingResolver.Resolve(MappingToDp3(), digitizers, monitors);

        Assert.NotNull(resolved);
        Assert.Equal(MappingMatchQuality.BoundsOnly, resolved!.MatchQuality);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenDigitizerAbsent()
    {
        var digitizers = new[] { Touch(TouchParentA, "25188e6b") }; // only the OTHER digitizer
        var monitors = new[] { Dp2(), Dp3() };

        Assert.Null(MappingResolver.Resolve(MappingToDp3(), digitizers, monitors));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNoMonitorMatches()
    {
        var digitizers = new[] { Touch(TouchParentB, "19abaff7") };
        var monitors = new[] { Dp2() };

        Assert.Null(MappingResolver.Resolve(MappingToDp3(), digitizers, monitors));
    }

    /// <summary>
    /// Real hardware produced a USB parent instance ending in a serial number rather than a
    /// port path (VID_0408 / PID_3008 with instance "0000"), which is identical for every unit
    /// of that model. Two such devices collide, and picking either would route touches to the
    /// wrong screen — the exact failure this model exists to prevent. Refusing is correct.
    /// </summary>
    [Fact]
    public void FindUniqueTouchDevice_RefusesWhenTwoDevicesShareAnAnchor()
    {
        const string collidingParent = @"USB\VID_0408&PID_3008\0000";
        var digitizers = new[]
        {
            Touch(collidingParent, "143319bc"),
            Touch(collidingParent, "99887766")
        };

        Assert.Null(MappingResolver.FindUniqueTouchDevice(collidingParent, digitizers));
    }

    [Fact]
    public void FindAmbiguousTouchKeys_ReportsCollisionsAndAnchorlessDevices()
    {
        const string collidingParent = @"USB\VID_0408&PID_3008\0000";
        var digitizers = new[]
        {
            Touch(collidingParent, "143319bc"),
            Touch(collidingParent, "99887766"),
            new HidDeviceInfo(@"\\?\HID#X", "NoAnchor", 0x1234, 0x5678)
        };

        var ambiguous = MappingResolver.FindAmbiguousTouchKeys(digitizers);

        Assert.Equal(2, ambiguous.Count); // one collision + one anchorless device
        Assert.Contains(HidDeviceInfo.NormalizeInstanceId(collidingParent), ambiguous);
        Assert.Contains(string.Empty, ambiguous);
    }

    /// <summary>
    /// Two panels at the same bounds cannot be told apart by bounds. Resolving must decline
    /// rather than take the first.
    /// </summary>
    [Fact]
    public void FindMonitor_RefusesWhenTwoMonitorsShareTheFallbackCriterion()
    {
        var mapping = MappingToDp3() with { MonitorHardwareKey = "", MonitorConnectorLabel = "" };
        var monitors = new[]
        {
            Dp3() with { DevicePath = "", ConnectorLabel = "" },
            Dp2() with { DevicePath = "", ConnectorLabel = "", X = 1920 }
        };

        var (monitor, quality) = MappingResolver.FindMonitor(mapping, monitors);

        Assert.Null(monitor);
        Assert.Equal(MappingMatchQuality.None, quality);
    }

    [Fact]
    public void ResolveAll_SkipsUnresolvableEntriesInsteadOfFailing()
    {
        var digitizers = new[] { Touch(TouchParentB, "19abaff7") };
        var monitors = new[] { Dp2(), Dp3() };

        var mappings = new[]
        {
            MappingToDp3(),
            MappingToDp3() with { TouchHardwareKey = @"USB\VID_DEAD&PID_BEEF\1&2&3" }
        };

        var resolved = MappingResolver.ResolveAll(mappings, digitizers, monitors);

        Assert.Single(resolved);
    }

    [Fact]
    public void BuildTabcalArguments_UsesCurrentRuntimeIdentifiers()
    {
        var arguments = MappingResolver.BuildTabcalArguments(
            @"\\.\DISPLAY2", @"\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{guid}");

        Assert.Contains(@"DisplayID=\\.\DISPLAY2", arguments);
        Assert.Contains("DeviceKind=touch", arguments);
        Assert.Contains(@"DevicePath=""\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{guid}""", arguments);
    }
}
