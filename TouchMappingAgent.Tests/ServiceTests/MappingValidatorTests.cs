using TouchMappingAgent.Service.Validation;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Regression tests for MappingValidator. The original ValidateIdentifier() rejected any
/// identifier containing a backslash or forward slash as a naive path-traversal guard —
/// but real Windows monitor device names ("\\.\DISPLAY1") and HID device interface paths
/// ("\\?\HID#VID_...#...") both legitimately contain backslashes, so mapping could never
/// succeed with real hardware identifiers. These tests lock in the fix.
/// </summary>
public class MappingValidatorTests
{
    [Fact]
    public void ValidateMapRequest_WithRealWindowsDeviceIdentifiers_DoesNotThrow()
    {
        var request = new MapTouchRequest(
            MonitorId: @"\\.\DISPLAY1",
            DeviceId: @"\\?\HID#VID_046D&PID_C52B#7&2a1b3c4d&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}");

        var exception = Record.Exception(() => MappingValidator.ValidateMapRequest(request));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY2")]
    [InlineData(@"\\.\DISPLAY10")]
    public void ValidateMapRequest_WithValidMonitorIdFormats_DoesNotThrow(string monitorId)
    {
        var request = new MapTouchRequest(monitorId, @"\\?\HID#VID_0001&PID_0002#instance#{guid}");

        var exception = Record.Exception(() => MappingValidator.ValidateMapRequest(request));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("DISPLAY1")]
    [InlineData(@"\\.\DISPLAY")]
    [InlineData(@"\\.\DISPLAYabc")]
    [InlineData(@"\\.\DISPLAY1\..\..\Windows\System32")]
    public void ValidateMapRequest_WithMalformedMonitorId_ThrowsArgumentException(string monitorId)
    {
        var request = new MapTouchRequest(monitorId, @"\\?\HID#VID_0001&PID_0002#instance#{guid}");

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_0001&PID_0002#..\..\..\Windows\System32#{guid}")]
    [InlineData("C:\\Windows\\System32\\evil.dll")]
    [InlineData("")]
    public void ValidateMapRequest_WithMalformedOrTraversalDeviceId_ThrowsArgumentException(string deviceId)
    {
        var request = new MapTouchRequest(@"\\.\DISPLAY1", deviceId);

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    [Fact]
    public void ValidateMapRequest_WithNullBytesInDeviceId_ThrowsArgumentException()
    {
        var request = new MapTouchRequest(@"\\.\DISPLAY1", "\\\\?\\HID#VID_0001\0malicious");

        Assert.Throws<ArgumentException>(() => MappingValidator.ValidateMapRequest(request));
    }

    [Fact]
    public void ValidateMonitor_WithRealDisplayDeviceName_DoesNotThrow()
    {
        var monitor = new MonitorInfo(@"\\.\DISPLAY1", "Generic PnP Monitor", 1920, 1080, 60);

        var exception = Record.Exception(() => MappingValidator.ValidateMonitor(monitor));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateHidDevice_WithWhitelistedVendor_DoesNotThrow()
    {
        var device = new HidDeviceInfo(@"\\?\HID#VID_0461&PID_0001#instance#{guid}", "Test Touch Panel", 0x0461, 0x0001);

        var exception = Record.Exception(() => MappingValidator.ValidateHidDevice(device));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateHidDevice_WithNonWhitelistedVendor_ThrowsUnauthorizedAccessException()
    {
        var device = new HidDeviceInfo(@"\\?\HID#VID_FFFF&PID_0001#instance#{guid}", "Unknown Device", 0xFFFF, 0x0001);

        Assert.Throws<UnauthorizedAccessException>(() => MappingValidator.ValidateHidDevice(device));
    }
}
