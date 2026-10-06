namespace TouchMappingAgent.WPF.Localization;

/// <summary>
/// Resource keys for text that originates in the CLIENT.
///
/// Kept separate from <see cref="Shared.Localization.MessageKeys"/> on purpose. Those are a
/// contract between two processes — the service sends them, the client resolves them, and neither
/// side may change one without the other. These are purely local: window titles, buttons, tray
/// entries, the operator handbook. Mixing them would suggest the service can send text it has no
/// business knowing about.
///
/// Every key here has a German entry in Strings.resx and an English one in Strings.en.resx.
/// LocalizationTests enumerates this class by reflection alongside MessageKeys and
/// EdidMessageKeys, so a key added here without both translations — or with mismatched
/// placeholder counts — fails the build instead of showing "[Key]" on a commissioning screen.
/// </summary>
internal static class LocalizationKeys
{
    // ---- Shared value converters ----------------------------------------------------------------
    //
    // Used from CommonConverters.cs, which is instantiated once as a static XAML resource rather
    // than through DI, so these read TouchMappingAgent.WPF.Localization.LocalizationSource.Instance
    // directly instead of taking an ILocalizer — same pattern as EdidPortRow and IdentifyWindow.

    /// <summary>Boolean rendered as "yes".</summary>
    public const string Common_Yes = "Common_Yes";

    /// <summary>Boolean rendered as "no".</summary>
    public const string Common_No = "Common_No";

    /// <summary>Elevation state: running elevated.</summary>
    public const string Common_Administrator = "Common_Administrator";

    /// <summary>Elevation state: running as a standard user.</summary>
    public const string Common_StandardUser = "Common_StandardUser";

    /// <summary>Commissioning step outcome: passed.</summary>
    public const string Common_StepOk = "Common_StepOk";

    /// <summary>Commissioning step outcome: needs attention.</summary>
    public const string Common_StepCheck = "Common_StepCheck";

    // ---- Language picker --------------------------------------------------------------------

    /// <summary>Tray submenu heading for the language picker.</summary>
    public const string LanguageMenuHeader = "Language_MenuHeader";

    // ---- Main window --------------------------------------------------------------------------

    /// <summary>Subtitle under the product name.</summary>
    public const string Main_Subtitle = "Main_Subtitle";

    /// <summary>Bold lead-in of the touch-calibration notice banner.</summary>
    public const string Main_NoticeLabel = "Main_NoticeLabel";

    /// <summary>Body of the touch-calibration notice banner.</summary>
    public const string Main_NoticeText = "Main_NoticeText";

    /// <summary>Label above the monitor picker.</summary>
    public const string Main_MonitorLabel = "Main_MonitorLabel";

    /// <summary>Button: re-reads the monitor list.</summary>
    public const string Main_LoadMonitors = "Main_LoadMonitors";

    /// <summary>Button: starts the touch-identification flow for the selected monitor.</summary>
    public const string Main_LearnScreen = "Main_LearnScreen";

    /// <summary>Button: creates a mapping backup.</summary>
    public const string Main_CreateBackup = "Main_CreateBackup";

    /// <summary>Button: runs the advanced repair sequence.</summary>
    public const string Main_AdvancedRepair = "Main_AdvancedRepair";

    /// <summary>Button: asks the service for its current hardware status.</summary>
    public const string Main_CheckServiceStatus = "Main_CheckServiceStatus";

    /// <summary>Button: opens the in-app diagnostics log window.</summary>
    public const string Main_ShowDiagnosticsLog = "Main_ShowDiagnosticsLog";

    /// <summary>Label in front of the status line.</summary>
    public const string Main_StatusLabel = "Main_StatusLabel";

    /// <summary>No monitor was selected before starting the learn flow.</summary>
    public const string Main_SelectMonitorFirst = "Main_SelectMonitorFirst";

    /// <summary>{0} = monitor name, {1} = connector. Instruction shown during the learn flow.</summary>
    public const string Main_TouchTargetScreen = "Main_TouchTargetScreen";

    /// <summary>Learn flow is waiting for a touch.</summary>
    public const string Main_LearningInProgress = "Main_LearningInProgress";

    /// <summary>Learn flow was cancelled without a touch being detected.</summary>
    public const string Main_LearnAborted = "Main_LearnAborted";

    /// <summary>The touch that arrived does not match any enumerated digitizer.</summary>
    public const string Main_TouchDeviceUnmatched = "Main_TouchDeviceUnmatched";

    /// <summary>The touched digitizer has no usable hardware anchor, so it cannot be remembered.</summary>
    public const string Main_UnstableHardwareAnchor = "Main_UnstableHardwareAnchor";

    /// <summary>The learn flow threw before it could finish.</summary>
    public const string Main_LearnFailed = "Main_LearnFailed";

    /// <summary>The service rejected a mapping it was asked to store.</summary>
    public const string Main_MappingRejected = "Main_MappingRejected";

    /// <summary>Mapping stored; no local tabcal execution was required.</summary>
    public const string Main_MappingSaved = "Main_MappingSaved";

    /// <summary>{0} = monitor name, {1} = connector. Mapping stored and tabcal ran successfully.</summary>
    public const string Main_MappingSavedAndActivated = "Main_MappingSavedAndActivated";

    /// <summary>tabcal ran but did not confirm the calibration.</summary>
    public const string Main_CalibrationFailed = "Main_CalibrationFailed";

    /// <summary>Idle status shown before any command has run.</summary>
    public const string Main_Ready = "Main_Ready";

    /// <summary>Monitor list is being (re-)read.</summary>
    public const string Main_LoadingMonitors = "Main_LoadingMonitors";

    /// <summary>{0} = monitor count. Monitor list finished loading.</summary>
    public const string Main_MonitorsLoaded = "Main_MonitorsLoaded";

    /// <summary>Reading the monitor list threw.</summary>
    public const string Main_LoadMonitorsFailed = "Main_LoadMonitorsFailed";

    /// <summary>A mapping backup is being created.</summary>
    public const string Main_CreatingBackup = "Main_CreatingBackup";

    /// <summary>The backup command succeeded.</summary>
    public const string Main_BackupCreated = "Main_BackupCreated";

    /// <summary>The service reported that the backup failed.</summary>
    public const string Main_BackupFailed = "Main_BackupFailed";

    /// <summary>The backup command threw before it could finish.</summary>
    public const string Main_BackupError = "Main_BackupError";

    /// <summary>The advanced repair sequence is running.</summary>
    public const string Main_RunningAdvancedRepair = "Main_RunningAdvancedRepair";

    /// <summary>The advanced repair sequence succeeded.</summary>
    public const string Main_AdvancedRepairSucceeded = "Main_AdvancedRepairSucceeded";

    /// <summary>The advanced repair sequence reported partial success.</summary>
    public const string Main_AdvancedRepairIssues = "Main_AdvancedRepairIssues";

    /// <summary>The advanced repair sequence could not even be requested.</summary>
    public const string Main_AdvancedRepairIpcFailed = "Main_AdvancedRepairIpcFailed";

    /// <summary>The service answered the status probe.</summary>
    public const string Main_ServiceActive = "Main_ServiceActive";

    /// <summary>The service did not answer the status probe.</summary>
    public const string Main_ServiceNotResponding = "Main_ServiceNotResponding";

    /// <summary>Shown after an unhandled UI exception was mirrored into the in-app log.</summary>
    public const string Main_UnhandledExceptionReported = "Main_UnhandledExceptionReported";

    // ---- Full-screen touch identification prompt (IdentifyWindow) ---------------------------

    /// <summary>Big instruction on the full-screen prompt.</summary>
    public const string Identify_TouchNow = "Identify_TouchNow";

    /// <summary>Waiting-for-touch status line (Esc cancels).</summary>
    public const string Identify_WaitingForTouch = "Identify_WaitingForTouch";

    /// <summary>Raw Input registration failed; automatic identification is unavailable.</summary>
    public const string Identify_RawInputUnavailableDetail = "Identify_RawInputUnavailableDetail";

    /// <summary>Raw Input is unavailable for a simpler reason (no detail to add).</summary>
    public const string Identify_RawInputUnavailable = "Identify_RawInputUnavailable";

    /// <summary>A touch was recognised; the mapping is being saved.</summary>
    public const string Identify_TouchDetected = "Identify_TouchDetected";

    // ---- Setup wizard: chrome ------------------------------------------------------------------

    /// <summary>Window title (OS taskbar / title bar). Does not track the current step.</summary>
    public const string Wizard_WindowTitle = "Wizard_WindowTitle";

    /// <summary>Label in front of the elevation indicator.</summary>
    public const string Wizard_RightsLabel = "Wizard_RightsLabel";

    /// <summary>Back navigation button.</summary>
    public const string Wizard_Back = "Wizard_Back";

    /// <summary>Forward navigation button.</summary>
    public const string Wizard_Next = "Wizard_Next";

    /// <summary>Initial status line before any step has run.</summary>
    public const string Wizard_Ready = "Wizard_Ready";

    /// <summary>{0} = current step number, {1} = total steps. "Step X of Y" caption.</summary>
    public const string Wizard_StepCaption = "Wizard_StepCaption";

    // ---- Setup wizard: step titles (also used as the recorded step's title, where unchanged) --

    /// <summary>Step 1 title.</summary>
    public const string Wizard_StepTitle_PreFlight = "Wizard_StepTitle_PreFlight";

    /// <summary>Step 2 title.</summary>
    public const string Wizard_StepTitle_EdidStrategy = "Wizard_StepTitle_EdidStrategy";

    /// <summary>Step 3 title.</summary>
    public const string Wizard_StepTitle_PnpVerification = "Wizard_StepTitle_PnpVerification";

    /// <summary>Step 4 title.</summary>
    public const string Wizard_StepTitle_TouchAssignment = "Wizard_StepTitle_TouchAssignment";

    /// <summary>Step 5 title.</summary>
    public const string Wizard_StepTitle_TabcalTest = "Wizard_StepTitle_TabcalTest";

    /// <summary>Step 6 title.</summary>
    public const string Wizard_StepTitle_Summary = "Wizard_StepTitle_Summary";

    // ---- Setup wizard: step 1 (pre-flight) -----------------------------------------------------

    /// <summary>Introductory paragraph.</summary>
    public const string Wizard_Step1_Intro = "Wizard_Step1_Intro";

    /// <summary>"Result" sub-heading.</summary>
    public const string Wizard_Step1_ResultHeading = "Wizard_Step1_ResultHeading";

    /// <summary>Label: administrative rights.</summary>
    public const string Wizard_Step1_AdminRights = "Wizard_Step1_AdminRights";

    /// <summary>Label: background service reachable.</summary>
    public const string Wizard_Step1_ServiceReachable = "Wizard_Step1_ServiceReachable";

    /// <summary>Label: monitors detected.</summary>
    public const string Wizard_Step1_MonitorsDetected = "Wizard_Step1_MonitorsDetected";

    /// <summary>Label: digitizers detected.</summary>
    public const string Wizard_Step1_DigitizersDetected = "Wizard_Step1_DigitizersDetected";

    /// <summary>Sub-heading above the detected-monitors list.</summary>
    public const string Wizard_Step1_MonitorsHeading = "Wizard_Step1_MonitorsHeading";

    /// <summary>Sub-heading above the detected-digitizers list.</summary>
    public const string Wizard_Step1_DigitizersHeading = "Wizard_Step1_DigitizersHeading";

    /// <summary>Button: runs the pre-flight scan.</summary>
    public const string Wizard_Step1_Run = "Wizard_Step1_Run";

    /// <summary>Status line while the pre-flight scan is running.</summary>
    public const string Wizard_Step1_Checking = "Wizard_Step1_Checking";

    /// <summary>Initial, not-yet-checked collision status.</summary>
    public const string Wizard_Step1_NotYetChecked = "Wizard_Step1_NotYetChecked";

    /// <summary>No monitors were enumerated at all.</summary>
    public const string Wizard_Step1_NoMonitors = "Wizard_Step1_NoMonitors";

    /// <summary>The background service did not answer.</summary>
    public const string Wizard_Step1_ServiceNotResponding = "Wizard_Step1_ServiceNotResponding";

    /// <summary>The pre-flight scan threw before it could finish.</summary>
    public const string Wizard_Step1_Failed = "Wizard_Step1_Failed";

    /// <summary>Collision status: the service could not be reached.</summary>
    public const string Wizard_Step1_ServiceUnreachableStatus = "Wizard_Step1_ServiceUnreachableStatus";

    /// <summary>{0} = port count. Collision status: extender ports with no EDID.</summary>
    public const string Wizard_Step1_ExtenderDetected = "Wizard_Step1_ExtenderDetected";

    /// <summary>{0} = monitor count. Collision status: identity collision detected.</summary>
    public const string Wizard_Step1_CollisionDetected = "Wizard_Step1_CollisionDetected";

    /// <summary>Collision status: nothing wrong.</summary>
    public const string Wizard_Step1_Clean = "Wizard_Step1_Clean";

    /// <summary>{0} = monitor count, {1} = digitizer count, {2} = collision status. Status line.</summary>
    public const string Wizard_Step1_Summary = "Wizard_Step1_Summary";

    // ---- Setup wizard: step 2 (EDID strategy) --------------------------------------------------

    /// <summary>Introductory paragraph.</summary>
    public const string Wizard_Step2_Intro = "Wizard_Step2_Intro";

    /// <summary>Option (a) title.</summary>
    public const string Wizard_Step2_OptionATitle = "Wizard_Step2_OptionATitle";

    /// <summary>Option (a) description.</summary>
    public const string Wizard_Step2_OptionADesc = "Wizard_Step2_OptionADesc";

    /// <summary>Option (b) title.</summary>
    public const string Wizard_Step2_OptionBTitle = "Wizard_Step2_OptionBTitle";

    /// <summary>Option (b) description.</summary>
    public const string Wizard_Step2_OptionBDesc = "Wizard_Step2_OptionBDesc";

    /// <summary>Button: opens the template file picker.</summary>
    public const string Wizard_Step2_PickTemplate = "Wizard_Step2_PickTemplate";

    /// <summary>{0} = file path. Shown once a template file has been picked.</summary>
    public const string Wizard_Step2_SelectedFile = "Wizard_Step2_SelectedFile";

    /// <summary>Button: writes the EDID overrides and resets the PnP nodes.</summary>
    public const string Wizard_Step2_Apply = "Wizard_Step2_Apply";

    /// <summary>The "Skip" route was chosen — identities were already unique.</summary>
    public const string Wizard_Step2_Skipped = "Wizard_Step2_Skipped";

    /// <summary>Status line while unique serials are being written.</summary>
    public const string Wizard_Step2_WritingSerials = "Wizard_Step2_WritingSerials";

    /// <summary>{0} = connector label, {1} = per-port suffix letter. Progress applying a template.</summary>
    public const string Wizard_Step2_ApplyingToPort = "Wizard_Step2_ApplyingToPort";

    /// <summary>Record title for the plain "strategy chosen" step, before a route is picked.</summary>
    public const string Wizard_Step2_RecordTitle = "Wizard_Step2_RecordTitle";

    /// <summary>Record title for the automatic-mutation route.</summary>
    public const string Wizard_Step2_RecordTitleAutomatic = "Wizard_Step2_RecordTitleAutomatic";

    /// <summary>Record title for the template route.</summary>
    public const string Wizard_Step2_RecordTitleTemplate = "Wizard_Step2_RecordTitleTemplate";

    /// <summary>No port with a resolvable PnP id was found for the template route.</summary>
    public const string Wizard_Step2_NoPnpTarget = "Wizard_Step2_NoPnpTarget";

    /// <summary>The EDID operation itself threw.</summary>
    public const string Wizard_Step2_OperationFailed = "Wizard_Step2_OperationFailed";

    /// <summary>{0} = applied count. All template applications on this route succeeded.</summary>
    public const string Wizard_Step2_TemplateAppliedAll = "Wizard_Step2_TemplateAppliedAll";

    /// <summary>{0} = applied count, {1} = failure detail list. Some template applications failed.</summary>
    public const string Wizard_Step2_TemplateAppliedPartial = "Wizard_Step2_TemplateAppliedPartial";

    /// <summary>An .inf file contained no EDID with a valid checksum.</summary>
    public const string Wizard_Step2_InfNoValidEdid = "Wizard_Step2_InfNoValidEdid";

    /// <summary>The picked template is structurally invalid (header, size or checksum).</summary>
    public const string Wizard_Step2_TemplateInvalid = "Wizard_Step2_TemplateInvalid";

    /// <summary>The picked template file could not be read at all.</summary>
    public const string Wizard_Step2_TemplateReadFailed = "Wizard_Step2_TemplateReadFailed";

    // ---- Setup wizard: step 3 (PnP verification) -----------------------------------------------

    /// <summary>Introductory paragraph.</summary>
    public const string Wizard_Step3_Intro = "Wizard_Step3_Intro";

    /// <summary>Label in front of the remaining-collisions count.</summary>
    public const string Wizard_Step3_RemainingCollisions = "Wizard_Step3_RemainingCollisions";

    /// <summary>"(required: 0)" qualifier next to the count.</summary>
    public const string Wizard_Step3_RequiredZero = "Wizard_Step3_RequiredZero";

    /// <summary>Button: re-measures and re-verifies.</summary>
    public const string Wizard_Step3_Verify = "Wizard_Step3_Verify";

    /// <summary>Not-yet-checked placeholder result.</summary>
    public const string Wizard_Step3_NotYetChecked = "Wizard_Step3_NotYetChecked";

    /// <summary>Status line while the re-measurement is running.</summary>
    public const string Wizard_Step3_Measuring = "Wizard_Step3_Measuring";

    /// <summary>{0} = monitor count. Verification passed with zero collisions.</summary>
    public const string Wizard_Step3_Passed = "Wizard_Step3_Passed";

    /// <summary>{0} = remaining collision count. Collisions remain.</summary>
    public const string Wizard_Step3_StillColliding = "Wizard_Step3_StillColliding";

    /// <summary>{0} = monitor count, {1} = distinct anchor count. Anchors are still not unique.</summary>
    public const string Wizard_Step3_PartialAnchors = "Wizard_Step3_PartialAnchors";

    /// <summary>Verification threw before it could finish.</summary>
    public const string Wizard_Step3_Failed = "Wizard_Step3_Failed";

    // ---- Setup wizard: step 4 (touch assignment) -----------------------------------------------

    /// <summary>Introductory paragraph.</summary>
    public const string Wizard_Step4_Intro = "Wizard_Step4_Intro";

    /// <summary>Sub-heading above the list of learned assignments.</summary>
    public const string Wizard_Step4_LearnedHeading = "Wizard_Step4_LearnedHeading";

    /// <summary>Button: runs the touch-learn sequence across all monitors.</summary>
    public const string Wizard_Step4_Run = "Wizard_Step4_Run";

    /// <summary>The touch-learn sequence threw before it could finish.</summary>
    public const string Wizard_Step4_Failed = "Wizard_Step4_Failed";

    /// <summary>{0} = monitor label. Prompt shown for the monitor currently being learned.</summary>
    public const string Wizard_Step4_TouchPrompt = "Wizard_Step4_TouchPrompt";

    /// <summary>{0} = connector label. No touch arrived for this monitor.</summary>
    public const string Wizard_Step4_Cancelled = "Wizard_Step4_Cancelled";

    /// <summary>{0} = connector label. The touch did not match an enumerated digitizer.</summary>
    public const string Wizard_Step4_Unmatched = "Wizard_Step4_Unmatched";

    /// <summary>{0} = connector label, {1} = product name. Digitizer has no stable anchor.</summary>
    public const string Wizard_Step4_UnstableAnchor = "Wizard_Step4_UnstableAnchor";

    /// <summary>{0} = connector label. The same digitizer answered a second time.</summary>
    public const string Wizard_Step4_DuplicateDigitizer = "Wizard_Step4_DuplicateDigitizer";

    /// <summary>{0} = connector label. The service rejected the assignment.</summary>
    public const string Wizard_Step4_SaveFailed = "Wizard_Step4_SaveFailed";

    /// <summary>{0} = connector label, {1} = digitizer label. One successfully learned entry.</summary>
    public const string Wizard_Step4_LearnedEntry = "Wizard_Step4_LearnedEntry";

    /// <summary>{0} = learned count. Every monitor was learned.</summary>
    public const string Wizard_Step4_AllLearned = "Wizard_Step4_AllLearned";

    /// <summary>{0} = learned count, {1} = total count. Some monitors still need learning.</summary>
    public const string Wizard_Step4_PartialLearned = "Wizard_Step4_PartialLearned";

    // ---- Setup wizard: step 5 (tabcal test) ----------------------------------------------------

    /// <summary>Paragraph before the bold elevation notice.</summary>
    public const string Wizard_Step5_IntroBeforeBold = "Wizard_Step5_IntroBeforeBold";

    /// <summary>Bold phrase: administrative rights are required for this step.</summary>
    public const string Wizard_Step5_BoldPhrase = "Wizard_Step5_BoldPhrase";

    /// <summary>Paragraph after the bold elevation notice.</summary>
    public const string Wizard_Step5_IntroAfterBold = "Wizard_Step5_IntroAfterBold";

    /// <summary>Button: runs tabcal and starts the visual check.</summary>
    public const string Wizard_Step5_Run = "Wizard_Step5_Run";

    /// <summary>Not-yet-run placeholder result.</summary>
    public const string Wizard_Step5_NotYetRun = "Wizard_Step5_NotYetRun";

    /// <summary>Elevation is required but this process does not have it.</summary>
    public const string Wizard_Step5_ElevationRequired = "Wizard_Step5_ElevationRequired";

    /// <summary>Appended to the elevation notice: asks whether to relaunch elevated.</summary>
    public const string Wizard_Step5_ElevationConfirmSuffix = "Wizard_Step5_ElevationConfirmSuffix";

    /// <summary>Title of the elevation-relaunch confirmation dialog.</summary>
    public const string Wizard_Step5_ElevationConfirmTitle = "Wizard_Step5_ElevationConfirmTitle";

    /// <summary>The service reports nothing pending to re-apply.</summary>
    public const string Wizard_Step5_NothingPending = "Wizard_Step5_NothingPending";

    /// <summary>The service has applied the learned assignments; the visual check follows.</summary>
    public const string Wizard_Step5_Applied = "Wizard_Step5_Applied";

    /// <summary>{0} = monitor name, {1} = connector. Progress while calibrating one assignment.</summary>
    public const string Wizard_Step5_CalibratingItem = "Wizard_Step5_CalibratingItem";

    /// <summary>{0} = succeeded count, {1} = total count. Tabcal run summary.</summary>
    public const string Wizard_Step5_ResultCount = "Wizard_Step5_ResultCount";

    /// <summary>Instruction preceding the visual-check question.</summary>
    public const string Wizard_Step5_VisualCheckIntro = "Wizard_Step5_VisualCheckIntro";

    /// <summary>Question put to the operator after tabcal runs.</summary>
    public const string Wizard_Step5_VisualCheckQuestion = "Wizard_Step5_VisualCheckQuestion";

    /// <summary>Title of the visual-check confirmation dialog.</summary>
    public const string Wizard_Step5_VisualCheckTitle = "Wizard_Step5_VisualCheckTitle";

    /// <summary>Appended to the result line when the visual check was confirmed.</summary>
    public const string Wizard_Step5_ConfirmedSuffix = "Wizard_Step5_ConfirmedSuffix";

    /// <summary>Appended to the result line when the visual check was NOT confirmed.</summary>
    public const string Wizard_Step5_NotConfirmedSuffix = "Wizard_Step5_NotConfirmedSuffix";

    /// <summary>Record title used once the visual check itself is included.</summary>
    public const string Wizard_Step5_RecordTitle = "Wizard_Step5_RecordTitle";

    /// <summary>The calibration test threw before it could finish.</summary>
    public const string Wizard_Step5_Failed = "Wizard_Step5_Failed";

    // ---- Setup wizard: step 6 (summary) --------------------------------------------------------

    /// <summary>All steps passed.</summary>
    public const string Wizard_Step6_VerdictSuccess = "Wizard_Step6_VerdictSuccess";

    /// <summary>At least one step needs attention.</summary>
    public const string Wizard_Step6_VerdictPartial = "Wizard_Step6_VerdictPartial";

    /// <summary>The word "Step" in the per-step result line, e.g. "Step 3 — ...".</summary>
    public const string Wizard_Step6_StepWord = "Wizard_Step6_StepWord";

    /// <summary>Button: exports setup-report.json.</summary>
    public const string Wizard_Step6_ExportReport = "Wizard_Step6_ExportReport";

    /// <summary>Button: closes the wizard.</summary>
    public const string Wizard_Step6_Close = "Wizard_Step6_Close";

    /// <summary>{0} = file path. Shown once the report has been saved.</summary>
    public const string Wizard_Step6_SavedPath = "Wizard_Step6_SavedPath";

    /// <summary>{0} = file path. Status-line phrasing of the same fact as <see cref="Wizard_Step6_SavedPath"/>.</summary>
    public const string Wizard_Step6_SavedToStatus = "Wizard_Step6_SavedToStatus";

    /// <summary>The report could not be written to disk.</summary>
    public const string Wizard_Step6_ExportFailed = "Wizard_Step6_ExportFailed";

    // ---- EDID & Display manager: chrome ---------------------------------------------------------

    /// <summary>Window title / header.</summary>
    public const string EdidUi_Title = "EdidUi_Title";

    /// <summary>Button: reloads ports and templates from the service.</summary>
    public const string EdidUi_Refresh = "EdidUi_Refresh";

    /// <summary>Bold lead-in of the status-legend card.</summary>
    public const string EdidUi_LegendHeading = "EdidUi_LegendHeading";

    /// <summary>Legend term: a valid EDID.</summary>
    public const string EdidUi_LegendValid = "EdidUi_LegendValid";

    /// <summary>Legend description: a valid EDID.</summary>
    public const string EdidUi_LegendValidDesc = "EdidUi_LegendValidDesc";

    /// <summary>Legend term: two monitors report the same identity.</summary>
    public const string EdidUi_LegendCollision = "EdidUi_LegendCollision";

    /// <summary>Legend description: identity collision.</summary>
    public const string EdidUi_LegendCollisionDesc = "EdidUi_LegendCollisionDesc";

    /// <summary>Status badge: no EDID and the port is not even attached.</summary>
    public const string EdidUi_StatusNoEdid = "EdidUi_StatusNoEdid";

    /// <summary>Legend term: a picture with no EDID at all.</summary>
    public const string EdidUi_LegendExtender = "EdidUi_LegendExtender";

    /// <summary>Legend description: extender port with no EDID.</summary>
    public const string EdidUi_LegendExtenderDesc = "EdidUi_LegendExtenderDesc";

    /// <summary>Legend term: an override is active.</summary>
    public const string EdidUi_LegendOverride = "EdidUi_LegendOverride";

    /// <summary>Legend description: override active.</summary>
    public const string EdidUi_LegendOverrideDesc = "EdidUi_LegendOverrideDesc";

    /// <summary>Column: graphics connector.</summary>
    public const string EdidUi_ColConnector = "EdidUi_ColConnector";

    /// <summary>Column: monitor name.</summary>
    public const string EdidUi_ColMonitor = "EdidUi_ColMonitor";

    /// <summary>Column: active display mode.</summary>
    public const string EdidUi_ColMode = "EdidUi_ColMode";

    /// <summary>Column: serial number.</summary>
    public const string EdidUi_ColSerial = "EdidUi_ColSerial";

    /// <summary>Column: EDID status badge.</summary>
    public const string EdidUi_ColStatus = "EdidUi_ColStatus";

    /// <summary>Column: PnP device instance id.</summary>
    public const string EdidUi_ColPnpId = "EdidUi_ColPnpId";

    /// <summary>Bold lead-in of the template-application panel.</summary>
    public const string EdidUi_ApplyTemplateHeading = "EdidUi_ApplyTemplateHeading";

    /// <summary>Label above the template picker.</summary>
    public const string EdidUi_TemplateLabel = "EdidUi_TemplateLabel";

    /// <summary>{0} = file path. Shown when a file was dropped onto the window.</summary>
    public const string EdidUi_DroppedFile = "EdidUi_DroppedFile";

    /// <summary>Button: browse for a template file.</summary>
    public const string EdidUi_BrowseFile = "EdidUi_BrowseFile";

    /// <summary>Label above the custom-serial text box.</summary>
    public const string EdidUi_SerialLabel = "EdidUi_SerialLabel";

    /// <summary>Button: applies the selected template to the selected port.</summary>
    public const string EdidUi_ApplyTemplate = "EdidUi_ApplyTemplate";

    /// <summary>Button: synthesises an EDID from the port's current mode.</summary>
    public const string EdidUi_Synthesize = "EdidUi_Synthesize";

    /// <summary>Button: clears a dropped/browsed file selection.</summary>
    public const string EdidUi_ClearFile = "EdidUi_ClearFile";

    /// <summary>Button: resolves every identity collision automatically.</summary>
    public const string EdidUi_ResolveCollisions = "EdidUi_ResolveCollisions";

    /// <summary>Button: previews collision resolution without writing anything.</summary>
    public const string EdidUi_PreviewDryRun = "EdidUi_PreviewDryRun";

    /// <summary>Button: removes the override from the selected port only.</summary>
    public const string EdidUi_RestoreSelected = "EdidUi_RestoreSelected";

    /// <summary>Button: removes every override on the machine.</summary>
    public const string EdidUi_RestoreAll = "EdidUi_RestoreAll";

    // ---- EDID & Display manager: view-model status text -----------------------------------------

    /// <summary>{0} = collision count. Full-identity collisions were found.</summary>
    public const string EdidUi_CollisionSummaryFull = "EdidUi_CollisionSummaryFull";

    /// <summary>{0} = group count. Only numeric serials collide; no action needed.</summary>
    public const string EdidUi_CollisionSummaryNumericOnly = "EdidUi_CollisionSummaryNumericOnly";

    /// <summary>No collisions of any kind.</summary>
    public const string EdidUi_CollisionSummaryNone = "EdidUi_CollisionSummaryNone";

    /// <summary>{0} = port count, {1} = template count. Overview line after a refresh.</summary>
    public const string EdidUi_StatusSummary = "EdidUi_StatusSummary";

    /// <summary>Shown while a refresh is loading displays and templates.</summary>
    public const string EdidUi_LoadingRunning = "EdidUi_LoadingRunning";

    /// <summary>The service could not be reached.</summary>
    public const string EdidUi_ServiceUnreachable = "EdidUi_ServiceUnreachable";

    /// <summary>Collision resolution is running.</summary>
    public const string EdidUi_ResolvingRunning = "EdidUi_ResolvingRunning";

    /// <summary>A dry run is in progress.</summary>
    public const string EdidUi_PreviewRunning = "EdidUi_PreviewRunning";

    /// <summary>All overrides are being removed.</summary>
    public const string EdidUi_RestoringAllRunning = "EdidUi_RestoringAllRunning";

    /// <summary>No port was selected before an action that needs one.</summary>
    public const string EdidUi_SelectPortFirst = "EdidUi_SelectPortFirst";

    /// <summary>{0} = connector label. A single port's override is being removed.</summary>
    public const string EdidUi_RestoringSelectedRunning = "EdidUi_RestoringSelectedRunning";

    /// <summary>{0} = connector label. A template is being applied.</summary>
    public const string EdidUi_ApplyingTemplateRunning = "EdidUi_ApplyingTemplateRunning";

    /// <summary>The selected port has no active display mode to synthesise from.</summary>
    public const string EdidUi_NoActiveMode = "EdidUi_NoActiveMode";

    /// <summary>{0} = connector label. Synthesis is in progress.</summary>
    public const string EdidUi_SynthesizingRunning = "EdidUi_SynthesizingRunning";

    /// <summary>No template is selected and no file was dropped.</summary>
    public const string EdidUi_SelectTemplateOrFile = "EdidUi_SelectTemplateOrFile";

    /// <summary>The built-in template has not been materialised to disk yet.</summary>
    public const string EdidUi_BuiltinTemplatePending = "EdidUi_BuiltinTemplatePending";

    /// <summary>{0} = file name. No EDID at all was found in the file.</summary>
    public const string EdidUi_NoEdidInFile = "EdidUi_NoEdidInFile";

    /// <summary>{0} = file name. Every EDID in the .inf has an invalid checksum.</summary>
    public const string EdidUi_InfAllChecksumsInvalid = "EdidUi_InfAllChecksumsInvalid";

    /// <summary>{0} = file name, {1} = entry count, {2} = section name used.</summary>
    public const string EdidUi_InfMultipleEntries = "EdidUi_InfMultipleEntries";

    /// <summary>{0} = file name. The file is not a structurally valid EDID.</summary>
    public const string EdidUi_FileNotValidEdid = "EdidUi_FileNotValidEdid";

    /// <summary>{0} = file name. The file could not be read at all.</summary>
    public const string EdidUi_FileReadFailed = "EdidUi_FileReadFailed";

    /// <summary>{0} = file name. A file was accepted via drag &amp; drop.</summary>
    public const string EdidUi_FileAccepted = "EdidUi_FileAccepted";

    /// <summary>The dropped/browsed file selection was cleared.</summary>
    public const string EdidUi_FileSelectionCleared = "EdidUi_FileSelectionCleared";

    /// <summary>Title of the template file picker dialog.</summary>
    public const string EdidUi_PickTemplateTitle = "EdidUi_PickTemplateTitle";

    /// <summary>File filter for the template file picker dialog.</summary>
    public const string EdidUi_PickTemplateFilter = "EdidUi_PickTemplateFilter";

    /// <summary>An EDID operation threw before it could report a structured result.</summary>
    public const string EdidUi_OperationFailed = "EdidUi_OperationFailed";

    // ---- Help window --------------------------------------------------------------------------

    /// <summary>Window title.</summary>
    public const string Help_WindowTitle = "Help_WindowTitle";

    /// <summary>Header title.</summary>
    public const string Help_HeaderTitle = "Help_HeaderTitle";

    /// <summary>Header subtitle.</summary>
    public const string Help_HeaderSubtitle = "Help_HeaderSubtitle";

    /// <summary>Section 1 heading.</summary>
    public const string Help_S1_Heading = "Help_S1_Heading";

    /// <summary>Section 1 body.</summary>
    public const string Help_S1_Body = "Help_S1_Body";

    /// <summary>Section 2 heading.</summary>
    public const string Help_S2_Heading = "Help_S2_Heading";

    /// <summary>Section 2, bold "Step 1" label.</summary>
    public const string Help_S2_Step1Label = "Help_S2_Step1Label";

    /// <summary>Section 2, step 1 text.</summary>
    public const string Help_S2_Step1Text = "Help_S2_Step1Text";

    /// <summary>Section 2, bold "Step 2" label.</summary>
    public const string Help_S2_Step2Label = "Help_S2_Step2Label";

    /// <summary>Section 2, step 2 text.</summary>
    public const string Help_S2_Step2Text = "Help_S2_Step2Text";

    /// <summary>Section 2, bold "Step 3" label.</summary>
    public const string Help_S2_Step3Label = "Help_S2_Step3Label";

    /// <summary>Section 2, step 3 text.</summary>
    public const string Help_S2_Step3Text = "Help_S2_Step3Text";

    /// <summary>Section 2, bold "Step 4" label.</summary>
    public const string Help_S2_Step4Label = "Help_S2_Step4Label";

    /// <summary>Section 2, step 4 text.</summary>
    public const string Help_S2_Step4Text = "Help_S2_Step4Text";

    /// <summary>Section 2, closing paragraph.</summary>
    public const string Help_S2_Closing = "Help_S2_Closing";

    /// <summary>Section 3 heading.</summary>
    public const string Help_S3_Heading = "Help_S3_Heading";

    /// <summary>Section 3, first body fragment (before the bold "other").</summary>
    public const string Help_S3_Body1 = "Help_S3_Body1";

    /// <summary>Section 3, bold "other" emphasis.</summary>
    public const string Help_S3_BoldOther = "Help_S3_BoldOther";

    /// <summary>Section 3, second body fragment (after the bold "other").</summary>
    public const string Help_S3_Body2 = "Help_S3_Body2";

    /// <summary>Section 3, third body paragraph.</summary>
    public const string Help_S3_Body3 = "Help_S3_Body3";

    /// <summary>Section 3, bold "Important:" label.</summary>
    public const string Help_S3_ImportantLabel = "Help_S3_ImportantLabel";

    /// <summary>Section 3, fourth body paragraph (after "Important:").</summary>
    public const string Help_S3_Body4 = "Help_S3_Body4";

    /// <summary>Section 4 heading.</summary>
    public const string Help_S4_Heading = "Help_S4_Heading";

    /// <summary>Section 4, intro line.</summary>
    public const string Help_S4_Body1 = "Help_S4_Body1";

    /// <summary>Section 4, bold "a) Generic EDID." label.</summary>
    public const string Help_S4_LabelA = "Help_S4_LabelA";

    /// <summary>Section 4, text for symptom (a).</summary>
    public const string Help_S4_TextA = "Help_S4_TextA";

    /// <summary>Section 4, bold "b) No EDID at all." label.</summary>
    public const string Help_S4_LabelB = "Help_S4_LabelB";

    /// <summary>Section 4, text for symptom (b).</summary>
    public const string Help_S4_TextB = "Help_S4_TextB";

    /// <summary>Section 4, bold "Order of remedies:" label.</summary>
    public const string Help_S4_SolutionOrderLabel = "Help_S4_SolutionOrderLabel";

    /// <summary>Section 4, the two-step remedy list.</summary>
    public const string Help_S4_SolutionSteps = "Help_S4_SolutionSteps";

    /// <summary>Section 4, bold "On the synthetic EDID:" label.</summary>
    public const string Help_S4_SyntheticLabel = "Help_S4_SyntheticLabel";

    /// <summary>Section 4, synthetic-EDID caveat text.</summary>
    public const string Help_S4_SyntheticText = "Help_S4_SyntheticText";

    /// <summary>Section 4, bold "On the handshake:" label.</summary>
    public const string Help_S4_HandshakeLabel = "Help_S4_HandshakeLabel";

    /// <summary>Section 4, handshake caveat text.</summary>
    public const string Help_S4_HandshakeText = "Help_S4_HandshakeText";

    /// <summary>Section 5 heading.</summary>
    public const string Help_S5_Heading = "Help_S5_Heading";

    /// <summary>Section 5, first body paragraph.</summary>
    public const string Help_S5_Body1 = "Help_S5_Body1";

    /// <summary>Section 5, second body paragraph.</summary>
    public const string Help_S5_Body2 = "Help_S5_Body2";

    /// <summary>Section 6 heading.</summary>
    public const string Help_S6_Heading = "Help_S6_Heading";

    /// <summary>Section 6, first body paragraph.</summary>
    public const string Help_S6_Body1 = "Help_S6_Body1";

    /// <summary>Section 6, "Logs live under:" label.</summary>
    public const string Help_S6_LogsLabel = "Help_S6_LogsLabel";

    /// <summary>Section 6, "EDID templates live under:" label.</summary>
    public const string Help_S6_TemplatesLabel = "Help_S6_TemplatesLabel";

    /// <summary>Help section "controls": heading, tray and main-window reference.</summary>
    public const string Help_S7_Heading = "Help_S7_Heading";
    /// <summary>Help section "controls": tray menu label.</summary>
    public const string Help_S7_TrayLabel = "Help_S7_TrayLabel";
    /// <summary>Help section "controls": tray menu entries.</summary>
    public const string Help_S7_TrayText = "Help_S7_TrayText";
    /// <summary>Help section "controls": main window label.</summary>
    public const string Help_S7_MainLabel = "Help_S7_MainLabel";
    /// <summary>Help section "controls": main window buttons.</summary>
    public const string Help_S7_MainText = "Help_S7_MainText";

    /// <summary>Help section "symptoms and remedies": heading.</summary>
    public const string Help_S8_Heading = "Help_S8_Heading";
    /// <summary>Symptom: touch lands on the wrong screen.</summary>
    public const string Help_S8_WrongScreenLabel = "Help_S8_WrongScreenLabel";
    /// <summary>Remedy: touch lands on the wrong screen.</summary>
    public const string Help_S8_WrongScreenText = "Help_S8_WrongScreenText";
    /// <summary>Symptom: touch briefly unresponsive.</summary>
    public const string Help_S8_BriefOutageLabel = "Help_S8_BriefOutageLabel";
    /// <summary>Explanation: touch briefly unresponsive.</summary>
    public const string Help_S8_BriefOutageText = "Help_S8_BriefOutageText";
    /// <summary>Symptom: unresolvable mappings reported.</summary>
    public const string Help_S8_UnresolvableLabel = "Help_S8_UnresolvableLabel";
    /// <summary>Remedy: unresolvable mappings reported.</summary>
    public const string Help_S8_UnresolvableText = "Help_S8_UnresolvableText";
    /// <summary>Symptom: service not reachable.</summary>
    public const string Help_S8_ServiceLabel = "Help_S8_ServiceLabel";
    /// <summary>Remedy: service not reachable.</summary>
    public const string Help_S8_ServiceText = "Help_S8_ServiceText";
    /// <summary>Symptom: a touch USB cable was moved.</summary>
    public const string Help_S8_ReplugLabel = "Help_S8_ReplugLabel";
    /// <summary>Remedy: a touch USB cable was moved.</summary>
    public const string Help_S8_ReplugText = "Help_S8_ReplugText";
    /// <summary>Symptom: screens black after restart.</summary>
    public const string Help_S8_BlackLabel = "Help_S8_BlackLabel";
    /// <summary>Remedy: screens black after restart.</summary>
    public const string Help_S8_BlackText = "Help_S8_BlackText";

    /// <summary>Close button.</summary>
    public const string Help_Close = "Help_Close";

    // ---- Diagnostics log window -----------------------------------------------------------------

    /// <summary>Window title.</summary>
    public const string Log_WindowTitle = "Log_WindowTitle";

    /// <summary>Button: clears the on-screen log.</summary>
    public const string Log_Clear = "Log_Clear";

    /// <summary>Button: copies the log to the clipboard.</summary>
    public const string Log_Copy = "Log_Copy";

    // ---- Tray menu, dialogs, About ---------------------------------------------------------------

    /// <summary>Menu: opens the main configuration window.</summary>
    public const string Tray_OpenConfig = "Tray_OpenConfig";

    /// <summary>Menu: asks the service for its status.</summary>
    public const string Tray_CheckStatus = "Tray_CheckStatus";

    /// <summary>Menu: opens the setup wizard.</summary>
    public const string Tray_Wizard = "Tray_Wizard";

    /// <summary>Menu: opens the EDID &amp; display manager.</summary>
    public const string Tray_EdidManager = "Tray_EdidManager";

    /// <summary>Menu: opens the help window.</summary>
    public const string Tray_Help = "Tray_Help";

    /// <summary>Menu: exports the diagnostics report.</summary>
    public const string Tray_ExportLog = "Tray_ExportLog";

    /// <summary>Menu: shows the About dialog.</summary>
    public const string Tray_About = "Tray_About";

    /// <summary>Menu: exits the resident agent.</summary>
    public const string Tray_Exit = "Tray_Exit";

    /// <summary>Title of the exit-confirmation dialog.</summary>
    public const string Tray_ExitConfirmTitle = "Tray_ExitConfirmTitle";

    /// <summary>Full body of the exit warning, verbatim wording required by the operator.</summary>
    public const string Tray_ExitWarning = "Tray_ExitWarning";

    /// <summary>Balloon/tooltip text while the service status is being checked.</summary>
    public const string Tray_CheckingStatus = "Tray_CheckingStatus";

    /// <summary>Tray menu: apply every stored assignment now.</summary>
    public const string Tray_ApplyNow = "Tray_ApplyNow";

    /// <summary>Tray menu: header of the technician submenu.</summary>
    public const string Tray_DiagnoseMenu = "Tray_DiagnoseMenu";

    /// <summary>Tooltip/balloon: the service answered.</summary>
    public const string Tray_StatusReachable = "Tray_StatusReachable";

    /// <summary>Tooltip/balloon: the service did not answer.</summary>
    public const string Tray_StatusUnreachable = "Tray_StatusUnreachable";

    /// <summary>Tooltip/balloon: the status check itself failed.</summary>
    public const string Tray_StatusUnknown = "Tray_StatusUnknown";

    /// <summary>Tooltip: reload monitors.</summary>
    public const string Tip_LoadMonitors = "Tip_LoadMonitors";
    /// <summary>Tooltip: learn the selected screen by touch.</summary>
    public const string Tip_LearnScreen = "Tip_LearnScreen";
    /// <summary>Tooltip: apply every stored assignment now (main window and tray).</summary>
    public const string Tip_ApplyNow = "Tip_ApplyNow";
    /// <summary>Tooltip: check the background service (main window and tray).</summary>
    public const string Tip_CheckStatus = "Tip_CheckStatus";
    /// <summary>Tooltip: show this session's log.</summary>
    public const string Tip_ShowLog = "Tip_ShowLog";
    /// <summary>Tooltip (tray): open the main window.</summary>
    public const string Tip_OpenConfig = "Tip_OpenConfig";
    /// <summary>Tooltip (tray): setup wizard.</summary>
    public const string Tip_Wizard = "Tip_Wizard";
    /// <summary>Tooltip (tray): EDID manager.</summary>
    public const string Tip_EdidManager = "Tip_EdidManager";
    /// <summary>Tooltip (tray): help window.</summary>
    public const string Tip_Help = "Tip_Help";
    /// <summary>Tooltip (tray): export the diagnostics report.</summary>
    public const string Tip_ExportLog = "Tip_ExportLog";
    /// <summary>Tooltip (tray): language submenu.</summary>
    public const string Tip_Language = "Tip_Language";
    /// <summary>Tooltip (tray): exit the agent.</summary>
    public const string Tip_Exit = "Tip_Exit";

    /// <summary>Main window button: apply every stored assignment now.</summary>
    public const string Main_ApplyNow = "Main_ApplyNow";

    /// <summary>Status line while the service applies the assignments.</summary>
    public const string Main_ApplyingNow = "Main_ApplyingNow";

    /// <summary>{0} = applied, {1} = stored, {2} = unresolvable, {3} = failed.</summary>
    public const string Main_ApplyNowResult = "Main_ApplyNowResult";

    /// <summary>Apply-now with no stored assignment at all.</summary>
    public const string Main_ApplyNowNothingStored = "Main_ApplyNowNothingStored";

    /// <summary>Apply-now could not reach the service.</summary>
    public const string Main_ApplyNowFailed = "Main_ApplyNowFailed";

    /// <summary>The EDID manager window could not be opened.</summary>
    public const string Tray_EdidManagerOpenFailed = "Tray_EdidManagerOpenFailed";

    /// <summary>The setup wizard window could not be opened.</summary>
    public const string Tray_WizardOpenFailed = "Tray_WizardOpenFailed";

    /// <summary>Title of the export-target save dialog.</summary>
    public const string Tray_ExportDialogTitle = "Tray_ExportDialogTitle";

    /// <summary>File filter for the export-target save dialog.</summary>
    public const string Tray_ExportDialogFilter = "Tray_ExportDialogFilter";

    /// <summary>{0} = file path. The diagnostics report was written successfully.</summary>
    public const string Tray_ExportSucceeded = "Tray_ExportSucceeded";

    /// <summary>The diagnostics report could not be written.</summary>
    public const string Tray_ExportFailed = "Tray_ExportFailed";

    /// <summary>Generic message-box caption for tray-originated dialogs.</summary>
    public const string Tray_Caption = "Tray_Caption";

    /// <summary>About dialog body.</summary>
    public const string Tray_AboutBody = "Tray_AboutBody";

    // ---- App-level dialogs --------------------------------------------------------------------

    /// <summary>Caption of the unhandled-exception dialog.</summary>
    public const string App_UnhandledExceptionCaption = "App_UnhandledExceptionCaption";

    /// <summary>{0} = exception message. Body of the unhandled-exception dialog.</summary>
    public const string App_UnhandledExceptionBody = "App_UnhandledExceptionBody";

    // ---- Setup interaction service: dialogs and file pickers ------------------------------------

    /// <summary>Title of the EDID template picker dialog.</summary>
    public const string Interaction_PickTemplateTitle = "Interaction_PickTemplateTitle";

    /// <summary>File filter for the EDID template picker dialog.</summary>
    public const string Interaction_PickTemplateFilter = "Interaction_PickTemplateFilter";

    /// <summary>Title of the commissioning-report save dialog.</summary>
    public const string Interaction_SaveReportTitle = "Interaction_SaveReportTitle";

    /// <summary>File filter for the commissioning-report save dialog.</summary>
    public const string Interaction_SaveReportFilter = "Interaction_SaveReportFilter";
}
