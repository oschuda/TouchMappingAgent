using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Services;
using TouchMappingAgent.WPF.ViewModels;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// A scriptable <see cref="ITouchAlignmentService"/> for <see cref="MonitorMappingViewModel"/>
/// tests.
///
/// Deliberately NOT the real <see cref="TouchAlignmentService"/>: its identify phase calls
/// <c>TouchDigitizerEnumerator.EnumerateDigitizers()</c>, a real SetupAPI enumeration with no
/// seam to mock, so nothing exercising a specific <see cref="TouchIdentifyOutcome.Success"/>
/// result can go through it on a machine with no attached digitizers (see
/// <c>SetupWizardTests.TouchAssignment_CompletesOnlyWhenEveryMonitorIsLearned</c>'s comment for
/// the same limitation on the wizard side). This fake lets a test hand back whatever
/// identify/save result it needs regardless of what hardware the test machine has.
/// </summary>
internal sealed class FakeTouchAlignmentService : ITouchAlignmentService
{
    /// <summary>What <see cref="IdentifyAsync"/> returns. Defaults to a plausible success.</summary>
    public TouchIdentifyResult IdentifyResult { get; set; } = TouchIdentifyResult.Success(
        new HidDeviceInfo(
            DevicePath: @"\\?\HID#VID_14E1&PID_3508#8&25188e6b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}",
            ProductName: "PM1715",
            VendorId: 0x14E1,
            ProductId: 0x3508,
            InstanceId: @"HID\VID_14E1&PID_3508&COL01\8&25188e6b&0&0000",
            ParentInstanceId: "7&1b9afb93&0&6"));

    /// <summary>What <see cref="SaveAsync"/> returns. Defaults to a plain success.</summary>
    public TouchSaveResult SaveResult { get; set; } =
        TouchSaveResult.FromResponse(new MapTouchResponse(true, null));

    /// <summary>Every monitor <see cref="IdentifyAsync"/> was called with, in order.</summary>
    public List<MonitorInfo> IdentifyCalls { get; } = new();

    /// <summary>Every (monitor, device) pair <see cref="SaveAsync"/> was called with, in order.</summary>
    public List<(MonitorInfo Monitor, HidDeviceInfo Device)> SaveCalls { get; } = new();

    /// <inheritdoc/>
    public Task<TouchIdentifyResult> IdentifyAsync(MonitorInfo monitor)
    {
        IdentifyCalls.Add(monitor);
        return Task.FromResult(IdentifyResult);
    }

    /// <inheritdoc/>
    public Task<TouchSaveResult> SaveAsync(MonitorInfo monitor, HidDeviceInfo device)
    {
        SaveCalls.Add((monitor, device));
        return Task.FromResult(SaveResult);
    }
}
