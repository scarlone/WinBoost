using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

/// <summary>Percorsi dei file dati copiati accanto agli assembly di test.</summary>
public static class TestData
{
    public static string Tweaks => Path.Combine(AppContext.BaseDirectory, "data", "tweaks.json");
    public static string NvidiaProfiles => Path.Combine(AppContext.BaseDirectory, "data", "nvidia-profiles.json");

    public static Catalog LoadCatalog() => CatalogLoader.Load(Tweaks);
}

public class CatalogTests
{
    /// <summary>
    /// Tipi che il catalogo puo' dichiarare senza che il motore li esegua davvero.
    /// Ogni voce qui e' un debito dichiarato, non una svista: il tweak corrispondente
    /// viene riportato come saltato con un messaggio esplicito.
    /// </summary>
    private static readonly HashSet<string> DocumentedGaps = new(StringComparer.OrdinalIgnoreCase)
    {
        "adlx-preset"   // richiede l'SDK AMD ADLX, non verificabile senza hardware Radeon
    };

    [Fact]
    public void CatalogoRealeSiCaricaEValida()
    {
        var catalog = TestData.LoadCatalog();

        Assert.NotEmpty(catalog.Tweaks);
        Assert.NotEmpty(catalog.Categories);
        Assert.NotEmpty(catalog.Presets);
    }

    [Fact]
    public void OgniTweakHaUnIdUnivoco()
    {
        var duplicati = TestData.LoadCatalog().Tweaks
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        Assert.Empty(duplicati);
    }

    [Fact]
    public void OgniOperazioneDelCatalogoEEseguibileDalMotore()
    {
        // Confronto contro la lista del motore, non contro una copia locale:
        // due elenchi separati divergerebbero al primo tipo nuovo.
        var sconosciuti = TestData.LoadCatalog().Tweaks
            .SelectMany(t => t.Ops.Select(o => new { t.Id, o.Type }))
            .Where(x => !TweakEngine.SupportedTypes.Contains(x.Type) && !DocumentedGaps.Contains(x.Type))
            .Select(x => $"{x.Id}: {x.Type}")
            .ToList();

        Assert.Empty(sconosciuti);
    }

    [Fact]
    public void ILimitiDichiaratiSonoAncoraTali()
    {
        // Se un tipo elencato come lacuna viene implementato, va tolto da DocumentedGaps:
        // altrimenti il test smette di sorvegliare qualcosa che invece funziona.
        var risolti = DocumentedGaps.Where(TweakEngine.SupportedTypes.Contains).ToList();

        Assert.Empty(risolti);
    }

    [Fact]
    public void OgniBersaglioDiRiavvioEGestito()
    {
        var sconosciuti = TestData.LoadCatalog().Tweaks
            .SelectMany(t => t.Restart.Select(r => new { t.Id, Target = r }))
            .Where(x => !ShellRestarter.IsKnownTarget(x.Target))
            .Select(x => $"{x.Id}: {x.Target}")
            .ToList();

        Assert.Empty(sconosciuti);
    }

    [Fact]
    public void OgniTweakAppartieneAUnaCategoriaDichiarata()
    {
        var catalog = TestData.LoadCatalog();
        var orfani = catalog.Tweaks.Where(t => catalog.FindCategory(t.Category) is null).Select(t => t.Id);

        Assert.Empty(orfani);
    }

    [Fact]
    public void LeOperazioniDiRegistroHannoTipoEValoreCoerenti()
    {
        var problemi = new List<string>();

        foreach (var t in TestData.LoadCatalog().Tweaks)
            foreach (var op in t.Ops.Where(o => o.Type is "reg" or "reg-template"))
            {
                if (op.Value is null) { problemi.Add($"{t.Id}: operazione senza value"); continue; }
                if (string.IsNullOrWhiteSpace(op.ValueType)) { problemi.Add($"{t.Id}: operazione senza valueType"); continue; }

                // La conversione deve riuscire: un DWORD scritto male esploderebbe solo in fase di apply.
                var ex = Record.Exception(() => RegistryHelper.Coerce(op.Value.Value, op.ValueType));
                if (ex is not null) problemi.Add($"{t.Id}: {op.Name} -> {ex.Message}");
            }

        Assert.Empty(problemi);
    }

    [Fact]
    public void IPercorsiDiRegistroUsanoHiveValidi()
    {
        var problemi = new List<string>();

        foreach (var t in TestData.LoadCatalog().Tweaks)
            foreach (var op in t.Ops.Where(o => o.Type is "reg" or "reg-template"))
            {
                var ex = Record.Exception(() =>
                {
                    var (root, _) = RegistryHelper.Split(op.Path!);
                    root.Dispose();
                });
                if (ex is not null) problemi.Add($"{t.Id}: {op.Path} -> {ex.Message}");
            }

        Assert.Empty(problemi);
    }

    [Fact]
    public void ITweakConSegnapostoDichiaranoUnEspansioneDinamica()
    {
        var problemi = TestData.LoadCatalog().Tweaks
            .Where(t => t.Ops.Any(o => o.Type == "reg-template"))
            .Where(t => string.IsNullOrWhiteSpace(t.Dynamic))
            .Select(t => t.Id);

        Assert.Empty(problemi);
    }

    [Fact]
    public void ITweakAdAltoRischioDichiaranoUnAvviso()
    {
        var senzaAvviso = TestData.LoadCatalog().Tweaks
            .Where(t => t.Risk == RiskLevel.High && string.IsNullOrWhiteSpace(t.Warning))
            .Select(t => t.Id);

        Assert.Empty(senzaAvviso);
    }

    [Fact]
    public void LeOperazioniIrreversibiliDichiaranoRevertNone()
    {
        // Se un'operazione non e' annullabile deve dirlo: il rollback promette solo cio' che mantiene.
        var irreversibili = new[] { "appx-remove", "uninstaller", "clear-dir", "process-kill", "winget", "winget-upgrade-all" };

        var bugiarde = TestData.LoadCatalog().Tweaks
            .SelectMany(t => t.Ops.Select(o => new { t.Id, Op = o }))
            .Where(x => irreversibili.Contains(x.Op.Type, StringComparer.OrdinalIgnoreCase))
            .Where(x => !string.Equals(x.Op.Revert, "none", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.Id}: {x.Op.Type} dichiara revert='{x.Op.Revert}'");

        Assert.Empty(bugiarde);
    }

    [Fact]
    public void UnCatalogoConCategoriaInesistenteVieneRifiutato()
    {
        var json = File.ReadAllText(TestData.Tweaks)
            .Replace("\"category\": \"privacy\"", "\"category\": \"categoria-inventata\"");

        var ex = Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(json));
        Assert.Contains("categoria-inventata", ex.Message);
    }

    [Fact]
    public void UnCatalogoConIdDuplicatoVieneRifiutato()
    {
        const string json = """
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "dup", "category": "x", "name": "A",
              "ops": [ { "type": "reg", "path": "HKCU:\\S", "name": "n", "valueType": "DWord", "value": 1 } ] },
            { "id": "dup", "category": "x", "name": "B",
              "ops": [ { "type": "reg", "path": "HKCU:\\S", "name": "n", "valueType": "DWord", "value": 1 } ] }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(json));
        Assert.Contains("duplicato", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnTweakSenzaOperazioniVieneRifiutato()
    {
        const string json = """
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [ { "id": "vuoto", "category": "x", "name": "Vuoto", "ops": [] } ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(json));
        Assert.Contains("nessuna operazione", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnOperazioneDiRegistroSenzaPathVieneRifiutata()
    {
        const string json = """
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "senza-path", "category": "x", "name": "A",
              "ops": [ { "type": "reg", "name": "n", "valueType": "DWord", "value": 1 } ] }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => CatalogLoader.Parse(json));
        Assert.Contains("path", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
