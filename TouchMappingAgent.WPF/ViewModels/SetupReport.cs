namespace TouchMappingAgent.WPF.ViewModels;

/// <summary>
/// Commissioning record written at the end of the wizard.
///
/// Serialised to JSON rather than prose because it is meant to be diffed against the next
/// commissioning of the same machine: which connector carried which panel, which USB port held
/// which digitizer, and whether the operator confirmed the pointer actually followed their
/// finger. Those are the facts a later fault report has to be checked against.
/// </summary>
public sealed record SetupReport
{
    /// <summary>Schema version, so a later reader can tell the shape.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Product that produced the report.</summary>
    public string Tool { get; init; } = "MultiTouch Agent Setup-Assistent";

    /// <summary>Agent version.</summary>
    public string ToolVersion { get; init; } = "1.0.0";

    /// <summary>Machine the commissioning ran on.</summary>
    public string MachineName { get; init; } = Environment.MachineName;

    /// <summary>Operator account.</summary>
    public string UserName { get; init; } = Environment.UserName;

    /// <summary>When the wizard finished.</summary>
    public string CompletedUtc { get; init; } = DateTime.UtcNow.ToString("O");

    /// <summary>True when the wizard ran elevated.</summary>
    public bool WasElevated { get; init; }

    /// <summary>Outcome of each step.</summary>
    public IReadOnlyList<SetupStepResult> Steps { get; init; } = Array.Empty<SetupStepResult>();

    /// <summary>Monitors present at the end of the run.</summary>
    public IReadOnlyList<SetupMonitorRecord> Monitors { get; init; } = Array.Empty<SetupMonitorRecord>();

    /// <summary>Assignments established or confirmed.</summary>
    public IReadOnlyList<SetupAssignmentRecord> Assignments { get; init; } = Array.Empty<SetupAssignmentRecord>();

    /// <summary>
    /// EDID identities that still collide after the run. Non-empty means the commissioning is
    /// incomplete, however green the rest looks.
    /// </summary>
    public int RemainingEdidCollisions { get; init; }

    /// <summary>
    /// The operator's answer to "does the pointer follow your finger on every display".
    /// Null when the test was skipped.
    /// </summary>
    public bool? PointerFollowsFingerConfirmed { get; init; }

    /// <summary>Overall verdict.</summary>
    public string Verdict { get; init; } = string.Empty;
}

/// <summary>Outcome of one wizard step.</summary>
/// <param name="Step">Step index (1-based, as shown to the operator).</param>
/// <param name="Title">Step name.</param>
/// <param name="Succeeded">Whether the step reached its success condition.</param>
/// <param name="Detail">What happened, in the operator's language.</param>
public sealed record SetupStepResult(int Step, string Title, bool Succeeded, string Detail);

/// <summary>A monitor as it stood at the end of the run.</summary>
/// <param name="PnpInstanceId">PnP device instance id.</param>
/// <param name="Connector">Graphics connector, e.g. "DP-2".</param>
/// <param name="MonitorName">Friendly name.</param>
/// <param name="SerialNumber">EDID serial after any override.</param>
/// <param name="SerialText">EDID serial text after any override.</param>
/// <param name="HasOverride">Whether an EDID override is installed.</param>
/// <param name="Bounds">Desktop bounds ("x,y,width,height").</param>
public sealed record SetupMonitorRecord(
    string PnpInstanceId,
    string Connector,
    string MonitorName,
    uint SerialNumber,
    string? SerialText,
    bool HasOverride,
    string Bounds);

/// <summary>One touch-to-monitor assignment.</summary>
/// <param name="TouchHardwareKey">USB parent instance of the digitizer.</param>
/// <param name="TouchProductName">Digitizer product name.</param>
/// <param name="MonitorConnector">Connector of the assigned monitor.</param>
/// <param name="MonitorHardwareKey">PnP device path of the assigned monitor.</param>
/// <param name="TabcalSucceeded">Whether tabcal.exe reported success for this pair.</param>
public sealed record SetupAssignmentRecord(
    string TouchHardwareKey,
    string TouchProductName,
    string MonitorConnector,
    string MonitorHardwareKey,
    bool TabcalSucceeded);
