using System.Diagnostics;
using WinBoost.Core;
using Xunit;
using Xunit.Abstractions;

namespace WinBoost.Tests;

/// <summary>
/// Verifica le promesse che il README fa sull'anteprima. Sono le affermazioni su
/// cui si regge la fiducia nel programma: che guardare non cambi niente, e che
/// guardare non costi quanto costava prima dell'ottimizzazione CIM.
/// </summary>
public class PreviewClaimsTests
{
    private readonly ITestOutputHelper _output;

    public PreviewClaimsTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// I bersagli di registro del catalogo, esclusi quelli con segnaposto: quelli
    /// si risolvono a runtime e non hanno un percorso fisso da fotografare.
    /// </summary>
    private static List<(string Path, string Name)> BersagliDiRegistro(Catalog catalog) =>
        catalog.Tweaks
            .SelectMany(t => t.Ops)
            .Where(o => o.Type == "reg"
                     && !string.IsNullOrWhiteSpace(o.Path)
                     && !o.Path!.Contains('{'))
            .Select(o => (o.Path!, o.Name ?? RegistryHelper.DefaultValueName))
            .Distinct()
            .ToList();

    private static string Fotografia(string path, string name)
    {
        try
        {
            var s = RegistryHelper.Capture(path, name);
            return $"{s.KeyExists}|{s.ValueExists}|{s.Kind}|{s.Value}";
        }
        catch (Exception e)
        {
            // Un percorso illeggibile e' comunque un valore stabile: se prima e dopo
            // fallisce allo stesso modo, non e' stato toccato.
            return "errore:" + e.GetType().Name;
        }
    }

    /// <summary>
    /// La promessa centrale: "anteprima non distruttiva". Si fotografa ogni valore
    /// di registro che il catalogo dichiara di voler toccare, si esegue l'anteprima
    /// dell'intero catalogo, si rifotografa. Un solo valore diverso e' una bugia.
    /// </summary>
    [Fact]
    public void LAnteprimaNonModificaNessunValoreDiRegistro()
    {
        using var fx = new EngineFixture();
        var bersagli = BersagliDiRegistro(fx.Engine.Catalog);

        Assert.NotEmpty(bersagli);   // se il filtro non trova nulla, il test non prova nulla

        var prima = bersagli.ToDictionary(b => $"{b.Path}!{b.Name}", b => Fotografia(b.Path, b.Name));

        fx.Engine.Preview(fx.Engine.Catalog.Tweaks);

        var dopo = bersagli.ToDictionary(b => $"{b.Path}!{b.Name}", b => Fotografia(b.Path, b.Name));

        var cambiati = prima.Where(kv => dopo[kv.Key] != kv.Value)
                            .Select(kv => $"{kv.Key}: '{kv.Value}' -> '{dopo[kv.Key]}'")
                            .ToList();

        Assert.True(cambiati.Count == 0,
            "L'anteprima ha modificato il registro:\n" + string.Join("\n", cambiati));

        _output.WriteLine($"{bersagli.Count} valori di registro invariati dopo l'anteprima completa.");
    }

    /// <summary>Un'anteprima non e' una sessione: non deve lasciare journal.</summary>
    [Fact]
    public void LAnteprimaNonScriveNessunJournal()
    {
        using var fx = new EngineFixture();

        fx.Engine.Preview(fx.Engine.Catalog.Tweaks);

        Assert.Empty(fx.Engine.Store.LoadAll());
        Assert.Empty(Directory.GetFiles(fx.SessionDir));
    }

    /// <summary>
    /// Il README documenta l'anteprima completa a 291 ms, dopo il passaggio dai
    /// cmdlet NetAdapter alle query CIM in cache; prima erano 17.392 ms.
    /// La soglia e' volutamente larga: non riproduce quel numero su hardware
    /// altrui, ma inchioda il ritorno a un processo per proprieta', che si
    /// misurava in decine di secondi.
    /// </summary>
    [Fact]
    public void LAnteprimaCompletaNonTornaACostareDecineDiSecondi()
    {
        using var fx = new EngineFixture();

        var cronometro = Stopwatch.StartNew();
        var righe = fx.Engine.Preview(fx.Engine.Catalog.Tweaks);
        cronometro.Stop();

        _output.WriteLine($"Anteprima di {fx.Engine.Catalog.Tweaks.Count} tweak "
                        + $"({righe.Count} righe) in {cronometro.ElapsedMilliseconds} ms.");

        Assert.True(cronometro.ElapsedMilliseconds < 5000,
            $"anteprima completa in {cronometro.ElapsedMilliseconds} ms: "
            + "la regressione riporta al costo dei cmdlet per proprieta'.");
    }

    /// <summary>
    /// Ogni tweak selezionabile deve produrre almeno una riga di anteprima: una
    /// riga muta significherebbe un tweak che l'utente puo' spuntare senza sapere
    /// cosa fara'.
    /// </summary>
    [Fact]
    public void OgniTweakApplicabileProduceAlmenoUnaRigaDiAnteprima()
    {
        using var fx = new EngineFixture();

        var muti = fx.Engine.Catalog.Tweaks
            .Where(t => fx.Engine.CheckHardware(t).CanApply)
            .Where(t => fx.Engine.Preview(new[] { t }).Count == 0)
            .Select(t => t.Id)
            .ToList();

        Assert.True(muti.Count == 0, "tweak applicabili senza anteprima: " + string.Join(", ", muti));
    }

    /// <summary>
    /// Ogni riga di anteprima deve dire il valore attuale e quello proposto: sono
    /// le due colonne su cui l'utente decide.
    /// </summary>
    [Fact]
    public void OgniRigaDiAnteprimaDichiaraAttualeEProposto()
    {
        using var fx = new EngineFixture();

        foreach (var riga in fx.Engine.Preview(fx.Engine.Catalog.Tweaks).Where(r => r.Kind != "skip"))
        {
            Assert.False(string.IsNullOrWhiteSpace(riga.Target), $"{riga.TweakId}: bersaglio vuoto");
            Assert.False(string.IsNullOrWhiteSpace(riga.Proposed), $"{riga.TweakId}: proposto vuoto");
            Assert.NotNull(riga.Current);
        }
    }
}

/// <summary>
/// Il README annuncia numeri ricavati dal catalogo. Sono la prima cosa che legge
/// chi arriva al progetto, e la prima a invecchiare: bastano due tweak aggiunti
/// perche' dichiari il falso senza che nessuno se ne accorga.
/// </summary>
public class ReadmeTests
{
    private static string Readme() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "README.md"));

    [Fact]
    public void IlConteggioDiTweakEOperazioniEQuelloReale()
    {
        var catalog = TestData.LoadCatalog();
        var tweak = catalog.Tweaks.Count;
        var ops = catalog.Tweaks.Sum(t => t.Ops.Count);

        Assert.Contains($"{tweak} tweak, {ops} operazioni", Readme());
    }

    /// <summary>
    /// La tabella delle prestazioni cita il numero di tweak dell'anteprima completa:
    /// se il catalogo cresce, quella misura si riferisce a un'altra cosa.
    /// </summary>
    [Fact]
    public void LaTabellaDellePrestazioniCitaIlCatalogoAttuale()
    {
        Assert.Contains($"anteprima completa ({TestData.LoadCatalog().Tweaks.Count} tweak)", Readme());
    }
}

/// <summary>
/// L'eseguibile dichiara asInvoker e chiede l'elevazione solo quando serve. E' una
/// proprieta' di sicurezza dichiarata nel README e nel manifest winget: se qualcuno
/// mettesse requireAdministrator, ogni avvio diventerebbe un prompt UAC e la
/// promessa "esplori senza privilegi" cadrebbe in silenzio.
/// </summary>
public class ManifestTests
{
    private static string Manifest() => File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "app.manifest"));

    /// <summary>
    /// Senza i commenti: il manifest spiega a parole di essere "deliberatamente
    /// asInvoker, non requireAdministrator", e cercare quel termine nel testo
    /// grezzo troverebbe la spiegazione invece della dichiarazione.
    /// </summary>
    private static string ManifestSenzaCommenti() =>
        System.Text.RegularExpressions.Regex.Replace(Manifest(), "<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void LEseguibileNonChiedeLElevazioneAllAvvio()
    {
        var manifest = ManifestSenzaCommenti();

        Assert.Contains("level=\"asInvoker\"", manifest);
        Assert.DoesNotContain("requireAdministrator", manifest);
        Assert.DoesNotContain("highestAvailable", manifest);
    }

    /// <summary>
    /// La versione nel manifest committato e' la sentinella sostituita a build time
    /// da $(Version): se qualcuno ci rimettesse un numero vero, tornerebbe a
    /// divergere in silenzio dalla versione reale.
    /// </summary>
    [Fact]
    public void LaVersioneNelManifestRestaUnaSentinella()
    {
        Assert.Contains("version=\"0.0.0.0\"", Manifest());
    }
}
