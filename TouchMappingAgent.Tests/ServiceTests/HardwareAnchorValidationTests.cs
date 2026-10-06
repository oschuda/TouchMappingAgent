using TouchMappingAgent.Service.Validation;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Tests for validating hardware anchors.
///
/// The phase-1 validator required "\\.\DISPLAYn" as THE identifier, which meant a stable key
/// could not even be submitted for storage. It now validates two different kinds of value with
/// different rules: the volatile runtime handles tabcal.exe consumes, and the durable anchors
/// that get persisted.
/// </summary>
public class HardwareAnchorValidationTests
{
    private const string ValidGdiName = @"\\.\DISPLAY3";
    private const string ValidHidPath =
        @"\\?\HID#VID_14E1&PID_3508#8&19abaff7&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string ValidMonitorPath =
        @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string ValidTouchAnchor = @"USB\VID_14E1&PID_3508\7&1b9afb93&0&6";

    private static MapTouchRequest AnchoredRequest() => new(
        MonitorId: ValidGdiName,
        DeviceId: ValidHidPath,
        TouchHardwareKey: ValidTouchAnchor,
        MonitorHardwareKey: ValidMonitorPath,
        MonitorConnectorLabel: "DP-3",
        MonitorTargetId: 250118,
        MonitorBoundsKey: "1920,0,1920,1080",
        TouchProductName: "PM1715",
        MonitorFriendlyName: "DM7000");

    [Fact]
    public void ValidateMapRequest_AcceptsFullyAnchoredRequest()
    {
        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapRequest(AnchoredRequest())));
    }

    /// <summary>
    /// A one-shot calibration with no intent to persist stays valid — the anchors are optional.
    /// </summary>
    [Fact]
    public void ValidateMapRequest_AcceptsRequestWithoutAnchors()
    {
        var request = new MapTouchRequest(ValidGdiName, ValidHidPath);

        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapRequest(request)));
        Assert.False(MappingValidator.RequestCarriesAnchors(request));
    }

    /// <summary>
    /// Half an anchor pair is worse than none: it looks persistable but could never be
    /// resolved back to a device, so it is rejected rather than silently stored.
    /// </summary>
    [Fact]
    public void ValidateMapRequest_RejectsMonitorAnchorWithoutTouchAnchor()
    {
        var request = AnchoredRequest() with { TouchHardwareKey = "" };

        var ex = Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
        Assert.Contains("touch hardware anchor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateMapRequest_RejectsTouchAnchorWithoutAnyMonitorAnchor()
    {
        var request = AnchoredRequest() with
        {
            MonitorHardwareKey = "",
            MonitorConnectorLabel = "",
            MonitorBoundsKey = ""
        };

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    /// <summary>
    /// A PnP instance id and a device interface path are different shapes. Confusing them is
    /// how a mapping ends up keyed on something that never matches.
    /// </summary>
    [Fact]
    public void ValidateMapRequest_RejectsDeviceInterfacePathAsTouchAnchor()
    {
        var request = AnchoredRequest() with { TouchHardwareKey = ValidHidPath };

        var ex = Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
        Assert.Contains("PnP instance id", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("NoBackslashes")]
    [InlineData(@"USB\")]
    [InlineData(@"\Instance")]
    public void ValidateMapRequest_RejectsMalformedTouchAnchor(string anchor)
    {
        var request = AnchoredRequest() with { TouchHardwareKey = anchor };

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    [Theory]
    [InlineData("DP-2")]
    [InlineData("DP-0")]
    [InlineData("HDMI-1")]
    [InlineData("INTERNAL-0")]
    public void ValidateMapRequest_AcceptsRealConnectorLabels(string label)
    {
        var request = AnchoredRequest() with { MonitorConnectorLabel = label };

        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapRequest(request)));
    }

    [Theory]
    [InlineData("DP")]
    [InlineData("DP-")]
    [InlineData("-2")]
    [InlineData("DP-X")]
    public void ValidateMapRequest_RejectsMalformedConnectorLabels(string label)
    {
        var request = AnchoredRequest() with { MonitorConnectorLabel = label };

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    /// <summary>
    /// A monitor left of the primary has a negative origin. Rejecting that would break the
    /// most common two-panel arrangement there is.
    /// </summary>
    [Theory]
    [InlineData("0,0,1920,1080")]
    [InlineData("1920,0,1920,1080")]
    [InlineData("-2560,8,2560,1080")]
    public void ValidateMapRequest_AcceptsBoundsIncludingNegativeOrigin(string bounds)
    {
        var request = AnchoredRequest() with { MonitorBoundsKey = bounds };

        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapRequest(request)));
    }

    [Theory]
    [InlineData("0,0,1920")]
    [InlineData("0,0,1920,1080,60")]
    [InlineData("a,b,c,d")]
    [InlineData("0,0,0,1080")]
    [InlineData("0,0,1920,-1080")]
    public void ValidateMapRequest_RejectsMalformedBounds(string bounds)
    {
        var request = AnchoredRequest() with { MonitorBoundsKey = bounds };

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    /// <summary>
    /// The GDI name is still required — tabcal.exe consumes it — even though it is no longer
    /// the identity of anything.
    /// </summary>
    [Fact]
    public void ValidateMapRequest_StillRequiresGdiNameForTabcal()
    {
        var request = AnchoredRequest() with { MonitorId = "not-a-display" };

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    [Fact]
    public void ValidateMapping_AcceptsAMappingWithOnlyBoundsAsMonitorAnchor()
    {
        var mapping = new TouchMapping(
            TouchHardwareKey: ValidTouchAnchor,
            MonitorHardwareKey: "",
            MonitorConnectorLabel: "",
            MonitorTargetId: 0,
            MonitorBoundsKey: "1920,0,1920,1080");

        Assert.Null(Record.Exception(() => MappingValidator.ValidateMapping(mapping)));
    }

    [Fact]
    public void ValidateMapping_RejectsAMappingThatCouldNeverResolve()
    {
        var mapping = new TouchMapping(
            TouchHardwareKey: ValidTouchAnchor,
            MonitorHardwareKey: "",
            MonitorConnectorLabel: "",
            MonitorTargetId: 0,
            MonitorBoundsKey: "");

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapping(mapping));
    }

    /// <summary>
    /// The whitelist is not wired into any production path, but it must not be a trap either:
    /// activating it should not immediately lock out the installation's own digitizers.
    /// </summary>
    [Fact]
    public void VendorWhitelist_ContainsTheTargetInstallationsDigitizer()
    {
        Assert.Contains((ushort)0x14E1, MappingValidator.GetAllowedVendorIds());
    }

    [Fact]
    public void GetAllowedVendorIds_ReturnsASnapshotNotTheLiveSet()
    {
        var first = MappingValidator.GetAllowedVendorIds();
        MappingValidator.AddAllowedVendorId(0x7777);

        Assert.DoesNotContain((ushort)0x7777, first);
        Assert.Contains((ushort)0x7777, MappingValidator.GetAllowedVendorIds());
    }
}
