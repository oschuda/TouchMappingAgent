using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using TouchMappingAgent.Service.Services;
using TouchMappingAgent.Shared.Models;
using Xunit;

namespace TouchMappingAgent.Tests.ServiceTests;

/// <summary>
/// Tests for writing Windows' touch routing table.
///
/// Runs against a throw-away HKCU subtree and a recording fake in place of
/// "pnputil /restart-device": no test touches the machine's real routing or restarts a device.
/// The paths are the ones measured on the target installation.
/// </summary>
public class WindowsTouchMapApplierTests : IDisposable
{
    private const string TouchPathFromSetupApi =
        @"\\?\hid#vid_14e1&pid_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string ValueNameAsWindowsWritesIt =
        @"20-\\?\HID#VID_14E1&PID_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";
    private const string Dp2Path = @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Dp3Path = @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private readonly string _rootName = $@"Software\PadaLumaTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _root;
    private readonly List<string> _restarts = new();
    private bool _restartSucceeds = true;
    private readonly WindowsTouchMapApplier _applier;

    public WindowsTouchMapApplierTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
        _applier = new WindowsTouchMapApplier(
            NullLogger<WindowsTouchMapApplier>.Instance,
            _root,
            id => { _restarts.Add(id); return _restartSucceeds; });
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootName, throwOnMissingSubKey: false); } catch { }
        GC.SuppressFinalize(this);
    }

    private static HidDeviceInfo Touch() => new(
        DevicePath: TouchPathFromSetupApi, ProductName: "PM1715", VendorId: 0x14E1, ProductId: 0x3508,
        InstanceId: @"HID\VID_14E1&PID_3508\8&25188E6B&0&0000");

    private static MonitorInfo Monitor(string devicePath, string connector) => new(
        @"\\.\DISPLAY2", "DM7000", 1920, 1080, 59, ConnectorLabel: connector, DevicePath: devicePath);

    private string? Routing(string name)
    {
        using var key = _root.OpenSubKey(WindowsTouchMapApplier.DigimonKeyPath);
        return key?.GetValue(name) as string;
    }

    private int RoutingEntryCount()
    {
        using var key = _root.OpenSubKey(WindowsTouchMapApplier.DigimonKeyPath);
        return key?.GetValueNames().Length ?? 0;
    }

    [Fact]
    public void ValueName_UsesTheCasingWindowsWritesItself()
    {
        Assert.Equal(ValueNameAsWindowsWritesIt, WindowsTouchMapApplier.BuildValueName(TouchPathFromSetupApi));
    }

    [Fact]
    public void Apply_WritesTheRouting_AndRestartsTheDigitizer()
    {
        var outcome = _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"));

        Assert.Equal(TouchMapApplyOutcome.Updated, outcome);
        Assert.Equal(Dp3Path, Routing(ValueNameAsWindowsWritesIt));
        Assert.Equal(new[] { Touch().InstanceId }, _restarts);
    }

    [Fact]
    public void Apply_DoesNotRestart_WhenTheRoutingIsAlreadyCurrent()
    {
        // Every logon produces a new hardware generation; restarting the touch screens each time
        // although nothing changed would be a visible flicker for no reason.
        _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"));
        _restarts.Clear();

        var outcome = _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"));

        Assert.Equal(TouchMapApplyOutcome.AlreadyCurrent, outcome);
        Assert.Empty(_restarts);
    }

    [Fact]
    public void Apply_RestartsAnyway_WhenForced()
    {
        _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"));
        _restarts.Clear();

        var outcome = _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"), forceRestart: true);

        Assert.Equal(TouchMapApplyOutcome.Updated, outcome);
        Assert.Single(_restarts);
    }

    /// <summary>
    /// The field case: after an extender power-cycle the same HID path belongs to the other
    /// screen, so the existing entry must be overwritten, not duplicated.
    /// </summary>
    [Fact]
    public void Apply_OverwritesAWrongEntry_EvenWhenItsNameDiffersInCase()
    {
        using (var key = _root.CreateSubKey(WindowsTouchMapApplier.DigimonKeyPath, writable: true)!)
            key.SetValue(ValueNameAsWindowsWritesIt, Dp2Path);

        var outcome = _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3"));

        Assert.Equal(TouchMapApplyOutcome.Updated, outcome);
        Assert.Equal(Dp3Path, Routing(ValueNameAsWindowsWritesIt));
        Assert.Equal(1, RoutingEntryCount());
    }

    [Fact]
    public void Apply_RetriesTheRestart_WhenItFailedBefore()
    {
        _restartSucceeds = false;
        Assert.Equal(TouchMapApplyOutcome.Failed, _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3")));

        // The table now already reads as current; without remembering the failed restart the
        // routing would silently wait for the next logon.
        _restartSucceeds = true;
        _restarts.Clear();

        Assert.Equal(TouchMapApplyOutcome.Updated, _applier.Apply(Touch(), Monitor(Dp3Path, "DP-3")));
        Assert.Single(_restarts);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\.\DISPLAY2")]
    [InlineData(@"\\?\DISPLAY#..#x#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    [InlineData(@"\\?\USB#VID_1234#x#{guid}")]
    public void Apply_RefusesMalformedMonitorPaths_WithoutWritingOrRestarting(string monitorPath)
    {
        var outcome = _applier.Apply(Touch(), Monitor(monitorPath, "DP-3"));

        Assert.Equal(TouchMapApplyOutcome.Failed, outcome);
        Assert.Equal(0, RoutingEntryCount());
        Assert.Empty(_restarts);
    }

    [Fact]
    public void Apply_RefusesAMissingInstanceId_WithoutWritingOrRestarting()
    {
        var outcome = _applier.Apply(Touch() with { InstanceId = "" }, Monitor(Dp3Path, "DP-3"));

        Assert.Equal(TouchMapApplyOutcome.Failed, outcome);
        Assert.Equal(0, RoutingEntryCount());
        Assert.Empty(_restarts);
    }
}
