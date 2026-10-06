using System.ComponentModel;

namespace TouchMappingAgent.WPF.Localization;

/// <summary>
/// The single binding target every localised XAML string points at.
///
/// WHY AN INDEXER AND NOT A STATIC LOOKUP: a static lookup is resolved once, when the window is
/// parsed. Switching language would then only affect windows opened afterwards, leaving the tray
/// menu and any already-open window in the previous language — which on a commissioning station
/// means the operator sees two languages at once. Binding through an indexer on a single
/// INotifyPropertyChanged instance lets one PropertyChanged notification with an empty property
/// name re-evaluate every localised binding in the process at once.
/// </summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationSource> Lazy = new(() => new LocalizationSource());

    private ILocalizer? _localizer;

    private LocalizationSource()
    {
    }

    /// <summary>
    /// The process-wide instance XAML binds to. A singleton because XAML markup cannot reach the
    /// DI container; <see cref="Attach"/> injects the real localiser at start-up.
    /// </summary>
    public static LocalizationSource Instance => Lazy.Value;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Looks up a key. Before <see cref="Attach"/> runs — and in the XAML designer, which
    /// instantiates controls without the application's start-up path — this returns the key in
    /// brackets rather than throwing, so a designer surface still renders.
    /// </summary>
    public string this[string key] => _localizer?[key] ?? $"[{key}]";

    /// <summary>
    /// Connects the source to the DI-resolved localiser and subscribes to language changes.
    /// Called once, from application start-up.
    /// </summary>
    public void Attach(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);

        if (_localizer != null)
            _localizer.CultureChanged -= OnCultureChanged;

        _localizer = localizer;
        _localizer.CultureChanged += OnCultureChanged;

        Refresh();
    }

    private void OnCultureChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Tells WPF that every indexed value may have changed. The empty property name is the
    /// documented way to invalidate all bindings on a source at once.
    /// </summary>
    private void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
