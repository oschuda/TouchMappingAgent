using System.Windows.Markup;
// The project enables both WPF and WinForms (for the tray NotifyIcon), and both define Binding.
using Binding = System.Windows.Data.Binding;
using BindingMode = System.Windows.Data.BindingMode;

namespace TouchMappingAgent.WPF.Localization;

/// <summary>
/// XAML markup extension for localised text: <c>Text="{loc:Loc Status_NoMappingsStored}"</c>.
///
/// It returns a Binding rather than a finished string, so the text follows a language switch at
/// runtime instead of freezing at the value the window was parsed with. See
/// <see cref="LocalizationSource"/> for why that matters.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    /// <summary>Creates an empty extension; <see cref="Key"/> must then be set explicitly.</summary>
    public LocExtension()
    {
    }

    /// <summary>Creates an extension for the given key, as in <c>{loc:Loc Some_Key}</c>.</summary>
    public LocExtension(string key) => Key = key;

    /// <summary>The resource key to look up. Matches a name in Strings.resx.</summary>
    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay
        };

        return binding.ProvideValue(serviceProvider);
    }
}
