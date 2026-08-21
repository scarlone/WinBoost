using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

/// <summary>
/// Il catalogo dichiara quattro modalita' di revert - snapshot, none, cmd,
/// delete-key - ma i test ne esercitavano una sola. Qui si coprono le altre,
/// a partire da delete-key, che e' l'unico percorso di rollback che cancella
/// un albero di registro invece di riscrivere un valore.
/// </summary>
public class RollbackModesTests
{
    private static Catalog CatalogoConOp(string opJson) => CatalogLoader.Parse($$"""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.prova", "category": "x", "name": "Prova", "risk": "low",
              "ops": [ {{opJson}} ] }
          ]
        }
        """);

    private static string Esc(string path) => path.Replace("\\", "\\\\");

    /// <summary>
    /// Il caso reale di explorer.classic-context-menu: il tweak crea una chiave
    /// che prima non c'era, e il rollback la toglie di mezzo interamente perche'
    /// non c'e' nessun "valore precedente" a cui tornare.
    /// </summary>
    [Fact]
    public void DeleteKeyRimuoveLAlberoCreatoDalTweak()
    {
        using var scratch = new ScratchKey();
        var sotto = scratch.Path + @"\InprocServer32";

        var catalog = CatalogoConOp($$"""
            { "type": "reg", "path": "{{Esc(sotto)}}", "name": "(default)",
              "valueType": "String", "value": "",
              "revert": "delete-key", "revertKey": "{{Esc(scratch.Path)}}" }
            """);

        using var fx = new EngineFixture(catalog);

        Assert.False(RegistryHelper.KeyExists(scratch.Path));

        var session = fx.Engine.Apply(catalog.Tweaks);
        Assert.True(RegistryHelper.KeyExists(sotto));

        fx.Engine.Rollback(session);

        // Sparisce l'intero albero, non solo il valore scritto.
        Assert.False(RegistryHelper.KeyExists(scratch.Path));
        Assert.All(session.Entries, e => Assert.Equal(EntryStatus.RolledBack, e.Status));
    }

    /// <summary>
    /// Il lato tagliente, messo per iscritto invece che scoperto sul campo:
    /// delete-key cancella l'albero dichiarato in revertKey senza chiedersi se
    /// esistesse gia' prima. Se una chiave conteneva altro, quell'altro sparisce.
    ///
    /// Per il tweak che usa questa modalita' il caso non si presenta - la chiave
    /// CLSID del menu contestuale non esiste finche' non la si crea - ma chi
    /// aggiunge un nuovo tweak con revert "delete-key" deve sapere che il
    /// rollback e' una potatura, non un ripristino.
    /// </summary>
    [Fact]
    public void DeleteKeyCancellaAncheCioCheCeraPrima()
    {
        using var scratch = new ScratchKey();
        var preesistente = scratch.Path + @"\Preesistente";
        var sotto = scratch.Path + @"\InprocServer32";

        RegistryHelper.Write(preesistente, "NonMio", "String", "roba di qualcun altro");

        var catalog = CatalogoConOp($$"""
            { "type": "reg", "path": "{{Esc(sotto)}}", "name": "(default)",
              "valueType": "String", "value": "",
              "revert": "delete-key", "revertKey": "{{Esc(scratch.Path)}}" }
            """);

        using var fx = new EngineFixture(catalog);
        fx.Engine.Rollback(fx.Engine.Apply(catalog.Tweaks));

        Assert.False(RegistryHelper.KeyExists(preesistente));
    }

    /// <summary>
    /// Solo un tweak del catalogo usa delete-key. Se ne comparissero altri,
    /// meritano una lettura attenta prima di essere dati per buoni: vedi sopra.
    /// </summary>
    [Fact]
    public void SoloUnTweakDelCatalogoUsaDeleteKey()
    {
        var conDeleteKey = TestData.LoadCatalog().Tweaks
            .Where(t => t.Ops.Any(o => string.Equals(o.Revert, "delete-key", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Id)
            .ToList();

        Assert.Equal(new[] { "explorer.classic-context-menu" }, conDeleteKey);
    }

    /// <summary>
    /// Ogni operazione che dichiara revertKey deve dichiarare anche delete-key:
    /// un revertKey con revert "snapshot" sarebbe un campo che non fa nulla, cioe'
    /// una promessa di rollback diversa da quella che il motore esegue.
    /// </summary>
    [Fact]
    public void RevertKeyCompareSoloDoveServe()
    {
        foreach (var t in TestData.LoadCatalog().Tweaks)
            foreach (var o in t.Ops.Where(o => !string.IsNullOrWhiteSpace(o.RevertKey)))
                Assert.True(string.Equals(o.Revert, "delete-key", StringComparison.OrdinalIgnoreCase),
                    $"{t.Id}: revertKey dichiarato con revert '{o.Revert}'");
    }
}

/// <summary>
/// Il revert per comando: l'unica modalita' in cui annullare significa eseguire
/// qualcosa invece di riscrivere uno stato catturato.
/// </summary>
public class CommandRollbackTests
{
    /// <summary>
    /// Testimone di esecuzione: una cartella creata da "cmd.exe /c mkdir". Si usa
    /// mkdir e non una redirezione "> file" perche' gli argomenti passano per
    /// ArgumentList, che li cita uno per uno: l'operatore di redirezione
    /// arriverebbe a cmd come testo, non come redirezione, e il test misurerebbe
    /// il proprio difetto invece del motore.
    /// </summary>
    private sealed class Spia : IDisposable
    {
        public Spia() => Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "winboost-spia-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }
        public bool Scattata => Directory.Exists(Path);

        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }

    private static Catalog CatalogoCmd(string opJson) => CatalogLoader.Parse($$"""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.cmd", "category": "x", "name": "Cmd", "risk": "low",
              "ops": [ {{opJson}} ] }
          ]
        }
        """);

    [Fact]
    public void IlComandoInversoVieneEseguitoAlRollback()
    {
        using var spia = new Spia();
        var esc = spia.Path.Replace("\\", "\\\\");

        var catalog = CatalogoCmd($$"""
            { "type": "cmd", "exe": "cmd.exe", "args": ["/c", "exit", "0"],
              "revert": "cmd", "revertExe": "cmd.exe",
              "revertArgs": ["/c", "mkdir", "{{esc}}"] }
            """);

        using var fx = new EngineFixture(catalog);
        var session = fx.Engine.Apply(catalog.Tweaks);

        Assert.False(spia.Scattata);   // applicare non deve annullare

        fx.Engine.Rollback(session);

        Assert.True(spia.Scattata, "il comando inverso non e' stato eseguito");
    }

    /// <summary>
    /// Senza comando inverso il motore non deve fingere: la voce va marcata come
    /// non annullabile, cosi' la UI non offre un rollback che non avverrebbe.
    /// </summary>
    [Fact]
    public void SenzaComandoInversoLaVoceNonSiDichiaraAnnullabile()
    {
        var catalog = CatalogoCmd("""
            { "type": "cmd", "exe": "cmd.exe", "args": ["/c", "exit", "0"], "revert": "cmd" }
            """);

        using var fx = new EngineFixture(catalog);
        var session = fx.Engine.Apply(catalog.Tweaks);

        Assert.All(session.Entries, e => Assert.False(e.CanRollback));
    }

    /// <summary>
    /// Un comando che fallisce non deve lasciare la voce come annullabile: non c'e'
    /// nulla da annullare, e provarci significherebbe eseguire l'inverso di
    /// qualcosa che non e' avvenuto.
    /// </summary>
    [Fact]
    public void UnComandoFallitoNonLasciaUnaVoceAnnullabile()
    {
        using var spia = new Spia();
        var esc = spia.Path.Replace("\\", "\\\\");

        var catalog = CatalogoCmd($$"""
            { "type": "cmd", "exe": "cmd.exe", "args": ["/c", "exit", "1"],
              "revert": "cmd", "revertExe": "cmd.exe",
              "revertArgs": ["/c", "mkdir", "{{esc}}"] }
            """);

        using var fx = new EngineFixture(catalog);
        var session = fx.Engine.Apply(catalog.Tweaks);

        Assert.All(session.Entries, e => Assert.Equal(EntryStatus.Failed, e.Status));
        Assert.All(session.Entries, e => Assert.False(e.CanRollback));

        fx.Engine.Rollback(session);
        Assert.False(spia.Scattata);
    }
}
