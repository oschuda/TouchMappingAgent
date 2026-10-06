namespace TouchMappingAgent.Shared.Localization;

/// <summary>
/// A piece of operator-facing text that crosses the pipe as a message key plus its parameters,
/// not as a finished sentence.
///
/// WHY: the service runs as LocalSystem in Session 0. Its thread culture is whatever the machine
/// account happens to have — typically the system default, which is NOT the language the operator
/// logged in with. A service that formats sentences itself therefore produces text in the wrong
/// language whenever the two differ, and there is no way for the client to correct it after the
/// fact. Passing key + arguments moves the formatting to the only process that knows the
/// operator's language: the client, in the interactive session.
///
/// A second reason is version skew. The installer can leave a newer service running against an
/// older client (or the reverse) between a service restart and a client restart. A key the client
/// does not know degrades to something readable and reportable instead of an empty label — see
/// the fallback contract on the client-side resolver.
///
/// This type deliberately carries NO English or German text. Diagnostic detail that is not
/// operator-facing — exception messages, tabcal stderr, device paths — stays a plain
/// <see cref="string"/> on the surrounding contract and is never translated.
/// </summary>
/// <param name="Key">
/// A key from <see cref="MessageKeys"/>. Both sides compile against those constants, so a typo is
/// a build error rather than a blank label discovered on the machine.
/// </param>
/// <param name="Arguments">
/// Positional format arguments, already rendered to strings by the sender. They are values the
/// client cannot derive on its own (a connector label, a count, a monitor name) and are
/// substituted into the localised format string in order.
/// </param>
public sealed record LocalizableText(string Key, IReadOnlyList<string> Arguments)
{
    /// <summary>An empty argument list, shared so parameterless messages allocate nothing.</summary>
    private static readonly string[] NoArguments = [];

    /// <summary>
    /// Creates a message with no parameters.
    /// </summary>
    public static LocalizableText Of(string key) => new(key, NoArguments);

    /// <summary>
    /// Creates a message whose parameters are rendered with the invariant culture.
    /// </summary>
    /// <remarks>
    /// Invariant, not current culture: these arguments are produced in Session 0 and consumed in
    /// the operator's session. Formatting a number here with the service's culture and displaying
    /// it under the operator's would mix conventions within one sentence. Values that genuinely
    /// need cultural formatting (dates, decimals) should be passed as raw components and formatted
    /// by the client instead.
    /// </remarks>
    public static LocalizableText Of(string key, params object?[] arguments)
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

        return new LocalizableText(key, rendered);
    }

    /// <summary>
    /// Renders the message for a log file or audit trail, where it must be readable without a
    /// resource lookup and must stay stable across languages.
    /// </summary>
    public string ToDiagnosticString() =>
        Arguments.Count == 0 ? Key : $"{Key}({string.Join(", ", Arguments)})";
}
