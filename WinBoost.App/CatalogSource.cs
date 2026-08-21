using System.IO;
using System.Reflection;
using WinBoost.Core;

namespace WinBoost.App;

public sealed record LoadedCatalog(Catalog Catalog, string Origin, bool IsExternal);

/// <summary>
/// Determina da dove arriva il catalogo.
///
/// Il default e' la risorsa incorporata nell'eseguibile: un file JSON accanto
/// all'exe sarebbe modificabile da chiunque abbia accesso in scrittura alla
/// cartella, e questo processo scrive in HKLM con privilegi elevati. Sarebbe
/// una escalation locale gratuita.
///
/// L'override esterno resta possibile, ma solo esplicito e dichiarato nella UI.
/// </summary>
public static class CatalogSource
{
    private const string ResourceName = "WinBoost.tweaks.json";
    private const string NvidiaResourceName = "WinBoost.nvidia-profiles.json";
    private const string Flag = "--catalog";
    private const string InspectorFlag = "--profile-inspector";
    private const string NoUpdateCheckFlag = "--no-update-check";

    /// <summary>Profili NVIDIA verificati, incorporati come il catalogo principale.</summary>
    public static NvidiaProfileCatalog LoadNvidiaProfiles() =>
        NvidiaProfileCatalog.Parse(ReadResource(NvidiaResourceName), NvidiaResourceName);

    /// <summary>Percorso di nvidiaProfileInspector.exe indicato dall'utente, se presente.</summary>
    public static string? ParseInspectorPath(IReadOnlyList<string> args) => ParseFlag(args, InspectorFlag);

    /// <summary>
    /// Sopprime il controllo aggiornamenti per questa esecuzione. Vale per chi non
    /// vuole traffico di rete non richiesto ma non intende cambiare la preferenza
    /// salvata: utile in ambienti isolati e negli avvii automatici.
    /// </summary>
    public static bool UpdateCheckDisabled(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, NoUpdateCheckFlag, StringComparison.OrdinalIgnoreCase));

    public static LoadedCatalog Load(IReadOnlyList<string> args)
    {
        var external = ParseFlag(args, Flag);
        if (external is not null)
        {
            var full = Path.GetFullPath(external);
            if (!File.Exists(full))
                throw new FileNotFoundException($"Catalogo esterno non trovato: {full}");

            return new LoadedCatalog(CatalogLoader.Load(full), full, IsExternal: true);
        }

        return new LoadedCatalog(LoadEmbedded(), "catalogo incorporato", IsExternal: false);
    }

    private static string? ParseFlag(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) continue;

            if (i + 1 >= args.Count)
                throw new ArgumentException($"{flag} richiede un percorso di file.");

            return args[i + 1];
        }
        return null;
    }

    private static Catalog LoadEmbedded() => CatalogLoader.Parse(ReadResource(ResourceName), ResourceName);

    private static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException(
                $"Risorsa '{name}' assente dall'eseguibile. "
                + $"Risorse presenti: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
