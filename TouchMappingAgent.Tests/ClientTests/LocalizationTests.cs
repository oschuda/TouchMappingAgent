using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.RegularExpressions;
using Evolved.EdidManager;
using Microsoft.Extensions.Logging.Abstractions;
using TouchMappingAgent.Shared.Localization;
using TouchMappingAgent.WPF.Localization;
using Xunit;

namespace TouchMappingAgent.Tests.ClientTests;

/// <summary>
/// Guards the localisation contract.
///
/// The failure these tests exist to prevent is specific: a key added on the service side, or a
/// translation left out of one language, produces no build error and no exception. It shows up as
/// "[Some_Key]" on a commissioning screen at the plant — where nobody can fix it. Every rule the
/// contract depends on is therefore asserted here, at build time:
///
///   * every key constant that either side can send has a German translation;
///   * every German key has an English one, and vice versa;
///   * the two languages agree on how many placeholders a message takes, so a translation cannot
///     throw FormatException against the arguments the sender actually supplies.
/// </summary>
public class LocalizationTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>Matches {0}, {1}, and the aligned/formatted variants such as {0,-8:X2}.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static ResourceManager Resources => new(
        "TouchMappingAgent.WPF.Localization.Strings",
        typeof(LocExtension).Assembly);

    /// <summary>
    /// Every message key either process can send, gathered from the two key classes by
    /// reflection. Reflection rather than a hand-written list on purpose: a list would have to be
    /// maintained alongside the constants, and the day someone forgets is exactly the day this
    /// test needs to fire.
    /// </summary>
    public static TheoryData<string> AllMessageKeys
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var key in ConstantsOf(typeof(MessageKeys))
                         .Concat(ConstantsOf(typeof(EdidMessageKeys)))
                         .Concat(ConstantsOf(typeof(LocalizationKeys))))
                data.Add(key);

            return data;
        }
    }

    private static IEnumerable<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Distinct();

    [Theory]
    [MemberData(nameof(AllMessageKeys))]
    public void Every_message_key_has_a_German_translation(string key)
    {
        var value = Resources.GetString(key, German);

        Assert.False(string.IsNullOrWhiteSpace(value),
            $"MessageKeys/EdidMessageKeys declares '{key}' but Strings.resx has no entry for it. " +
            "The service can send this key, and the client would render it as a placeholder.");
    }

    [Theory]
    [MemberData(nameof(AllMessageKeys))]
    public void Every_message_key_has_an_English_translation(string key)
    {
        var value = Resources.GetString(key, English);

        Assert.False(string.IsNullOrWhiteSpace(value),
            $"'{key}' is missing from Strings.en.resx.");
    }

    [Theory]
    [MemberData(nameof(AllMessageKeys))]
    public void German_and_English_agree_on_placeholder_count(string key)
    {
        var de = Resources.GetString(key, German);
        var en = Resources.GetString(key, English);

        if (de == null || en == null)
            return; // Reported by the two tests above; not this test's failure to report.

        // Highest index + 1, not the raw count: a translation may legitimately use {0} twice or
        // reorder arguments, but it must not reference an argument the sender never supplies —
        // that is what throws FormatException at runtime.
        Assert.True(ArgumentCount(de) == ArgumentCount(en),
            $"'{key}' takes {ArgumentCount(de)} argument(s) in German but {ArgumentCount(en)} in " +
            $"English. Whichever is wrong will throw FormatException when the message is shown.\n" +
            $"  de: {de}\n  en: {en}");
    }

    private static int ArgumentCount(string format)
    {
        var matches = Placeholder.Matches(format);
        return matches.Count == 0
            ? 0
            : matches.Max(m => int.Parse(m.Groups[1].Value)) + 1;
    }

    [Fact]
    public void Neutral_resources_are_German()
    {
        // German is compiled into the main assembly rather than shipped as a satellite, so a
        // deployment that lost its satellite folders still shows readable German. If this ever
        // flips to English, an incomplete deployment would silently change language.
        var attribute = typeof(LocExtension).Assembly
            .GetCustomAttribute<NeutralResourcesLanguageAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("de", attribute!.CultureName);
    }

    [Fact]
    public void Unknown_key_renders_as_a_visible_placeholder()
    {
        var localizer = new Localizer(NullLogger<Localizer>.Instance, Resources);

        var rendered = localizer["This_Key_Does_Not_Exist"];

        // Bracketed rather than empty: a blank label on a commissioning screen is
        // indistinguishable from a broken layout, whereas this can be read out over the phone.
        Assert.Equal("[This_Key_Does_Not_Exist]", rendered);
    }

    [Fact]
    public void Arguments_are_substituted_positionally()
    {
        var localizer = new Localizer(NullLogger<Localizer>.Instance, Resources);

        var rendered = localizer.Format(LocalizableText.Of(
            MessageKeys.Status_MappingDegraded, "DM7000", "DP-1", "Connector"));

        Assert.Contains("DM7000", rendered);
        Assert.Contains("DP-1", rendered);
        Assert.Contains("Connector", rendered);
        Assert.DoesNotContain("{0}", rendered);
    }

    [Fact]
    public void A_null_message_renders_as_empty_rather_than_throwing()
    {
        var localizer = new Localizer(NullLogger<Localizer>.Instance, Resources);

        Assert.Equal(string.Empty, localizer.Format(null));
    }

    [Fact]
    public void Too_few_arguments_degrade_to_a_diagnosable_string()
    {
        // A mistranslation that references {2} when the sender supplies one argument must not
        // take the window down. It should show something a support call can act on.
        var localizer = new Localizer(NullLogger<Localizer>.Instance, Resources);

        var rendered = localizer.Format(
            new LocalizableText(MessageKeys.Status_MappingDegraded, ["only-one"]));

        Assert.Contains(MessageKeys.Status_MappingDegraded, rendered);
        Assert.Contains("only-one", rendered);
    }

    [Fact]
    public void Message_arguments_are_rendered_with_the_invariant_culture()
    {
        // The service formats these in Session 0 under the machine account's culture while the
        // client displays them under the operator's. Rendering a number with the sender's
        // separators would mix conventions inside one sentence.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var message = LocalizableText.Of("Some_Key", 1234.5);

            Assert.Equal("1234.5", message.Arguments[0]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Diagnostic_rendering_keeps_the_key_and_its_arguments()
    {
        // What goes into logs and the diagnostics export: stable across languages, so a report
        // exported in one language is readable by support working in another.
        var message = LocalizableText.Of(MessageKeys.Status_MappingsUnresolvable, 2);

        Assert.Equal("Status_MappingsUnresolvable(2)", message.ToDiagnosticString());
    }

    [Fact]
    public void Module_messages_convert_to_pipe_messages_without_losing_anything()
    {
        // The EDID module keeps its own message type so it can ship without TouchMappingAgent.
        // That is only safe while the conversion is lossless.
        var moduleMessage = EdidMessage.Of(EdidMessageKeys.TemplateApplied, "DISPLAY\\CHR8910\\1", 880);

        var pipeMessage = new LocalizableText(moduleMessage.Key, moduleMessage.Arguments);

        Assert.Equal(moduleMessage.Key, pipeMessage.Key);
        Assert.Equal(moduleMessage.Arguments, pipeMessage.Arguments);
        Assert.Equal(moduleMessage.ToDiagnosticString(), pipeMessage.ToDiagnosticString());
    }
}
