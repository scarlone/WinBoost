namespace WinBoost.Core;

/// <summary>Rettangolo in unita' indipendenti dal dispositivo (DIP).</summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Collocazione della finestra dentro l'area di lavoro dello schermo.
///
/// Serve perche' il centraggio automatico di WPF divide per due la differenza fra
/// area disponibile e dimensione richiesta: se la finestra e' piu' alta dello schermo
/// la differenza e' negativa e il bordo superiore finisce fuori. Su 1920x1080 al 150%
/// l'area utile e' 1280x672 DIP, e una finestra alta 820 DIP nasce con Top = -74,
/// cioe' con la barra del titolo irraggiungibile.
/// </summary>
public static class WindowPlacement
{
    /// <summary>Spazio lasciato fra la finestra e i bordi dell'area di lavoro.</summary>
    public const double DefaultMargin = 16;

    /// <summary>
    /// Riduce la dimensione richiesta a quella disponibile e centra il risultato.
    /// Il rettangolo restituito e' sempre interamente dentro l'area di lavoro.
    /// </summary>
    /// <param name="minWidth">Larghezza sotto la quale non scendere, salvo che lo schermo sia ancora piu' stretto.</param>
    /// <param name="minHeight">Altezza sotto la quale non scendere, salvo che lo schermo sia ancora piu' basso.</param>
    public static ScreenRect FitToWorkArea(
        double desiredWidth,
        double desiredHeight,
        ScreenRect workArea,
        double minWidth = 0,
        double minHeight = 0,
        double margin = DefaultMargin)
    {
        // Su un'area minuscola il margine stesso diventa il problema: si restringe.
        var usableWidth = Math.Max(0, workArea.Width - margin);
        var usableHeight = Math.Max(0, workArea.Height - margin);

        var width = Math.Min(desiredWidth, usableWidth);
        var height = Math.Min(desiredHeight, usableHeight);

        // I minimi valgono solo finche' lo schermo li consente: una finestra piu' grande
        // dello schermo e' esattamente il difetto che stiamo evitando.
        width = Math.Min(Math.Max(width, Math.Min(minWidth, usableWidth)), usableWidth);
        height = Math.Min(Math.Max(height, Math.Min(minHeight, usableHeight)), usableHeight);

        var left = workArea.Left + (workArea.Width - width) / 2;
        var top = workArea.Top + (workArea.Height - height) / 2;

        return new ScreenRect(left, top, width, height);
    }
}
