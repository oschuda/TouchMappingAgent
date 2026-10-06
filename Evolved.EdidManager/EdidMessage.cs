namespace Evolved.EdidManager;

/// <summary>
/// An operator-facing result message, carried as a key plus its parameters rather than as
/// finished text.
///
/// WHY THIS TYPE EXISTS SEPARATELY: this module is standalone by design — its only dependency is
/// Microsoft.Extensions.Logging.Abstractions, so it can be lifted into another product without
/// dragging TouchMappingAgent along. Referencing the host's LocalizableText would break that.
/// The pair of types is deliberate duplication at a real assembly boundary; the host adapts one
/// into the other in a single line where the module meets the pipe contract.
///
/// The module never formats these itself. It has no idea which language the eventual operator
/// reads, and in this host it runs as LocalSystem in Session 0, whose culture is unrelated to the
/// operator's.
/// </summary>
/// <param name="Key">A constant from <see cref="EdidMessageKeys"/>.</param>
/// <param name="Arguments">Positional format parameters, already rendered to strings.</param>
public sealed record EdidMessage(string Key, IReadOnlyList<string> Arguments)
{
    private static readonly string[] NoArguments = [];

    /// <summary>Creates a message with no parameters.</summary>
    public static EdidMessage Of(string key) => new(key, NoArguments);

    /// <summary>
    /// Creates a message whose parameters are rendered with the invariant culture, so a value
    /// produced here does not carry Session 0's number formatting into the operator's sentence.
    /// </summary>
    public static EdidMessage Of(string key, params object?[] arguments)
    {
        if (arguments is null || arguments.Length == 0)
            return Of(key);

        var rendered = new string[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            rendered[i] = arguments[i] switch
            {
                null => string.Empty,
                IFormattable formattable =>
                    formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                var other => other.ToString() ?? string.Empty
            };
        }

        return new EdidMessage(key, rendered);
    }

    /// <summary>Renders the message for a log file, where it must be readable without resources.</summary>
    public string ToDiagnosticString() =>
        Arguments.Count == 0 ? Key : $"{Key}({string.Join(", ", Arguments)})";
}

/// <summary>
/// Message keys this module can produce. The host must carry a translation for each; its
/// localiser test asserts that, so an untranslated key fails the build rather than the
/// commissioning.
/// </summary>
public static class EdidMessageKeys
{
    /// <summary>{0} = number of monitors that would be rewritten. Nothing was changed.</summary>
    public const string DryRun = "Edid_DryRun";

    /// <summary>{0} = device instance id, {1} = the new serial. Applied and re-enumerated.</summary>
    public const string TemplateApplied = "Edid_TemplateApplied";

    /// <summary>
    /// {0} = device instance id, {1} = the new serial. Applied, but the device node could not be
    /// re-enumerated, so the change takes effect at the next reboot at the latest.
    /// </summary>
    public const string TemplateAppliedNoReenumerate = "Edid_TemplateAppliedNoReenumerate";

    /// <summary>{0} = device instance id. Override removed and the device node re-enumerated.</summary>
    public const string OverrideRemoved = "Edid_OverrideRemoved";

    /// <summary>{0} = device instance id. Override removed; re-enumeration failed.</summary>
    public const string OverrideRemovedNoReenumerate = "Edid_OverrideRemovedNoReenumerate";

    /// <summary>Scan found no duplicate serial numbers, so nothing was rewritten.</summary>
    public const string NoCollisions = "Edid_NoCollisions";

    /// <summary>{0} = monitors given a unique serial. All succeeded.</summary>
    public const string CollisionsResolved = "Edid_CollisionsResolved";

    /// <summary>{0} = monitors changed, {1} = monitors that failed.</summary>
    public const string CollisionsResolvedWithFailures = "Edid_CollisionsResolvedWithFailures";

    /// <summary>
    /// The operation writes under HKLM\SYSTEM\CurrentControlSet\Enum and needs administrative
    /// rights the current process does not have.
    /// </summary>
    public const string ElevationRequired = "Edid_ElevationRequired";

    /// <summary>{0} = the rejected value. Not a well-formed PnP device instance id.</summary>
    public const string InvalidPnpId = "Edid_InvalidPnpId";

    /// <summary>{0} = actual byte count, {1} = required block size.</summary>
    public const string TemplateWrongSize = "Edid_TemplateWrongSize";

    /// <summary>The template does not start with the VESA EDID header.</summary>
    public const string TemplateBadHeader = "Edid_TemplateBadHeader";

    /// <summary>{0} = stored checksum, {1} = computed checksum.</summary>
    public const string TemplateBadChecksum = "Edid_TemplateBadChecksum";

    /// <summary>The block failed its own checksum check after being stamped — an internal fault.</summary>
    public const string StampChecksumFailed = "Edid_StampChecksumFailed";

    /// <summary>{0} = device instance id. The registry write did not succeed.</summary>
    public const string OverrideWriteFailed = "Edid_OverrideWriteFailed";

    /// <summary>{0} = device instance id. There was no override to remove.</summary>
    public const string NoOverridePresent = "Edid_NoOverridePresent";

    /// <summary>No overrides were stored anywhere, so RestoreDefaults had nothing to do.</summary>
    public const string NoOverridesStored = "Edid_NoOverridesStored";

    /// <summary>{0} = number of overrides removed.</summary>
    public const string OverridesRemoved = "Edid_OverridesRemoved";
}
