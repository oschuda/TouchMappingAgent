using System.Resources;
using System.Windows;

// German is the product's original language and the one the plant runs in, so it is compiled into
// the main assembly as the neutral culture rather than shipped as a satellite. Any language
// without its own satellite — and any machine where the satellites were not deployed — therefore
// falls back to readable German instead of to nothing.
[assembly: NeutralResourcesLanguage("de")]

// Theme and generic-resource lookup locations. WPF requires this on an assembly that ships XAML;
// without it, ResourceDictionary lookups for themed controls probe locations that do not exist.
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]
