using System.Text.Json;

namespace WinBoost.Core;

/// <summary>
/// Le poche preferenze che devono sopravvivere alla chiusura. Vivono accanto ai
/// journal di sessione, sotto il profilo utente: non richiedono privilegi e non
/// finiscono nella cartella dell'eseguibile, che e' scrivibile da chiunque abbia
/// accesso a quella cartella.
/// </summary>
public sealed class UpdatePreferences
{
    /// <summary>
    /// Il controllo contatta api.github.com all'avvio, quindi espone l'indirizzo IP
    /// dell'utente a GitHub. E' poco, ma e' una connessione di rete che l'utente non
    /// ha chiesto: deve poterla spegnere, e la scelta deve restare.
    /// </summary>
    public bool CheckOnStartup { get; set; } = true;
}

public sealed class PreferencesStore
{
    private readonly string _path;

    public PreferencesStore(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinBoost");

        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public string FilePath => _path;

    /// <summary>
    /// Un file assente o illeggibile non e' un errore: significa "prima esecuzione",
    /// e i valori predefiniti sono la risposta giusta. Non lo cancelliamo, per lo
    /// stesso motivo per cui non cancelliamo un journal corrotto.
    /// </summary>
    public UpdatePreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return new UpdatePreferences();

            return JsonSerializer.Deserialize<UpdatePreferences>(
                File.ReadAllText(_path), CatalogLoader.Options) ?? new UpdatePreferences();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new UpdatePreferences();
        }
    }

    /// <summary>Il salvataggio e' best effort: non poter scrivere una preferenza
    /// non giustifica un errore in faccia a chi sta usando il programma.</summary>
    public void Save(UpdatePreferences preferences)
    {
        try
        {
            var json = JsonSerializer.Serialize(preferences, CatalogLoader.Options);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // ignorato di proposito
        }
    }
}
