using System.Globalization;
using System.IO;
using System.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TouchMappingAgent.Shared.Localization;

namespace TouchMappingAgent.WPF.Localization;

/// <summary>
/// The resource-backed <see cref="ILocalizer"/>.
///
/// LANGUAGE SELECTION, in order:
///   1. an explicit choice this user made, stored under HKCU;
///   2. otherwise the user's Windows display language (CurrentUICulture);
///   3. otherwise the neutral resources, which are German.
///
/// HKCU and not HKLM: the language is a property of the person standing at the terminal, not of
/// the machine. A commissioning technician switching to English must not change what the plant
/// operator sees at the same station afterwards.
/// </summary>
public sealed class Localizer : ILocalizer
{
    /// <summary>Where the per-user language choice is kept.</summary>
    internal const string SettingsKeyPath = @"Software\PadaLuma\TouchMappingAgent";

    /// <summary>Registry value name holding an IETF tag such as "de" or "en".</summary>
    internal const string LanguageValueName = "Language";

    private readonly ResourceManager _resources;
    private readonly ILogger<Localizer> _logger;
    private CultureInfo _culture;

    /// <summary>
    /// Creates a localizer, resolving the start-up language from the registry and the user's
    /// Windows settings.
    /// </summary>
    public Localizer(ILogger<Localizer> logger)
        : this(logger, Strings.ResourceManager)
    {
    }

    /// <summary>
    /// Test seam: accepts an arbitrary <see cref="ResourceManager"/> so a test can assert lookup
    /// and fallback behaviour without depending on the shipped resource files.
    /// </summary>
    internal Localizer(ILogger<Localizer> logger, ResourceManager resources)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _culture = ResolveStartupCulture();

        _logger.LogInformation(
            "UI language: {Culture} (Windows display language {SystemCulture})",
            _culture.Name.Length == 0 ? "(neutral)" : _culture.Name,
            CultureInfo.CurrentUICulture.Name);
    }

    /// <inheritdoc />
    public CultureInfo CurrentCulture => _culture;

    /// <inheritdoc />
    public IReadOnlyList<CultureInfo> AvailableCultures { get; } = DiscoverAvailableCultures();

    /// <inheritdoc />
    public event EventHandler? CultureChanged;

    /// <inheritdoc />
    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            try
            {
                var value = _resources.GetString(key, _culture);
                if (value != null)
                    return value;
            }
            catch (MissingManifestResourceException ex)
            {
                // The satellite assembly is missing or damaged. Log once per key rather than
                // throwing: a half-translated window is still usable, a crashed one is not.
                _logger.LogError(ex, "Resource lookup failed for {Key}", key);
            }

            _logger.LogWarning("No translation for {Key} in {Culture}", key, _culture.Name);
            return $"[{key}]";
        }
    }

    /// <inheritdoc />
    public string Format(LocalizableText? text)
    {
        if (text == null)
            return string.Empty;

        var format = this[text.Key];

        if (text.Arguments.Count == 0)
            return format;

        try
        {
            // The arguments arrived as strings from the service, so this substitutes positionally
            // and cannot re-format them under a culture they were not produced in.
            return string.Format(_culture, format, [.. text.Arguments]);
        }
        catch (FormatException ex)
        {
            // A translation whose placeholders do not match what the sender supplies — for
            // instance {0} {1} against a single argument. Show something diagnosable instead of
            // letting a bad translation take down the window.
            _logger.LogError(ex,
                "Translation for {Key} does not match its {ArgumentCount} argument(s)",
                text.Key, text.Arguments.Count);

            return $"[{text.ToDiagnosticString()}]";
        }
    }

    /// <inheritdoc />
    public void SetCulture(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        if (_culture.Name == culture.Name)
            return;

        _culture = culture;
        Persist(culture);

        _logger.LogInformation("UI language switched to {Culture}", culture.Name);
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private CultureInfo ResolveStartupCulture()
    {
        var stored = ReadStoredLanguage();
        if (stored != null)
            return stored;

        // No explicit choice: follow Windows. GetString falls back through the culture chain on
        // its own, so a culture we do not ship (fr-FR, say) lands on the neutral resources rather
        // than failing.
        return CultureInfo.CurrentUICulture;
    }

    private CultureInfo? ReadStoredLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            if (key?.GetValue(LanguageValueName) is not string tag || tag.Length == 0)
                return null;

            return CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException ex)
        {
            _logger.LogWarning(ex, "Stored UI language is not a known culture; using the system default");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _logger.LogWarning(ex, "Could not read the stored UI language; using the system default");
            return null;
        }
    }

    private void Persist(CultureInfo culture)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
            key?.SetValue(LanguageValueName, culture.Name, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The language still changed for this session; only remembering it failed.
            _logger.LogWarning(ex, "Could not persist the UI language choice");
        }
    }

    /// <summary>
    /// Finds which languages this build ships by looking for satellite assemblies beside the
    /// executable. German is always present because it is the neutral culture compiled into the
    /// main assembly, so it never appears as a satellite directory.
    /// </summary>
    private static IReadOnlyList<CultureInfo> DiscoverAvailableCultures()
    {
        var cultures = new List<CultureInfo> { CultureInfo.GetCultureInfo("de") };

        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            var satelliteName = $"{typeof(Localizer).Assembly.GetName().Name}.resources.dll";

            foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
            {
                if (!File.Exists(Path.Combine(directory, satelliteName)))
                    continue;

                try
                {
                    cultures.Add(CultureInfo.GetCultureInfo(Path.GetFileName(directory)));
                }
                catch (CultureNotFoundException)
                {
                    // A directory that merely looks like a culture folder. Ignore it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall back to German only. A language picker showing one entry is a smaller failure
            // than a client that will not start.
        }

        return cultures;
    }
}
