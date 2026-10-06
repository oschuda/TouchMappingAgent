namespace TouchMappingAgent.Shared.Localization;

/// <summary>
/// The message keys the service may send to the client.
///
/// This class is the contract between the two processes. It lives in Shared so that the sending
/// side (service) and the resolving side (client resource files) reference the same constants:
/// a mistyped key fails the build instead of surfacing as an untranslated label on the machine.
///
/// NAMING: "Area_Condition". The area matches the feature that produces the message, so a key
/// read from a log or a setup-report.json can be traced back to its source without a search.
///
/// Every key here must have a matching entry in the client's Strings.resx. The localiser test
/// asserts that automatically, so adding a key without translating it fails the test suite rather
/// than the commissioning.
/// </summary>
public static class MessageKeys
{
    // ---- Hardware status, shown in the tray balloon and the main window -------------------

    /// <summary>No assignment has ever been learned on this machine. No arguments.</summary>
    public const string Status_NoMappingsStored = "Status_NoMappingsStored";

    /// <summary>{0} = number of stored assignments that no longer match the present hardware.</summary>
    public const string Status_MappingsUnresolvable = "Status_MappingsUnresolvable";

    /// <summary>{0} = number of digitizers that share a hardware anchor and cannot be told apart.</summary>
    public const string Status_AmbiguousDigitizers = "Status_AmbiguousDigitizers";

    /// <summary>
    /// An assignment still resolves, but only through a weaker anchor than the one it was learned
    /// with. {0} = monitor name, {1} = connector label, {2} = match quality.
    /// </summary>
    public const string Status_MappingDegraded = "Status_MappingDegraded";

    /// <summary>The service could not determine hardware status at all. No arguments.</summary>
    public const string Status_Unavailable = "Status_Unavailable";

    // ---- Why a stored assignment is being re-applied --------------------------------------

    /// <summary>Monitor re-identified by its PnP device path — the strongest anchor.</summary>
    public const string Reapply_ReasonExact = "Reapply_ReasonExact";

    /// <summary>Monitor re-identified by graphics connector; the device path differs.</summary>
    public const string Reapply_ReasonConnector = "Reapply_ReasonConnector";

    /// <summary>Monitor re-identified by screen position only — the weakest anchor.</summary>
    public const string Reapply_ReasonBoundsOnly = "Reapply_ReasonBoundsOnly";

    /// <summary>Fallback when the match quality is not one of the known values.</summary>
    public const string Reapply_ReasonStored = "Reapply_ReasonStored";

    /// <summary>{0} = monitor name, {1} = connector label. Re-application succeeded.</summary>
    public const string Reapply_Succeeded = "Reapply_Succeeded";

    /// <summary>{0} = monitor name. Re-application failed; detail is in the log.</summary>
    public const string Reapply_Failed = "Reapply_Failed";

    /// <summary>
    /// The assignment names a digitizer that is not attached, so tabcal.exe would report success
    /// without calibrating anything. No arguments.
    /// </summary>
    public const string Reapply_DigitizerAbsent = "Reapply_DigitizerAbsent";

    /// <summary>The assignment carries no device path at all. No arguments.</summary>
    public const string Reapply_NoDevicePath = "Reapply_NoDevicePath";

    /// <summary>{0} = exception type and message. Digitizer enumeration itself failed.</summary>
    public const string Reapply_EnumerationFailed = "Reapply_EnumerationFailed";

    // ---- EDID management -------------------------------------------------------------------
    //
    // Only the keys the SERVICE itself produces live here. Everything the Evolved.EdidManager
    // module reports — applied templates, collision counts, elevation, template validation — is
    // owned by EdidMessageKeys in that module and passes through unchanged. One key, one owner:
    // declaring a module key here as well would give the same resource string two definitions
    // that could silently drift apart.

    /// <summary>A template could not be applied to the target display. No arguments.</summary>
    public const string Edid_TemplateApplyFailed = "Edid_TemplateApplyFailed";

    /// <summary>The de-collision run threw before it could report a result. No arguments.</summary>
    public const string Edid_DecollisionFailed = "Edid_DecollisionFailed";

    /// <summary>Restoring the stock EDID threw. No arguments.</summary>
    public const string Edid_RestoreFailed = "Edid_RestoreFailed";

    /// <summary>Synthesising a replacement EDID threw. No arguments.</summary>
    public const string Edid_SynthesisFailed = "Edid_SynthesisFailed";

    /// <summary>The template payload the client sent is not valid Base64. No arguments.</summary>
    public const string Edid_InvalidBase64 = "Edid_InvalidBase64";

    /// <summary>{0} = width, {1} = height, {2} = refresh rate. The mode has no EDID representation.</summary>
    public const string Edid_ModeNotRepresentable = "Edid_ModeNotRepresentable";

    // ---- Service availability ---------------------------------------------------------------

    /// <summary>The background service is not reachable over the pipe. No arguments.</summary>
    public const string Service_Unavailable = "Service_Unavailable";
}
