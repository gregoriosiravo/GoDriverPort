using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using LegionGoConverter.Core;

namespace LegionGoConverter.App.Views;

/// <summary>Visible when the bound <see cref="WizardStep"/> is one of the comma-separated names in the parameter.</summary>
public sealed class StepVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is WizardStep s && parameter is string list &&
        list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(s.ToString())
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible only when every value is <see cref="Visibility.Visible"/> (combine per-condition converters).</summary>
public sealed class AllVisibleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(v => v is Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

/// <summary>Collapsed for null, empty or whitespace strings (and null objects); parameter "invert" flips it.</summary>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool empty = value is null || (value is string s && string.IsNullOrWhiteSpace(s));
        if (parameter is string p && p == "invert") empty = !empty;
        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>bool → Visibility; parameter "invert" shows on false.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (parameter is string p && p == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when the value is null (used to disable the page behind an open prompt).</summary>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Full path → file name; empty stays empty.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? Path.GetFileName(s.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : s : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Pipeline stage → plain English title for the stage list.</summary>
public sealed class StageTitleConverter : IValueConverter
{
    public static string Title(Stage s) => s switch
    {
        Stage.Preflight => "Check this PC",
        Stage.ExtractLenovo => "Unpack the Lenovo driver",
        Stage.ExtractAmd => "Unpack the AMD driver",
        Stage.Analyze => "Match the Legion Go hardware",
        Stage.Build => "Build the converted driver",
        Stage.Sign => "Sign the driver package",
        Stage.Confirm => "Ask you to confirm",
        Stage.RestorePoint => "Create a restore point",
        Stage.StageLenovo => "Keep Lenovo's driver available",
        Stage.TrustCertificate => "Trust the signing certificate",
        Stage.InstallDriver => "Install the driver",
        Stage.VerifyDriver => "Check the driver is running",
        Stage.InstallAmdSoftware => "Install AMD Software",
        Stage.Export => "Export the converted package",
        _ => s.ToString(),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Stage s ? Title(s) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
