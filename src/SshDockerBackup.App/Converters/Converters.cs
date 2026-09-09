using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SshDockerBackup.Core.Reporting;

namespace SshDockerBackup.App.Converters;

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool flag && !flag;
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (parameter as string == "Invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Collapses an element when its bound string is empty. Used for status and error lines.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Green when the container is up, muted otherwise. Keeps the list scannable.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Running = new(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly SolidColorBrush Stopped = new(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly SolidColorBrush Problem = new(Color.FromRgb(0xC4, 0x2B, 0x1C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = (value as string ?? "").ToLowerInvariant();
        return state switch
        {
            "running" => Running,
            "restarting" or "paused" => Problem,
            _ => Stopped,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Binds a RadioButton to one value of an enum: IsChecked is true when the bound enum equals the
/// ConverterParameter, and checking it writes that value back.
/// </summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Unchecking a radio button fires too; only the newly checked one should write a value.
        if (value is not true || parameter is not string name) return Binding.DoNothing;

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return Enum.TryParse(enumType, name, out var parsed) ? parsed : Binding.DoNothing;
    }
}

/// <summary>Colours a report line by what it means, so the eye can triage before reading.</summary>
public sealed class ReportSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Good = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush Warning = new(Color.FromRgb(0xE0, 0x9B, 0x1A));
    private static readonly SolidColorBrush Action = new(Color.FromRgb(0x4C, 0xA0, 0xE8));
    private static readonly SolidColorBrush Problem = new(Color.FromRgb(0xE8, 0x4B, 0x3C));
    private static readonly SolidColorBrush Info = new(Color.FromRgb(0x99, 0x99, 0x99));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ReportSeverity.Good => Good,
            ReportSeverity.Warning => Warning,
            ReportSeverity.Action => Action,
            ReportSeverity.Problem => Problem,
            _ => Info,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ReportSeverityToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ReportSeverity.Good => "✓",     // check
            ReportSeverity.Warning => "!",  // exclamation
            ReportSeverity.Action => "→",   // arrow: something for you to do
            ReportSeverity.Problem => "✕",  // cross
            _ => "•",                        // bullet
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
