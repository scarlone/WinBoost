using System.IO;
using System.Windows;
using WinBoost.Core;

namespace WinBoost.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var loaded = CatalogSource.Load(e.Args);
            var probe = new HardwareProbe();
            var store = new SessionStore();
            var engine = new TweakEngine(loaded.Catalog, probe, store)
            {
                NvidiaProfiles = CatalogSource.LoadNvidiaProfiles(),
                Inspector = new ProfileInspector(CatalogSource.ParseInspectorPath(e.Args))
            };

            var window = new MainWindow { DataContext = new MainViewModel(engine, loaded) };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            // Catalogo mancante, incoerente o argomento malformato: sono errori di
            // configurazione, non crash. Li dichiariamo e usciamo con codice diverso da zero.
            MessageBox.Show(ex.Message, "WinBoost - errore di avvio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
