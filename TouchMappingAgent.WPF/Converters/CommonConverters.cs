using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TouchMappingAgent.WPF.Localization;

namespace TouchMappingAgent.WPF.Converters;

/// <summary>
/// Inverts a boolean. Used to drive IsEnabled from an IsBusy flag, so every action button
/// disables itself while an operation runs — on a touch screen a double-triggered EDID write
/// is easy to do and unpleasant to undo.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && !b;
}

/// <summary>
/// Shows a wizard step only when it is the current one. The parameter is the step's enum name,
/// compared by name rather than by value so the XAML stays readable and a renamed step fails
/// visibly instead of silently matching the wrong page.
/// </summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Two-way binding between an enum property and a RadioButton, so a group of radio buttons can
/// drive one enum without a converter per option.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && parameter is string name &&
        string.Equals(value.ToString(), name, StringComparison.Ordinal);

    /// <inheritdoc/>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Only the button being CHECKED carries information; the unchecked one would otherwise
        // write a value back too and race the checked one.
        if (value is not true || parameter is not string name)
            return System.Windows.Data.Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return enumType.IsEnum && Enum.TryParse(enumType, name, out var parsed)
            ? parsed
            : System.Windows.Data.Binding.DoNothing;
    }
}

/// <summary>Renders a boolean as "yes"/"no" for the operator-facing summaries.</summary>
public sealed class BooleanToYesNoConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LocalizationSource.Instance[value is true ? LocalizationKeys.Common_Yes : LocalizationKeys.Common_No];

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders the elevation state in the wizard header.</summary>
public sealed class BooleanToElevationConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LocalizationSource.Instance[
            value is true ? LocalizationKeys.Common_Administrator : LocalizationKeys.Common_StandardUser];

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Renders a step outcome in the commissioning summary.</summary>
public sealed class BooleanToOkFailConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LocalizationSource.Instance[
            value is true ? LocalizationKeys.Common_StepOk : LocalizationKeys.Common_StepCheck];

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Collapses an element when the bound value is null or an empty string.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
