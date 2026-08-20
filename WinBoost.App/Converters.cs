using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WinBoost.Core;

namespace WinBoost.App;

/// <summary>Colori di rischio in tre varianti: testo (default), sfondo tenue ("bg")
/// e bordo ("border"), per badge discreti invece di blocchi saturi.</summary>
public sealed class RiskToBrushConverter : IValueConverter
{
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Low = Frozen(0x6F, 0xCF, 0x8A);
    public static readonly SolidColorBrush Medium = Frozen(0xE0, 0xB3, 0x4D);
    public static readonly SolidColorBrush High = Frozen(0xF0, 0x7B, 0x76);

    private static readonly SolidColorBrush[] Backgrounds =
        { Frozen(0x14, 0x23, 0x19), Frozen(0x28, 0x20, 0x11), Frozen(0x2B, 0x16, 0x14) };

    private static readonly SolidColorBrush[] Borders =
        { Frozen(0x2A, 0x47, 0x32), Frozen(0x51, 0x40, 0x1D), Frozen(0x58, 0x2A, 0x26) };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is RiskLevel r
            ? (r switch { RiskLevel.Low => 0, RiskLevel.Medium => 1, _ => 2 })
            : 1;

        return (parameter as string) switch
        {
            "bg" => Backgrounds[index],
            "border" => Borders[index],
            _ => index switch { 0 => Low, 1 => Medium, _ => High }
        };
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visibile quando una collezione e' vuota (count pari a zero):
/// serve per gli stati vuoti di anteprima, cronologia e log.
/// Con parametro "invert" e' visibile quando la collezione ha elementi.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value is int count && count == 0;
        if (parameter as string == "invert") isZero = !isZero;
        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool flag && flag;
        if (parameter as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Riepilogo di una sessione per la lista Cronologia.</summary>
public sealed class SessionSummaryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Session s) return "";
        var state = s.IsRolledBack ? "ANNULLATA" : $"{s.AppliedCount} applicate";
        var failed = s.FailedCount > 0 ? $", {s.FailedCount} fallite" : "";
        return $"{s.StartedAt:dd/MM/yyyy HH:mm:ss}  —  {state}{failed}";
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
