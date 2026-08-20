using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WinBoost.Core;

namespace WinBoost.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        EnableDarkTitleBar(handle);
        FitToScreen(handle);
    }

    // ------------------------------------------------------------------
    // Collocazione sullo schermo
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private const int MonitorDefaultToNearest = 2;

    /// <summary>
    /// Riduce e ricentra la finestra dentro l'area di lavoro del monitor su cui nasce.
    ///
    /// Il centraggio di WPF non controlla che la finestra ci stia: su uno schermo
    /// 1920x1080 al 150% l'area utile e' 1280x672 DIP, e una finestra alta 820 DIP
    /// nasceva con il bordo superiore a -74, cioe' con la barra del titolo fuori dallo
    /// schermo e irraggiungibile.
    /// </summary>
    private void FitToScreen(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        // GetMonitorInfo lavora in pixel fisici; WPF posiziona in DIP.
        var dpi = VisualTreeHelper.GetDpi(this);
        if (dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0) return;

        var workArea = new ScreenRect(
            info.Work.Left / dpi.DpiScaleX,
            info.Work.Top / dpi.DpiScaleY,
            (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
            (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);

        var placed = WindowPlacement.FitToWorkArea(Width, Height, workArea, MinWidth, MinHeight);

        // I minimi vanno abbassati prima di restringere, altrimenti WPF li fa vincere
        // e la finestra resta piu' grande dello schermo.
        MinWidth = Math.Min(MinWidth, placed.Width);
        MinHeight = Math.Min(MinHeight, placed.Height);

        Width = placed.Width;
        Height = placed.Height;
        Left = placed.Left;
        Top = placed.Top;
    }

    // ------------------------------------------------------------------
    // Barra del titolo scura
    // ------------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19;   // build di Windows 10 precedenti alla 20H1

    /// <summary>
    /// La barra del titolo e' chrome di sistema: senza questa chiamata resta chiara
    /// sopra una finestra scura.
    /// </summary>
    private static void EnableDarkTitleBar(IntPtr handle)
    {
        var enabled = 1;
        if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
    }

    // ------------------------------------------------------------------
    // Log
    // ------------------------------------------------------------------

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Il log deve seguire l'ultima riga senza che l'utente debba scorrere.
        if (e.OldValue is MainViewModel old)
            old.LogLines.CollectionChanged -= OnLogChanged;

        if (e.NewValue is MainViewModel vm)
            vm.LogLines.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScroll.ScrollToEnd();
    }
}
