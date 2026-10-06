using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TouchMappingAgent.Shared.Contracts;
using TouchMappingAgent.Shared.Hardware;
using TouchMappingAgent.Shared.Models;
using TouchMappingAgent.WPF.Localization;
using TouchMappingAgent.WPF.Services;

namespace TouchMappingAgent.WPF.ViewModels;

/// <summary>The wizard's steps, in the order a commissioning runs through them.</summary>
public enum SetupStep
{
    /// <summary>Rights, service reachability and a hardware scan.</summary>
    PreFlight = 0,

    /// <summary>Choose and apply an EDID strategy.</summary>
    EdidStrategy = 1,

    /// <summary>Confirm Windows now sees distinct identities.</summary>
    PnpVerification = 2,

    /// <summary>Learn which digitizer belongs to which screen.</summary>
    TouchAssignment = 3,

    /// <summary>Run tabcal and let the operator confirm the result.</summary>
    TabcalTest = 4,

    /// <summary>Save and export the commissioning record.</summary>
    Summary = 5
}

/// <summary>Which route step 2 takes.</summary>
public enum EdidStrategy
{
    /// <summary>Derive unique serials from the EDIDs the hardware reports.</summary>
    Automatic,

    /// <summary>Install a template, for ports that report no usable EDID.</summary>
    Template,

    /// <summary>Nothing to do — the identities are already distinct.</summary>
    Skip
}

/// <summary>
/// Drives the guided commissioning wizard.
///
/// The state machine lives here and nowhere else: which step is reachable, what counts as
/// "done", and what the report says. The window binds to it and the
/// <see cref="ISetupInteractionService"/> supplies the parts that need a human, so the whole
/// sequence — including the paths where the operator cancels, no touch arrives, or tabcal
/// fails — is testable without a screen.
/// </summary>
public partial class SetupWizardViewModel : ObservableObject
{
    private readonly INamedPipeClient _pipeClient;
    private readonly IElevationService _elevation;
    private readonly ISetupInteractionService _interaction;
    private readonly ILogger<SetupWizardViewModel> _logger;
    private readonly ILocalizer _localizer;
    private readonly ITabcalRunner _tabcal;
    private readonly ITouchAlignmentService _alignment;

    private readonly List<SetupStepResult> _stepResults = new();
    private readonly List<SetupAssignmentRecord> _assignments = new();

    /// <summary>Total number of steps, for the "Schritt x von y" display.</summary>
    public const int TotalSteps = 6;

    /// <summary>Command-line flag that opens the wizard directly.</summary>
    public const string WizardSwitch = "--wizard";

    /// <summary>Initializes a new instance of <see cref="SetupWizardViewModel"/>.</summary>
    /// <param name="pipeClient">IPC client for talking to the service.</param>
    /// <param name="elevation">Elevation state and relaunch.</param>
    /// <param name="interaction">Everything requiring a human or a window.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="localizer">Resolves service messages, which arrive as keys.</param>
    /// <param name="tabcal">Runs tabcal.exe. Injected so no test can start the real tool.</param>
    /// <param name="alignment">Identifies and saves a touch-to-monitor mapping for one monitor.</param>
    public SetupWizardViewModel(
        INamedPipeClient pipeClient,
        IElevationService elevation,
        ISetupInteractionService interaction,
        ILogger<SetupWizardViewModel> logger,
        ILocalizer localizer,
        ITabcalRunner tabcal,
        ITouchAlignmentService alignment)
    {
        _pipeClient = pipeClient ?? throw new ArgumentNullException(nameof(pipeClient));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _tabcal = tabcal ?? throw new ArgumentNullException(nameof(tabcal));
        _alignment = alignment ?? throw new ArgumentNullException(nameof(alignment));

        StatusMessage = _localizer[LocalizationKeys.Wizard_Ready];
        PnpVerificationResult = _localizer[LocalizationKeys.Wizard_Step3_NotYetChecked];
        TabcalResult = _localizer[LocalizationKeys.Wizard_Step5_NotYetRun];
        CollisionStatus = _localizer[LocalizationKeys.Wizard_Step1_NotYetChecked];

        // StepTitle, StepCaption and the two file-path display strings are plain properties, not
        // {loc:Loc} bindings, because they combine a resource string with live state (the current
        // step, a path). Switching language while the wizard is open would otherwise leave them
        // frozen in whatever language the window was opened in.
        _localizer.CultureChanged += (_, _) =>
        {
            RaiseNavigationChanged();
            OnPropertyChanged(nameof(TemplateFileDisplay));
            OnPropertyChanged(nameof(ReportPathDisplay));
        };
    }

    // ------------------------------------------------------------------ navigation

    /// <summary>The step currently shown.</summary>
    [ObservableProperty]
    private SetupStep currentStep = SetupStep.PreFlight;

    /// <summary>True while a long-running action is in flight.</summary>
    [ObservableProperty]
    private bool isBusy;

    /// <summary>Status line at the bottom of the wizard.</summary>
    [ObservableProperty]
    private string statusMessage = string.Empty;

    /// <summary>"Step 1 of 6" caption.</summary>
    public string StepCaption => string.Format(
        _localizer[LocalizationKeys.Wizard_StepCaption], (int)CurrentStep + 1, TotalSteps);

    /// <summary>Heading of the current step.</summary>
    public string StepTitle => _localizer[CurrentStep switch
    {
        SetupStep.PreFlight => LocalizationKeys.Wizard_StepTitle_PreFlight,
        SetupStep.EdidStrategy => LocalizationKeys.Wizard_StepTitle_EdidStrategy,
        SetupStep.PnpVerification => LocalizationKeys.Wizard_StepTitle_PnpVerification,
        SetupStep.TouchAssignment => LocalizationKeys.Wizard_StepTitle_TouchAssignment,
        SetupStep.TabcalTest => LocalizationKeys.Wizard_StepTitle_TabcalTest,
        SetupStep.Summary => LocalizationKeys.Wizard_StepTitle_Summary,
        _ => string.Empty
    }];

    /// <summary>
    /// Whether the wizard may advance. Each step gates on its own success condition rather than
    /// letting the operator click through: a commissioning that skipped the PnP verification
    /// and then reports success would be worse than no report at all.
    /// </summary>
    public bool CanGoNext => !IsBusy && CurrentStep switch
    {
        SetupStep.PreFlight => PreFlightCompleted,
        SetupStep.EdidStrategy => EdidStepCompleted,
        SetupStep.PnpVerification => PnpVerified,
        SetupStep.TouchAssignment => TouchAssignmentCompleted,
        SetupStep.TabcalTest => TabcalStepCompleted,
        SetupStep.Summary => false,
        _ => false
    };

    /// <summary>Whether the operator can step back. The summary is terminal.</summary>
    public bool CanGoBack => !IsBusy && CurrentStep > SetupStep.PreFlight && CurrentStep != SetupStep.Summary;

    /// <summary>True on the last step.</summary>
    public bool IsFinalStep => CurrentStep == SetupStep.Summary;

    /// <summary>Command: advance one step.</summary>
    [RelayCommand]
    public void GoNext()
    {
        if (!CanGoNext)
            return;

        CurrentStep = (SetupStep)((int)CurrentStep + 1);
        StatusMessage = StepTitle;
    }

    /// <summary>Command: go back one step.</summary>
    [RelayCommand]
    public void GoBack()
    {
        if (!CanGoBack)
            return;

        CurrentStep = (SetupStep)((int)CurrentStep - 1);
        StatusMessage = StepTitle;
    }

    partial void OnCurrentStepChanged(SetupStep value) => RaiseNavigationChanged();

    partial void OnIsBusyChanged(bool value) => RaiseNavigationChanged();

    private void RaiseNavigationChanged()
    {
        OnPropertyChanged(nameof(StepCaption));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsFinalStep));
        GoNextCommand.NotifyCanExecuteChanged();
        GoBackCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------ step 1: pre-flight

    /// <summary>True when this process runs elevated.</summary>
    [ObservableProperty]
    private bool isElevated;

    /// <summary>True when the background service answered.</summary>
    [ObservableProperty]
    private bool serviceReachable;

    /// <summary>Monitors found.</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    /// <summary>Digitizers found.</summary>
    public ObservableCollection<HidDeviceInfo> Digitizers { get; } = new();

    /// <summary>Human-readable collision verdict.</summary>
    [ObservableProperty]
    private string collisionStatus = string.Empty;

    /// <summary>Number of monitors whose EDID identity collides.</summary>
    [ObservableProperty]
    private int collidingMonitorCount;

    /// <summary>Number of ports that report no EDID at all — the extender case.</summary>
    [ObservableProperty]
    private int portsWithoutEdidCount;

    /// <summary>True once the pre-flight scan produced a usable picture.</summary>
    [ObservableProperty]
    private bool preFlightCompleted;

    /// <summary>Command: run the pre-flight scan.</summary>
    [RelayCommand]
    public async Task RunPreFlightAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = _localizer[LocalizationKeys.Wizard_Step1_Checking];
            PreFlightCompleted = false;

            IsElevated = _elevation.IsElevated;

            var monitors = DisplayEnumerator.EnumerateMonitors();
            var digitizers = TouchDigitizerEnumerator.EnumerateDigitizers();

            Monitors.Clear();
            foreach (var m in monitors) Monitors.Add(m);

            Digitizers.Clear();
            foreach (var d in digitizers) Digitizers.Add(d);

            GetEdidStatusResponse? edid = null;
            try
            {
                edid = await _pipeClient.SendAsync<GetEdidStatusResponse>(new GetEdidStatusRequest());
                ServiceReachable = true;
            }
            catch (Exception ex)
            {
                ServiceReachable = false;
                _logger.LogWarning(ex, "Pre-flight: the service did not answer");
            }

            CollidingMonitorCount = edid?.FullIdentityCollisions ?? 0;

            // A port that drives a picture but has no EDID is the extender case, and it is the
            // reason step 2 offers a template route at all.
            var knownInstanceIds = (edid?.Monitors ?? Array.Empty<EdidMonitorStatus>())
                .Select(m => m.PnpInstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            PortsWithoutEdidCount = monitors.Count(m =>
            {
                var id = Evolved.EdidManager.Pnp.PnpDeviceInstanceId.FromDevicePath(m.DevicePath);
                return id != null && !knownInstanceIds.Contains(id);
            });

            CollisionStatus =
                !ServiceReachable ? _localizer[LocalizationKeys.Wizard_Step1_ServiceUnreachableStatus]
                : PortsWithoutEdidCount > 0 ? string.Format(
                    _localizer[LocalizationKeys.Wizard_Step1_ExtenderDetected], PortsWithoutEdidCount)
                : CollidingMonitorCount > 0 ? string.Format(
                    _localizer[LocalizationKeys.Wizard_Step1_CollisionDetected], CollidingMonitorCount)
                : _localizer[LocalizationKeys.Wizard_Step1_Clean];

            SelectedStrategy =
                PortsWithoutEdidCount > 0 ? EdidStrategy.Template
                : CollidingMonitorCount > 0 ? EdidStrategy.Automatic
                : EdidStrategy.Skip;

            // The scan must find hardware to work with, and the service must answer — without
            // either, every later step would fail on something the operator cannot see here.
            PreFlightCompleted = ServiceReachable && monitors.Count > 0;

            if (monitors.Count == 0)
                StatusMessage = _localizer[LocalizationKeys.Wizard_Step1_NoMonitors];
            else if (!ServiceReachable)
                StatusMessage = _localizer[LocalizationKeys.Wizard_Step1_ServiceNotResponding];
            else
                StatusMessage = string.Format(
                    _localizer[LocalizationKeys.Wizard_Step1_Summary],
                    monitors.Count, digitizers.Count, CollisionStatus);

            RecordStep(1, _localizer[LocalizationKeys.Wizard_StepTitle_PreFlight], PreFlightCompleted, StatusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-flight failed");
            StatusMessage = _localizer[LocalizationKeys.Wizard_Step1_Failed];
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ step 2: EDID strategy

    /// <summary>Route chosen for step 2.</summary>
    [ObservableProperty]
    private EdidStrategy selectedStrategy = EdidStrategy.Automatic;

    /// <summary>Path of a template file the operator picked, when any.</summary>
    [ObservableProperty]
    private string? templateFilePath;

    /// <summary>{0} = <see cref="TemplateFilePath"/>, formatted for display.</summary>
    public string TemplateFileDisplay => TemplateFilePath == null
        ? string.Empty
        : string.Format(_localizer[LocalizationKeys.Wizard_Step2_SelectedFile], TemplateFilePath);

    partial void OnTemplateFilePathChanged(string? value) => OnPropertyChanged(nameof(TemplateFileDisplay));

    /// <summary>True once step 2 is satisfied.</summary>
    [ObservableProperty]
    private bool edidStepCompleted;

    /// <summary>Result text of the last EDID action.</summary>
    [ObservableProperty]
    private string edidActionResult = string.Empty;

    partial void OnSelectedStrategyChanged(EdidStrategy value)
    {
        // Choosing "skip" is itself a decision and completes the step; the other routes have to
        // be carried out.
        EdidStepCompleted = value == EdidStrategy.Skip;
        RaiseNavigationChanged();
    }

    partial void OnEdidStepCompletedChanged(bool value) => RaiseNavigationChanged();

    partial void OnPreFlightCompletedChanged(bool value) => RaiseNavigationChanged();

    /// <summary>Command: pick an EDID template file.</summary>
    [RelayCommand]
    public async Task PickTemplateAsync()
    {
        var templateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Evolved", "EdidTemplates");

        var path = await _interaction.PickTemplateFileAsync(
            Directory.Exists(templateDirectory) ? templateDirectory : null);

        if (path == null)
            return;

        TemplateFilePath = path;
        SelectedStrategy = EdidStrategy.Template;
        StatusMessage = string.Format(
            _localizer[LocalizationKeys.Wizard_Step2_SelectedFile], Path.GetFileName(path));
    }

    /// <summary>
    /// Command: write the EDID overrides and re-enumerate the device nodes.
    ///
    /// No UAC prompt: the write happens in the background service, which runs as SYSTEM and
    /// therefore already owns the Enum key. Elevating the client would add a prompt without
    /// adding any capability.
    /// </summary>
    [RelayCommand]
    public async Task ApplyEdidStrategyAsync()
    {
        try
        {
            IsBusy = true;
            EdidStepCompleted = false;

            if (SelectedStrategy == EdidStrategy.Skip)
            {
                EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_Skipped];
                EdidStepCompleted = true;
                RecordStep(2, _localizer[LocalizationKeys.Wizard_Step2_RecordTitle], true, EdidActionResult);
                return;
            }

            if (SelectedStrategy == EdidStrategy.Automatic)
            {
                StatusMessage = _localizer[LocalizationKeys.Wizard_Step2_WritingSerials];
                var result = await _pipeClient.SendAsync<EdidOperationResponse>(
                    new ResolveEdidCollisionsRequest(false));

                EdidActionResult = _localizer.Format(result.Message);
                EdidStepCompleted = result.Success;
                RecordStep(2, _localizer[LocalizationKeys.Wizard_Step2_RecordTitleAutomatic],
                    result.Success, EdidActionResult);
                StatusMessage = EdidActionResult;
                return;
            }

            // Template route: apply to every port, with a distinct serial per port so the
            // panels stay tellable apart afterwards. Without the suffix all ports would end up
            // with the identical template and we would have recreated the collision.
            var targets = Monitors
                .Select(m => (Monitor: m, InstanceId: Evolved.EdidManager.Pnp.PnpDeviceInstanceId.FromDevicePath(m.DevicePath)))
                .Where(t => t.InstanceId != null)
                .OrderBy(t => t.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targets.Count == 0)
            {
                EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_NoPnpTarget];
                RecordStep(2, _localizer[LocalizationKeys.Wizard_Step2_RecordTitleTemplate],
                    false, EdidActionResult);
                StatusMessage = EdidActionResult;
                return;
            }

            byte[]? template = LoadTemplateBytes();
            if (template == null)
                return;

            int applied = 0;
            var failures = new List<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var suffix = ((char)('A' + i)).ToString();
                StatusMessage = string.Format(
                    _localizer[LocalizationKeys.Wizard_Step2_ApplyingToPort],
                    targets[i].Monitor.ConnectorLabel, suffix);

                var result = await _pipeClient.SendAsync<EdidOperationResponse>(
                    new ApplyEdidTemplateRequest(
                        targets[i].InstanceId!,
                        Convert.ToBase64String(template),
                        $"880-{suffix}"));

                if (result.Success)
                    applied++;
                else
                    failures.Add($"{targets[i].Monitor.ConnectorLabel}: {_localizer.Format(result.Message)}");
            }

            EdidStepCompleted = failures.Count == 0 && applied > 0;
            EdidActionResult = failures.Count == 0
                ? string.Format(_localizer[LocalizationKeys.Wizard_Step2_TemplateAppliedAll], applied)
                : string.Format(
                    _localizer[LocalizationKeys.Wizard_Step2_TemplateAppliedPartial],
                    applied, failures.Count, string.Join("; ", failures));

            RecordStep(2, _localizer[LocalizationKeys.Wizard_Step2_RecordTitleTemplate],
                EdidStepCompleted, EdidActionResult);
            StatusMessage = EdidActionResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying the EDID strategy failed");
            EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_OperationFailed];
            StatusMessage = EdidActionResult;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reads the chosen template, or the built-in DM7000 block when none was picked.
    /// Validation happens here as well as in the service — a broken template should be reported
    /// before it travels over the pipe.
    /// </summary>
    private byte[]? LoadTemplateBytes()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(TemplateFilePath))
                return Evolved.EdidManager.Templates.EdidTemplateStore.BuiltInDm7000.Edid.ToArray();

            var store = new Evolved.EdidManager.Templates.EdidTemplateStore(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    Evolved.EdidManager.Templates.EdidTemplateStore>.Instance,
                Path.GetDirectoryName(TemplateFilePath!) ?? Path.GetTempPath());

            if (Evolved.EdidManager.Templates.EdidTemplateStore.IsInfFile(TemplateFilePath!))
            {
                var entry = store.LoadFromInf(TemplateFilePath!).FirstOrDefault(e => e.IsChecksumValid);
                if (entry == null)
                {
                    EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_InfNoValidEdid];
                    return null;
                }

                return entry.Edid.ToArray();
            }

            var template = store.LoadFromFile(TemplateFilePath!);
            if (template == null)
            {
                EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_TemplateInvalid];
                return null;
            }

            return template.Edid.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the template {Path}", TemplateFilePath);
            EdidActionResult = _localizer[LocalizationKeys.Wizard_Step2_TemplateReadFailed];
            return null;
        }
    }

    // ------------------------------------------------------------------ step 3: PnP verification

    /// <summary>True once Windows reports distinct identities.</summary>
    [ObservableProperty]
    private bool pnpVerified;

    /// <summary>Verification result text.</summary>
    [ObservableProperty]
    private string pnpVerificationResult = string.Empty;

    /// <summary>Remaining colliding identities after the EDID step.</summary>
    [ObservableProperty]
    private int remainingCollisions;

    partial void OnPnpVerifiedChanged(bool value) => RaiseNavigationChanged();

    /// <summary>
    /// Command: re-measure and check the success metric — zero duplicate display identities.
    /// </summary>
    [RelayCommand]
    public async Task VerifyPnpAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = _localizer[LocalizationKeys.Wizard_Step3_Measuring];

            var edid = await _pipeClient.SendAsync<GetEdidStatusResponse>(new GetEdidStatusRequest());
            RemainingCollisions = edid.FullIdentityCollisions;

            // Also confirm the desktop side still resolves, so a successful EDID write that
            // knocked a monitor off the desktop cannot pass as a success.
            var monitors = DisplayEnumerator.EnumerateMonitors();
            Monitors.Clear();
            foreach (var m in monitors) Monitors.Add(m);

            var distinctAnchors = monitors
                .Select(m => m.HardwareKey)
                .Where(k => k.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            PnpVerified = RemainingCollisions == 0 && monitors.Count > 0 && distinctAnchors == monitors.Count;

            PnpVerificationResult = PnpVerified
                ? string.Format(_localizer[LocalizationKeys.Wizard_Step3_Passed], monitors.Count)
                : RemainingCollisions > 0
                    ? string.Format(
                        _localizer[LocalizationKeys.Wizard_Step3_StillColliding], RemainingCollisions)
                    : string.Format(
                        _localizer[LocalizationKeys.Wizard_Step3_PartialAnchors],
                        monitors.Count, distinctAnchors);

            RecordStep(3, _localizer[LocalizationKeys.Wizard_StepTitle_PnpVerification],
                PnpVerified, PnpVerificationResult);
            StatusMessage = PnpVerificationResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PnP verification failed");
            PnpVerificationResult = _localizer[LocalizationKeys.Wizard_Step3_Failed];
            StatusMessage = PnpVerificationResult;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ step 4: touch assignment

    /// <summary>Assignments learned in this run.</summary>
    public ObservableCollection<string> LearnedAssignments { get; } = new();

    /// <summary>True once every monitor carries an assignment.</summary>
    [ObservableProperty]
    private bool touchAssignmentCompleted;

    /// <summary>Index of the monitor currently being learned, for progress display.</summary>
    [ObservableProperty]
    private int currentAssignmentIndex;

    partial void OnTouchAssignmentCompletedChanged(bool value) => RaiseNavigationChanged();

    /// <summary>
    /// Command: walk every monitor, show the full-screen prompt and bind the digitizer that
    /// answers.
    /// </summary>
    [RelayCommand]
    public async Task RunTouchAssignmentAsync()
    {
        try
        {
            IsBusy = true;
            TouchAssignmentCompleted = false;
            LearnedAssignments.Clear();
            _assignments.Clear();

            var usedAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int learned = 0;

            for (int i = 0; i < Monitors.Count; i++)
            {
                CurrentAssignmentIndex = i;
                var monitor = Monitors[i];

                StatusMessage = string.Format(
                    _localizer[LocalizationKeys.Wizard_Step4_TouchPrompt], monitor.DisplayLabel);

                var identify = await _alignment.IdentifyAsync(monitor);

                if (identify.Outcome == TouchIdentifyOutcome.Cancelled)
                {
                    LearnedAssignments.Add(string.Format(
                        _localizer[LocalizationKeys.Wizard_Step4_Cancelled], monitor.ConnectorLabel));
                    continue;
                }

                if (identify.Outcome == TouchIdentifyOutcome.DeviceUnmatched)
                {
                    LearnedAssignments.Add(string.Format(
                        _localizer[LocalizationKeys.Wizard_Step4_Unmatched], monitor.ConnectorLabel));
                    continue;
                }

                if (identify.Outcome == TouchIdentifyOutcome.UnstableAnchor)
                {
                    LearnedAssignments.Add(string.Format(
                        _localizer[LocalizationKeys.Wizard_Step4_UnstableAnchor],
                        monitor.ConnectorLabel, identify.Device!.ProductName));
                    continue;
                }

                var device = identify.Device!;

                // The same digitizer answering twice means the operator touched the wrong
                // screen, or one panel's touch layer is dead. Silently overwriting the earlier
                // assignment would produce a report that claims both screens are configured.
                if (!usedAnchors.Add(device.HardwareKey))
                {
                    LearnedAssignments.Add(string.Format(
                        _localizer[LocalizationKeys.Wizard_Step4_DuplicateDigitizer], monitor.ConnectorLabel));
                    continue;
                }

                var saved = await _alignment.SaveAsync(monitor, device);
                if (!saved.Success)
                {
                    LearnedAssignments.Add(string.Format(
                        _localizer[LocalizationKeys.Wizard_Step4_SaveFailed], monitor.ConnectorLabel));
                    continue;
                }

                learned++;
                LearnedAssignments.Add(string.Format(
                    _localizer[LocalizationKeys.Wizard_Step4_LearnedEntry],
                    monitor.ConnectorLabel, device.DisplayLabel));
                _assignments.Add(new SetupAssignmentRecord(
                    device.HardwareKey, device.ProductName,
                    monitor.ConnectorLabel, monitor.HardwareKey,
                    TabcalSucceeded: false));
            }

            TouchAssignmentCompleted = learned == Monitors.Count && learned > 0;

            StatusMessage = TouchAssignmentCompleted
                ? string.Format(_localizer[LocalizationKeys.Wizard_Step4_AllLearned], learned)
                : string.Format(
                    _localizer[LocalizationKeys.Wizard_Step4_PartialLearned], learned, Monitors.Count);

            RecordStep(4, _localizer[LocalizationKeys.Wizard_StepTitle_TouchAssignment],
                TouchAssignmentCompleted, StatusMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Touch assignment failed");
            StatusMessage = _localizer[LocalizationKeys.Wizard_Step4_Failed];
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ step 5: tabcal test

    /// <summary>True once the calibration step reached a conclusion.</summary>
    [ObservableProperty]
    private bool tabcalStepCompleted;

    /// <summary>The operator's answer to the pointer-follows-finger question.</summary>
    [ObservableProperty]
    private bool? pointerFollowsFinger;

    /// <summary>Calibration result text.</summary>
    [ObservableProperty]
    private string tabcalResult = string.Empty;

    partial void OnTabcalStepCompletedChanged(bool value) => RaiseNavigationChanged();

    /// <summary>
    /// Command: have the service apply every learned assignment, then ask the operator whether
    /// the pointer actually follows their finger.
    ///
    /// No tabcal.exe and no elevation: the service (SYSTEM) writes Windows' touch routing table
    /// and restarts the digitizers when it answers the poll below. tabcal refused to run at all
    /// with two touch screens attached ("Only one touch input device can be calibrated at a
    /// time"). The poll carries this session's monitor list, which the service cannot see.
    /// </summary>
    [RelayCommand]
    public async Task RunTabcalTestAsync()
    {
        try
        {
            IsBusy = true;
            TabcalStepCompleted = false;

            await _pipeClient.SendAsync<GetPendingReapplyResponse>(
                new GetPendingReapplyRequest(Monitors.ToList()));

            // The digitizers restart when their routing changes; give them a moment to come
            // back before the operator starts touching.
            await Task.Delay(TimeSpan.FromSeconds(3));

            TabcalResult = _localizer[LocalizationKeys.Wizard_Step5_Applied];
            StatusMessage = TabcalResult;

            // The decisive check is whether the pointer actually lands under the finger. Only a
            // human can answer that, so the report records the operator's answer, not ours.
            PointerFollowsFinger = await _interaction.ConfirmAsync(
                _localizer[LocalizationKeys.Wizard_Step5_VisualCheckIntro] + "\n\n" +
                _localizer[LocalizationKeys.Wizard_Step5_VisualCheckQuestion],
                _localizer[LocalizationKeys.Wizard_Step5_VisualCheckTitle]);

            foreach (var assignment in _assignments.ToList())
                MarkAssignmentCalibrated(assignment.TouchHardwareKey, PointerFollowsFinger == true);

            TabcalStepCompleted = true;
            TabcalResult += PointerFollowsFinger == true
                ? _localizer[LocalizationKeys.Wizard_Step5_ConfirmedSuffix]
                : _localizer[LocalizationKeys.Wizard_Step5_NotConfirmedSuffix];

            RecordStep(5, _localizer[LocalizationKeys.Wizard_StepTitle_TabcalTest],
                PointerFollowsFinger == true, TabcalResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The calibration test failed");
            TabcalResult = _localizer[LocalizationKeys.Wizard_Step5_Failed];
            StatusMessage = TabcalResult;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>True when an elevated relaunch was started and this instance should close.</summary>
    [ObservableProperty]
    private bool elevationRequested;

    private void MarkAssignmentCalibrated(string touchHardwareKey, bool succeeded)
    {
        for (int i = 0; i < _assignments.Count; i++)
        {
            if (string.Equals(_assignments[i].TouchHardwareKey, touchHardwareKey, StringComparison.OrdinalIgnoreCase))
                _assignments[i] = _assignments[i] with { TabcalSucceeded = succeeded };
        }
    }

    // ------------------------------------------------------------------ step 6: summary

    /// <summary>Overall verdict shown on the last step.</summary>
    [ObservableProperty]
    private string finalVerdict = string.Empty;

    /// <summary>Path the report was written to, when it was.</summary>
    [ObservableProperty]
    private string? reportPath;

    /// <summary>{0} = <see cref="ReportPath"/>, formatted for display.</summary>
    public string ReportPathDisplay => ReportPath == null
        ? string.Empty
        : string.Format(_localizer[LocalizationKeys.Wizard_Step6_SavedPath], ReportPath);

    partial void OnReportPathChanged(string? value) => OnPropertyChanged(nameof(ReportPathDisplay));

    /// <summary>Recorded step outcomes, for display on the summary page.</summary>
    public ObservableCollection<SetupStepResult> StepResults { get; } = new();

    /// <summary>Builds the verdict when the summary step is entered.</summary>
    public void PrepareSummary()
    {
        bool allGood =
            PreFlightCompleted && EdidStepCompleted && PnpVerified &&
            TouchAssignmentCompleted && PointerFollowsFinger == true;

        FinalVerdict = allGood
            ? _localizer[LocalizationKeys.Wizard_Step6_VerdictSuccess]
            : _localizer[LocalizationKeys.Wizard_Step6_VerdictPartial];

        StepResults.Clear();
        foreach (var result in _stepResults)
            StepResults.Add(result);
    }

    /// <summary>Command: write the commissioning report as JSON.</summary>
    [RelayCommand]
    public async Task ExportReportAsync()
    {
        try
        {
            IsBusy = true;

            var suggested = $"setup-report_{Environment.MachineName}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            var target = await _interaction.PickReportTargetAsync(suggested);
            if (target == null)
                return;

            var report = BuildReport();
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            await File.WriteAllTextAsync(target, json, new System.Text.UTF8Encoding(false));

            ReportPath = target;
            StatusMessage = string.Format(_localizer[LocalizationKeys.Wizard_Step6_SavedToStatus], target);
            _logger.LogInformation("Commissioning report written to {Path}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not export the commissioning report");
            StatusMessage = _localizer[LocalizationKeys.Wizard_Step6_ExportFailed];
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Assembles the report from the state gathered during the run.</summary>
    internal SetupReport BuildReport() => new()
    {
        WasElevated = IsElevated,
        Steps = _stepResults.ToList(),
        Monitors = Monitors.Select(m => new SetupMonitorRecord(
            PnpInstanceId: Evolved.EdidManager.Pnp.PnpDeviceInstanceId.FromDevicePath(m.DevicePath) ?? string.Empty,
            Connector: m.ConnectorLabel,
            MonitorName: m.DisplayName,
            SerialNumber: 0,
            SerialText: null,
            HasOverride: false,
            Bounds: m.BoundsKey)).ToList(),
        Assignments = _assignments.ToList(),
        RemainingEdidCollisions = RemainingCollisions,
        PointerFollowsFingerConfirmed = PointerFollowsFinger,
        Verdict = FinalVerdict
    };

    private void RecordStep(int step, string title, bool succeeded, string detail)
    {
        _stepResults.RemoveAll(r => r.Step == step);
        _stepResults.Add(new SetupStepResult(step, title, succeeded, detail));
        _stepResults.Sort((a, b) => a.Step.CompareTo(b.Step));
    }
}
