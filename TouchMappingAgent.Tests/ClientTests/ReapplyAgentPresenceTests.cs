using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Services;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Covers the guard in front of tabcal.exe.
///
/// WHY THIS MATTERS: tabcal.exe was measured to exit with code 0 even when it did nothing —
/// started with a valid command line naming a DevicePath that is not attached, it parses the
/// arguments, calibrates nothing and returns 0. Its exit code therefore cannot be used to tell
/// "calibrated" from "silently skipped". Because <see cref="ReapplyAgent"/> reports success back
/// to the service, which then clears the queue entry and writes it to the audit trail, an
/// unguarded run would record a successful re-application while touch input stayed on the wrong
/// screen — the one failure the agent exists to prevent, and the one hardest to notice.
/// </summary>
public class ReapplyAgentPresenceTests
{
    private const string AssignedPath =
        @"\\?\hid#vid_0eef&pid_c000&col01#7&1b9afb93&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

    private static ReapplyAgent CreateAgent(params string[] attachedDevicePaths)
    {
        var digitizers = attachedDevicePaths
            .Select(path => new HidDeviceInfo(
                DevicePath: path,
                ProductName: "PM1715",
                VendorId: 0x0EEF,
                ProductId: 0xC000,
                InstanceId: @"HID\VID_0EEF&PID_C000&COL01\7&1b9afb93&0&0000",
                ParentInstanceId: "7&1b9afb93&0&6"))
            .ToList();

        return new ReapplyAgent(
            new UnusedPipeClient(),
            NullLogger<ReapplyAgent>.Instance,
            new TestLocalizer(),
            new FakeTabcalRunner(),
            () => digitizers);
    }

    [Fact]
    public void Present_when_the_assigned_digitizer_is_attached()
    {
        var (present, error) = CreateAgent(AssignedPath).IsTouchDevicePresent(AssignedPath);

        Assert.True(present);
        Assert.Null(error);
    }

    [Fact]
    public void Device_paths_compare_case_insensitively()
    {
        // Windows hands back HID paths with inconsistent casing between SetupAPI and Raw Input,
        // so a case-sensitive comparison would report a present digitizer as missing.
        var (present, _) = CreateAgent(AssignedPath.ToUpperInvariant())
            .IsTouchDevicePresent(AssignedPath.ToLowerInvariant());

        Assert.True(present);
    }

    [Fact]
    public void Not_present_when_the_digitizer_is_unplugged()
    {
        var (present, error) = CreateAgent().IsTouchDevicePresent(AssignedPath);

        Assert.False(present);
        Assert.Equal(MessageKeys.Reapply_DigitizerAbsent, error!.Key);
    }

    [Fact]
    public void Not_present_when_a_different_digitizer_is_attached()
    {
        // The BIGHYPERV case: two structurally identical PM1715 digitizers that differ only in the
        // USB port they hang off. Re-applying an assignment against the WRONG one would calibrate
        // the wrong screen, so a near-miss must not count as present.
        const string otherPath =
            @"\\?\hid#vid_0eef&pid_c000&col01#7&235bf858&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}";

        var (present, _) = CreateAgent(otherPath).IsTouchDevicePresent(AssignedPath);

        Assert.False(present);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Not_present_when_the_assignment_carries_no_device_path(string path)
    {
        var (present, error) = CreateAgent(AssignedPath).IsTouchDevicePresent(path);

        Assert.False(present);
        Assert.Equal(MessageKeys.Reapply_NoDevicePath, error!.Key);
    }

    [Fact]
    public void Enumeration_failure_counts_as_not_present()
    {
        // Fail closed. A false success is silently wrong and is written to the audit trail; a
        // false failure is merely retried on the next poll.
        var agent = new ReapplyAgent(
            new UnusedPipeClient(),
            NullLogger<ReapplyAgent>.Instance,
            new TestLocalizer(),
            new FakeTabcalRunner(),
            () => throw new InvalidOperationException("SetupDiGetClassDevs failed"));

        var (present, error) = agent.IsTouchDevicePresent(AssignedPath);

        Assert.False(present);
        Assert.Equal(MessageKeys.Reapply_EnumerationFailed, error!.Key);
        Assert.Contains("SetupDiGetClassDevs failed", error.Arguments[0]);
    }

    /// <summary>
    /// The presence check runs before any IPC, so these tests never reach the pipe.
    /// </summary>
    private sealed class UnusedPipeClient : INamedPipeClient
    {
        public Task<TResponse> SendAsync<TResponse>(object request) where TResponse : class =>
            throw new InvalidOperationException(
                "The presence check must not talk to the service.");

        public Task<TResponse> SendAsync<TResponse>(object request, TimeSpan responseTimeout) where TResponse : class =>
            throw new InvalidOperationException(
                "The presence check must not talk to the service.");

        public Task<bool> IsConnectedAsync() =>
            throw new InvalidOperationException(
                "The presence check must not talk to the service.");
    }
}
