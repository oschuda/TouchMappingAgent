using Evolved.EdidManager;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Services;
using TouchMappingAgent.WPF.ViewModels;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Tests for the commissioning wizard's state machine.
///
/// The property under test throughout is that the wizard cannot advance past a step that did
/// not actually succeed. A wizard that lets a technician click through and then prints
/// "Inbetriebnahme abgeschlossen" is worse than no wizard: it produces a signed-off report for
/// a machine whose touch input still lands on the wrong screen.
/// </summary>
public class SetupWizardTests
{
    private sealed class FakeElevation : IElevationService
    {
        public bool IsElevated { get; set; }
        public List<string> RelaunchArguments { get; } = new();
        public bool RelaunchResult { get; set; } = true;

        public bool RelaunchElevated(string arguments)
        {
            RelaunchArguments.Add(arguments);
            return RelaunchResult;
        }
    }


    private static MonitorInfo Monitor(string connector, string devicePath, int x = 0) => new(
        DeviceId: $@"\\.\DISPLAY{connector[^1]}",
        DisplayName: "DM7000",
        Width: 1920, Height: 1080, RefreshRate: 59,
        X: x, Y: 0,
        ConnectorLabel: connector,
        AdapterId: "00000000-0001F416",
        TargetId: 250116,
        DevicePath: devicePath);

    private static (SetupWizardViewModel Vm, Mock<INamedPipeClient> Pipe, FakeElevation Elevation, FakeSetupInteractionService Interaction)
        CreateWizard(int collisions = 0)
    {
        var pipe = new Mock<INamedPipeClient>();
        var elevation = new FakeElevation();
        var interaction = new FakeSetupInteractionService();

        pipe.Setup(p => p.SendAsync<GetEdidStatusResponse>(It.IsAny<object>()))
            .ReturnsAsync(new GetEdidStatusResponse(Array.Empty<EdidMonitorStatus>(), collisions, 0));

        // The real TouchAlignmentService, wired to the same fakes: step 4's tests now exercise
        // the shared identify+save logic exactly as MonitorMappingViewModel's tests do, rather
        // than a second hand-rolled fake that could quietly drift from what the wizard actually
        // calls.
        var alignment = new TouchAlignmentService(
            interaction, pipe.Object, NullLogger<TouchAlignmentService>.Instance);

        var vm = new SetupWizardViewModel(
            pipe.Object, elevation, interaction, NullLogger<SetupWizardViewModel>.Instance,
            new TestLocalizer(), new FakeTabcalRunner(), alignment);

        return (vm, pipe, elevation, interaction);
    }

    // ---------------------------------------------------------------- navigation

    [Fact]
    public void Wizard_StartsOnThePreFlightStep()
    {
        var (vm, _, _, _) = CreateWizard();

        Assert.Equal(SetupStep.PreFlight, vm.CurrentStep);
        Assert.Equal(LocalizationKeys.Wizard_StepCaption, vm.StepCaption);
        Assert.False(vm.CanGoBack);
    }

    /// <summary>
    /// The core guard: before the pre-flight has run there is nothing to base a commissioning
    /// on, so the wizard must not advance.
    /// </summary>
    [Fact]
    public void Wizard_CannotAdvanceBeforeThePreFlightSucceeds()
    {
        var (vm, _, _, _) = CreateWizard();

        Assert.False(vm.CanGoNext);

        vm.GoNext();

        Assert.Equal(SetupStep.PreFlight, vm.CurrentStep);
    }

    [Fact]
    public async Task PreFlight_BlocksWhenTheServiceIsUnreachable()
    {
        var (vm, pipe, _, _) = CreateWizard();
        pipe.Setup(p => p.SendAsync<GetEdidStatusResponse>(It.IsAny<object>()))
            .ThrowsAsync(new InvalidOperationException("offline"));

        await vm.RunPreFlightAsync();

        Assert.False(vm.ServiceReachable);
        Assert.False(vm.PreFlightCompleted);
        Assert.False(vm.CanGoNext);
        Assert.Equal(LocalizationKeys.Wizard_Step1_ServiceNotResponding, vm.StatusMessage);
    }

    [Fact]
    public void Wizard_CannotStepBackFromTheFirstStep()
    {
        var (vm, _, _, _) = CreateWizard();

        vm.GoBack();

        Assert.Equal(SetupStep.PreFlight, vm.CurrentStep);
    }

    [Fact]
    public void Wizard_IsBusyBlocksNavigationInBothDirections()
    {
        var (vm, _, _, _) = CreateWizard();
        vm.PreFlightCompleted = true;
        vm.CurrentStep = SetupStep.EdidStrategy;
        vm.EdidStepCompleted = true;

        Assert.True(vm.CanGoNext);
        Assert.True(vm.CanGoBack);

        vm.IsBusy = true;

        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);
    }

    [Fact]
    public void Wizard_TitlesAreDefinedForEveryStep()
    {
        var (vm, _, _, _) = CreateWizard();

        foreach (SetupStep step in Enum.GetValues<SetupStep>())
        {
            vm.CurrentStep = step;
            Assert.False(string.IsNullOrWhiteSpace(vm.StepTitle), $"{step} has no title.");
        }
    }

    [Fact]
    public void Wizard_SummaryIsTerminal()
    {
        var (vm, _, _, _) = CreateWizard();
        vm.CurrentStep = SetupStep.Summary;

        Assert.True(vm.IsFinalStep);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.CanGoBack);
    }

    // ---------------------------------------------------------------- step 2

    /// <summary>
    /// A clean system still has to make an explicit decision, but "skip" is a valid one and
    /// must not force the technician to write overrides they do not need.
    /// </summary>
    [Fact]
    public void EdidStep_SkipCompletesTheStepWithoutWriting()
    {
        var (vm, pipe, _, _) = CreateWizard();

        vm.SelectedStrategy = EdidStrategy.Skip;

        Assert.True(vm.EdidStepCompleted);
        pipe.Verify(p => p.SendAsync<EdidOperationResponse>(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public void EdidStep_ChoosingAnActiveStrategyResetsCompletion()
    {
        var (vm, _, _, _) = CreateWizard();

        vm.SelectedStrategy = EdidStrategy.Skip;
        Assert.True(vm.EdidStepCompleted);

        vm.SelectedStrategy = EdidStrategy.Automatic;
        Assert.False(vm.EdidStepCompleted);
    }

    [Fact]
    public async Task EdidStep_AutomaticFailureLeavesTheStepIncomplete()
    {
        var (vm, pipe, _, _) = CreateWizard();
        pipe.Setup(p => p.SendAsync<EdidOperationResponse>(It.IsAny<object>()))
            .ReturnsAsync(new EdidOperationResponse(false, 0, 2, LocalizableText.Of(EdidMessageKeys.ElevationRequired)));

        vm.SelectedStrategy = EdidStrategy.Automatic;
        await vm.ApplyEdidStrategyAsync();

        Assert.False(vm.EdidStepCompleted);
        Assert.Contains(EdidMessageKeys.ElevationRequired, vm.EdidActionResult);
    }

    [Fact]
    public async Task EdidStep_AutomaticSuccessCompletesTheStep()
    {
        var (vm, pipe, _, _) = CreateWizard();
        pipe.Setup(p => p.SendAsync<EdidOperationResponse>(It.IsAny<object>()))
            .ReturnsAsync(new EdidOperationResponse(true, 2, 0, LocalizableText.Of(EdidMessageKeys.CollisionsResolved, 2)));

        vm.SelectedStrategy = EdidStrategy.Automatic;
        await vm.ApplyEdidStrategyAsync();

        Assert.True(vm.EdidStepCompleted);
    }

    /// <summary>
    /// The template route must stamp a DIFFERENT serial per port. Applying the same template
    /// unchanged everywhere would recreate exactly the collision the wizard is there to remove.
    /// </summary>
    [Fact]
    public async Task EdidStep_TemplateRouteStampsADistinctSerialPerPort()
    {
        var (vm, pipe, _, _) = CreateWizard();

        var requests = new List<ApplyEdidTemplateRequest>();
        pipe.Setup(p => p.SendAsync<EdidOperationResponse>(It.IsAny<object>()))
            .Callback<object>(r => { if (r is ApplyEdidTemplateRequest a) requests.Add(a); })
            .ReturnsAsync(new EdidOperationResponse(true, 1, 0, LocalizableText.Of(EdidMessageKeys.CollisionsResolved, 1)));

        vm.Monitors.Add(Monitor("DP-2", @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{guid}"));
        vm.Monitors.Add(Monitor("DP-3", @"\\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118#{guid}", 1920));

        vm.SelectedStrategy = EdidStrategy.Template;
        await vm.ApplyEdidStrategyAsync();

        Assert.Equal(2, requests.Count);
        Assert.Equal(2, requests.Select(r => r.CustomSerial).Distinct().Count());
        Assert.Equal(2, requests.Select(r => r.PnpId).Distinct().Count());
        Assert.True(vm.EdidStepCompleted);
    }

    [Fact]
    public async Task EdidStep_TemplateRouteReportsPartialFailure()
    {
        var (vm, pipe, _, _) = CreateWizard();

        int call = 0;
        pipe.Setup(p => p.SendAsync<EdidOperationResponse>(It.IsAny<object>()))
            .ReturnsAsync(() => ++call == 1
                ? new EdidOperationResponse(true, 1, 0, LocalizableText.Of(EdidMessageKeys.CollisionsResolved, 1))
                : new EdidOperationResponse(false, 0, 1, LocalizableText.Of(EdidMessageKeys.TemplateBadChecksum, "30", "E0")));

        vm.Monitors.Add(Monitor("DP-2", @"\\?\DISPLAY#CHR8910#5&a&0&UID250116#{guid}"));
        vm.Monitors.Add(Monitor("DP-3", @"\\?\DISPLAY#CHR8910#5&a&0&UID250118#{guid}", 1920));

        vm.SelectedStrategy = EdidStrategy.Template;
        await vm.ApplyEdidStrategyAsync();

        Assert.False(vm.EdidStepCompleted);
        Assert.Equal(LocalizationKeys.Wizard_Step2_TemplateAppliedPartial, vm.EdidActionResult);
    }

    // ---------------------------------------------------------------- step 3

    /// <summary>The stated success metric: zero duplicate display identities.</summary>
    [Fact]
    public async Task PnpVerification_FailsWhileCollisionsRemain()
    {
        var (vm, _, _, _) = CreateWizard(collisions: 2);

        await vm.VerifyPnpAsync();

        Assert.False(vm.PnpVerified);
        Assert.Equal(2, vm.RemainingCollisions);
        Assert.False(vm.CanGoNext);
    }

    // ---------------------------------------------------------------- step 4

    [Fact]
    public async Task TouchAssignment_CompletesOnlyWhenEveryMonitorIsLearned()
    {
        var (vm, pipe, _, interaction) = CreateWizard();
        pipe.Setup(p => p.SendAsync<MapTouchResponse>(It.IsAny<object>()))
            .ReturnsAsync(new MapTouchResponse(true, null));

        vm.Monitors.Add(Monitor("DP-2", @"\\?\DISPLAY#CHR8910#5&a&0&UID250116#{guid}"));
        vm.Monitors.Add(Monitor("DP-3", @"\\?\DISPLAY#CHR8910#5&a&0&UID250118#{guid}", 1920));

        // No digitizers are attached to the test machine, so identification cannot succeed.
        interaction.TouchByConnector["DP-2"] = null;
        interaction.TouchByConnector["DP-3"] = null;

        await vm.RunTouchAssignmentAsync();

        Assert.False(vm.TouchAssignmentCompleted);
        Assert.Equal(2, vm.LearnedAssignments.Count);
        Assert.All(vm.LearnedAssignments, entry => Assert.Equal(LocalizationKeys.Wizard_Step4_Cancelled, entry));
    }

    [Fact]
    public async Task TouchAssignment_ReportsWhenNoTouchArrives()
    {
        var (vm, _, _, interaction) = CreateWizard();
        vm.Monitors.Add(Monitor("DP-2", @"\\?\DISPLAY#CHR8910#5&a&0&UID250116#{guid}"));
        interaction.TouchByConnector["DP-2"] = null;

        await vm.RunTouchAssignmentAsync();

        Assert.False(vm.TouchAssignmentCompleted);
        Assert.Equal(LocalizationKeys.Wizard_Step4_PartialLearned, vm.StatusMessage);
    }

    // ---------------------------------------------------------------- step 5

    /// <summary>
    /// The service (SYSTEM) writes the routing now; the wizard must neither ask for elevation
    /// nor run tabcal.exe, which refuses to work with two touch screens attached.
    /// </summary>
    [Fact]
    public async Task ApplyStep_NeedsNoElevation_AndLetsTheServiceApply()
    {
        var (vm, pipe, elevation, interaction) = CreateWizard();
        elevation.IsElevated = false;
        interaction.ConfirmResult = true;   // "yes, the pointer follows"

        pipe.Setup(p => p.SendAsync<GetPendingReapplyResponse>(It.IsAny<object>()))
            .ReturnsAsync(new GetPendingReapplyResponse(Array.Empty<PendingReapply>()));

        await vm.RunTabcalTestAsync();

        pipe.Verify(p => p.SendAsync<GetPendingReapplyResponse>(It.IsAny<GetPendingReapplyRequest>()), Times.Once);
        Assert.Empty(elevation.RelaunchArguments);
        Assert.False(vm.ElevationRequested);
        Assert.True(vm.TabcalStepCompleted);
        Assert.True(vm.PointerFollowsFinger);
        Assert.StartsWith(LocalizationKeys.Wizard_Step5_Applied, vm.TabcalResult);
        Assert.Contains(LocalizationKeys.Wizard_Step5_ConfirmedSuffix, vm.TabcalResult);
    }

    /// <summary>
    /// Nothing reports whether the pointer actually lands under the finger except the operator.
    /// The step must therefore hinge on the operator's answer.
    /// </summary>
    [Fact]
    public async Task TabcalStep_RecordsANegativeVisualCheckAsAFailure()
    {
        var (vm, pipe, elevation, interaction) = CreateWizard();
        elevation.IsElevated = true;
        interaction.ConfirmResult = false;   // "no, the pointer does not follow"

        pipe.Setup(p => p.SendAsync<GetPendingReapplyResponse>(It.IsAny<object>()))
            .ReturnsAsync(new GetPendingReapplyResponse(new[]
            {
                new PendingReapply("USB\\A", "MON", "DP-2", @"\\?\HID#A", @"\\.\DISPLAY2",
                    "DM7000", @"C:\Windows\System32\tabcal.exe", "LinCal ...",
                    MappingMatchQuality.Exact, LocalizableText.Of(MessageKeys.Reapply_ReasonExact))
            }));
        pipe.Setup(p => p.SendAsync<ReportReapplyResultResponse>(It.IsAny<object>()))
            .ReturnsAsync(new ReportReapplyResultResponse(true, null));

        await vm.RunTabcalTestAsync();

        Assert.True(vm.TabcalStepCompleted);           // the step concluded
        Assert.False(vm.PointerFollowsFinger);          // but the outcome is negative
        Assert.Contains(LocalizationKeys.Wizard_Step5_NotConfirmedSuffix, vm.TabcalResult);
    }

    [Fact]
    public async Task ApplyStep_ReportsAFailure_WhenTheServiceCannotBeReached()
    {
        var (vm, pipe, _, _) = CreateWizard();

        pipe.Setup(p => p.SendAsync<GetPendingReapplyResponse>(It.IsAny<object>()))
            .ThrowsAsync(new TimeoutException("service not reachable"));

        await vm.RunTabcalTestAsync();

        Assert.False(vm.TabcalStepCompleted);
        Assert.Equal(LocalizationKeys.Wizard_Step5_Failed, vm.TabcalResult);
    }

    // ---------------------------------------------------------------- step 6

    /// <summary>
    /// The verdict must reflect what actually happened. A report that says "abgeschlossen"
    /// after a failed visual check would sign off a broken machine.
    /// </summary>
    [Fact]
    public void Summary_VerdictIsQualifiedWhenAnyStepFailed()
    {
        var (vm, _, _, _) = CreateWizard();
        vm.PreFlightCompleted = true;
        vm.EdidStepCompleted = true;
        vm.PnpVerified = true;
        vm.TouchAssignmentCompleted = true;
        vm.PointerFollowsFinger = false;

        vm.PrepareSummary();

        Assert.Equal(LocalizationKeys.Wizard_Step6_VerdictPartial, vm.FinalVerdict);
    }

    [Fact]
    public void Summary_VerdictIsCleanOnlyWhenEveryStepSucceeded()
    {
        var (vm, _, _, _) = CreateWizard();
        vm.PreFlightCompleted = true;
        vm.EdidStepCompleted = true;
        vm.PnpVerified = true;
        vm.TouchAssignmentCompleted = true;
        vm.PointerFollowsFinger = true;

        vm.PrepareSummary();

        Assert.Equal(LocalizationKeys.Wizard_Step6_VerdictSuccess, vm.FinalVerdict);
    }

    [Fact]
    public async Task Report_CarriesTheStepOutcomesAndTheVisualCheck()
    {
        var (vm, _, _, _) = CreateWizard();

        await vm.RunPreFlightAsync();
        vm.PointerFollowsFinger = true;
        vm.RemainingCollisions = 0;
        vm.PrepareSummary();

        var report = vm.BuildReport();

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(Environment.MachineName, report.MachineName);
        Assert.NotEmpty(report.Steps);
        Assert.True(report.PointerFollowsFingerConfirmed);
        Assert.Equal(vm.FinalVerdict, report.Verdict);
    }

    [Fact]
    public async Task Report_IsNotWrittenWhenTheOperatorCancelsTheSaveDialog()
    {
        var (vm, _, _, interaction) = CreateWizard();
        interaction.ReportTargetToReturn = null;

        await vm.ExportReportAsync();

        Assert.Null(vm.ReportPath);
    }

    [Fact]
    public async Task Report_IsWrittenAsReadableJson()
    {
        var (vm, _, _, interaction) = CreateWizard();
        var target = Path.Combine(Path.GetTempPath(), $"setup-report_{Guid.NewGuid():N}.json");
        interaction.ReportTargetToReturn = target;

        try
        {
            await vm.RunPreFlightAsync();
            vm.PrepareSummary();
            await vm.ExportReportAsync();

            Assert.Equal(target, vm.ReportPath);
            Assert.True(File.Exists(target));

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(target));
            Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.True(document.RootElement.TryGetProperty("Steps", out _));
            Assert.True(document.RootElement.TryGetProperty("Verdict", out _));
        }
        finally
        {
            if (File.Exists(target))
                File.Delete(target);
        }
    }
}
