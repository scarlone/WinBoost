using System.IO;
using System.Reflection;
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

            var viewModel = new MainViewModel(engine, loaded);
            var window = new MainWindow { DataContext = viewModel };
            MainWindow = window;
            window.Show();

            // Dopo Show(): il controllo e' in sottofondo e non deve ritardare la
            // comparsa della finestra nemmeno del tempo di una risoluzione DNS.
            StartUpdateCheck(viewModel, e.Args);
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

    private static void StartUpdateCheck(MainViewModel viewModel, string[] args)
    {
        if (CatalogSource.UpdateCheckDisabled(args)) return;

        // AssemblyInformationalVersion porta la versione completa ('0.1.0+<sha>'),
        // non quella a quattro numeri di AssemblyVersion: e' l'unica confrontabile
        // con la tag di una release.
        var declared = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Una versione locale illeggibile rende il confronto senza senso: meglio
        // non dire niente che avvisare a caso.
        if (!SemVer.TryParse(declared, out var current)) return;

        viewModel.BeginUpdateCheck(current, new PreferencesStore());
    }
}
