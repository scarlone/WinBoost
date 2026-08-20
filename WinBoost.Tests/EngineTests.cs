using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

/// <summary>Motore che scrive sessioni in una cartella temporanea usa-e-getta.</summary>
public sealed class EngineFixture : IDisposable
{
    public EngineFixture(Catalog? catalog = null)
    {
        SessionDir = Path.Combine(Path.GetTempPath(), "WinBoost.Tests", Guid.NewGuid().ToString("N"));
        Engine = new TweakEngine(catalog ?? TestData.LoadCatalog(), new HardwareProbe(), new SessionStore(SessionDir))
        {
            AutoRestartShell = false   // un test non deve chiudere Explorer all'utente
        };
    }

    public string SessionDir { get; }
    public TweakEngine Engine { get; }

    public void Dispose()
    {
        if (Directory.Exists(SessionDir)) Directory.Delete(SessionDir, recursive: true);
    }
}

public class ApplicabilityTests
{
    [Fact]
    public void UnTweakVincolatoAUnVendoreAssenteNonEApplicabile()
    {
        using var fx = new EngineFixture();
        var probe = fx.Engine.Probe;

        foreach (var t in fx.Engine.Catalog.Tweaks.Where(t => !string.IsNullOrWhiteSpace(t.Vendor)))
        {
            var atteso = probe.HasVendor(t.Vendor!);
            Assert.Equal(atteso, fx.Engine.CheckHardware(t).CanApply);
        }
    }

    [Fact]
    public void LAnteprimaNonApplicaIlVincoloDiElevazione()
    {
        // L'anteprima serve a decidere SE elevare: nascondere cio' che richiede
        // privilegi la renderebbe circolare.
        using var fx = new EngineFixture();

        var tweak = fx.Engine.Catalog.Tweaks.First(t => t.Admin && string.IsNullOrWhiteSpace(t.Vendor)
                                                     && string.IsNullOrWhiteSpace(t.Condition)
                                                     && t.Ops.All(o => o.Type == "reg"));

        var righe = fx.Engine.Preview(new[] { tweak });

        Assert.NotEmpty(righe);
        Assert.DoesNotContain(righe, r => r.Kind == "skip");
    }

    [Fact]
    public void SenzaElevazioneLAnteprimaSegnalaCheServonoIPrivilegi()
    {
        if (HardwareProbe.IsElevated()) return;   // il test ha senso solo da utente standard

        using var fx = new EngineFixture();
        var tweak = fx.Engine.Catalog.Tweaks.First(t => t.Admin && string.IsNullOrWhiteSpace(t.Vendor)
                                                     && t.Ops.All(o => o.Type == "reg"));

        Assert.All(fx.Engine.Preview(new[] { tweak }),
            r => Assert.Contains("amministratore", r.Warning ?? "", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IPresetSelezionanoSoloTweakAttiviPerDefault()
    {
        using var fx = new EngineFixture();

        foreach (var preset in fx.Engine.Catalog.Presets)
            Assert.All(fx.Engine.ResolvePreset(preset), t => Assert.True(t.IsDefaultOn));
    }

    [Fact]
    public void IlPresetSicuroContieneSoloRischioBasso()
    {
        using var fx = new EngineFixture();
        var preset = fx.Engine.Catalog.Presets.First(p => p.Id == "safe");

        Assert.All(fx.Engine.ResolvePreset(preset), t => Assert.Equal(RiskLevel.Low, t.Risk));
    }

    [Fact]
    public void IlPresetCompetitivoEscludeIlRischioAlto()
    {
        using var fx = new EngineFixture();
        var preset = fx.Engine.Catalog.Presets.First(p => p.Id == "competitive");

        Assert.All(fx.Engine.ResolvePreset(preset), t => Assert.NotEqual(RiskLevel.High, t.Risk));
    }
}

public class ParameterTests
{
    [Fact]
    public void IlParametroDnsEUnaSceltaConCinqueOpzioni()
    {
        var tweak = TestData.LoadCatalog().FindTweak("network.dns-custom")!;
        var def = Assert.Single(ParameterParser.Parse(tweak));

        Assert.True(def.IsChoice);
        Assert.Equal("keep", def.DefaultChoice);
        Assert.Equal(5, def.Options.Count);
        Assert.Contains(def.Options, o => o.Key == "cloudflare" && o.Label == "Cloudflare");
    }

    [Fact]
    public void IlParametroAppxUnisceDefaultEDisponibili()
    {
        var tweak = TestData.LoadCatalog().FindTweak("debloat.remove-appx")!;
        var def = Assert.Single(ParameterParser.Parse(tweak));

        Assert.True(def.IsMultiSelect);
        Assert.True(def.Options.Count > def.DefaultList.Count);
        Assert.All(def.DefaultList, d => Assert.Contains(def.Options, o => o.Key == d));

        // I pacchetti Xbox restano disponibili ma non preselezionati:
        // XboxIdentityProvider serve al login di molti giochi Game Pass.
        Assert.Contains(def.Options, o => o.Key.Contains("Xbox", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(def.DefaultList, d => d.Contains("Xbox", StringComparison.OrdinalIgnoreCase));
    }

    private static string ProposedFor(TweakEngine engine, Tweak tweak, string kind) =>
        engine.Preview(new[] { tweak }).Where(c => c.Kind == kind).Select(c => c.Proposed).FirstOrDefault() ?? "";

    [Fact]
    public void IlDnsHaTreStatiDistinti()
    {
        using var fx = new EngineFixture();
        var tweak = fx.Engine.Catalog.FindTweak("network.dns-custom")!;

        if (fx.Engine.Probe.ActiveAdapters.Count == 0) return;   // nessuna scheda su cui ragionare

        // keep = non toccare
        Assert.Contains("keep", ProposedFor(fx.Engine, tweak, "dns"), StringComparison.OrdinalIgnoreCase);

        // provider esplicito = server statici
        fx.Engine.Overrides.SetChoice("network.dns-custom", "provider", "quad9");
        Assert.Contains("9.9.9.9", ProposedFor(fx.Engine, tweak, "dns"));

        // lista vuota = ritorno a DHCP, che e' una modifica, non un "non toccare"
        fx.Engine.Overrides.SetChoice("network.dns-custom", "provider", "dhcp");
        var dhcp = ProposedFor(fx.Engine, tweak, "dns");
        Assert.Contains("DHCP", dhcp, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("keep", dhcp, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LaSceltaDellUtentePrevaleSuiPacchettiPredefiniti()
    {
        using var fx = new EngineFixture();
        var tweak = fx.Engine.Catalog.FindTweak("debloat.remove-appx")!;

        fx.Engine.Overrides.SetList("debloat.remove-appx", "packages", new[] { "Microsoft.BingNews" });

        var proposto = ProposedFor(fx.Engine, tweak, "appx-remove");
        Assert.Equal("Microsoft.BingNews", proposto);
    }

    [Fact]
    public void UnaListaVuotaNonPropoveNessunPacchetto()
    {
        using var fx = new EngineFixture();
        var tweak = fx.Engine.Catalog.FindTweak("debloat.remove-appx")!;

        fx.Engine.Overrides.SetList("debloat.remove-appx", "packages", Array.Empty<string>());

        Assert.Contains("nessun pacchetto", ProposedFor(fx.Engine, tweak, "appx-remove"),
            StringComparison.OrdinalIgnoreCase);
    }
}

public class SessionTests
{
    [Fact]
    public void UnaSessioneSopravviveAlSalvataggioECaricamento()
    {
        using var fx = new EngineFixture();
        var store = new SessionStore(fx.SessionDir);

        var session = Session.New("gaming");
        session.Entries.Add(new SessionEntry
        {
            TweakId = "t", TweakName = "Test", Kind = "reg",
            Target = @"HKCU:\Software\X", ValueName = "V", RevertMode = "snapshot",
            RegistryBefore = new RegistryState { KeyExists = true, ValueExists = true, Kind = "DWord", Value = "5" }
        });
        store.Save(session);

        var caricata = store.Load(session.Id)!;

        Assert.Equal(session.Id, caricata.Id);
        Assert.Equal("gaming", caricata.PresetId);
        var entry = Assert.Single(caricata.Entries);
        Assert.Equal("5", entry.RegistryBefore!.Value);
        Assert.True(entry.CanRollback);
    }

    [Fact]
    public void LeSessioniSonoElencateDallaPiuRecente()
    {
        using var fx = new EngineFixture();
        var store = new SessionStore(fx.SessionDir);

        var vecchia = Session.New(null);
        vecchia.StartedAt = DateTimeOffset.Now.AddHours(-2);
        store.Save(vecchia);

        var recente = Session.New(null);
        recente.Id = vecchia.Id + "-b";
        recente.StartedAt = DateTimeOffset.Now;
        store.Save(recente);

        Assert.Equal(recente.Id, store.LoadAll().First().Id);
    }

    [Fact]
    public void UnJournalIllegibileVieneIgnoratoSenzaEsplodere()
    {
        using var fx = new EngineFixture();
        Directory.CreateDirectory(fx.SessionDir);
        File.WriteAllText(Path.Combine(fx.SessionDir, "rotto.json"), "{ questo non e' json");

        var store = new SessionStore(fx.SessionDir);

        Assert.Empty(store.LoadAll());               // ignorato
        Assert.True(File.Exists(Path.Combine(fx.SessionDir, "rotto.json")));   // ma non cancellato
    }

    [Fact]
    public void UnOperazioneIrreversibileNonSiDichiaraAnnullabile()
    {
        var entry = new SessionEntry { Status = EntryStatus.Applied, RevertMode = "none" };
        Assert.False(entry.CanRollback);
    }
}

public class RollbackTests
{
    [Fact]
    public void IlRollbackRipristinaEMarcaLeVoci()
    {
        using var fx = new EngineFixture();
        using var scratch = new ScratchKey();

        RegistryHelper.Write(scratch.Path, "Valore", "DWord", 1);

        var catalog = CatalogLoader.Parse($$"""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.scratch", "category": "x", "name": "Scratch", "risk": "low",
              "ops": [ { "type": "reg", "path": "{{scratch.Path.Replace("\\", "\\\\")}}",
                         "name": "Valore", "valueType": "DWord", "value": 42, "revert": "snapshot" } ] }
          ]
        }
        """);

        using var engineFx = new EngineFixture(catalog);
        var session = engineFx.Engine.Apply(catalog.Tweaks);

        Assert.Equal("42", RegistryHelper.Capture(scratch.Path, "Valore").Value);

        engineFx.Engine.Rollback(session);

        Assert.Equal("1", RegistryHelper.Capture(scratch.Path, "Valore").Value);
        Assert.All(session.Entries, e => Assert.Equal(EntryStatus.RolledBack, e.Status));
        Assert.True(session.IsRolledBack);
    }

    [Fact]
    public void IlRollbackCancellaCioCheNonEsistevaPrima()
    {
        using var scratch = new ScratchKey();

        var catalog = CatalogLoader.Parse($$"""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.nuovo", "category": "x", "name": "Nuovo", "risk": "low",
              "ops": [ { "type": "reg", "path": "{{scratch.Path.Replace("\\", "\\\\")}}",
                         "name": "Inedito", "valueType": "DWord", "value": 7, "revert": "snapshot" } ] }
          ]
        }
        """);

        using var fx = new EngineFixture(catalog);
        var session = fx.Engine.Apply(catalog.Tweaks);
        Assert.True(RegistryHelper.Capture(scratch.Path, "Inedito").ValueExists);

        fx.Engine.Rollback(session);
        Assert.False(RegistryHelper.Capture(scratch.Path, "Inedito").ValueExists);
    }

    [Fact]
    public void IlJournalEScrittoAncheSeLaSessioneVieneInterrotta()
    {
        using var scratch = new ScratchKey();

        var catalog = CatalogLoader.Parse($$"""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.a", "category": "x", "name": "A", "risk": "low",
              "ops": [ { "type": "reg", "path": "{{scratch.Path.Replace("\\", "\\\\")}}",
                         "name": "A", "valueType": "DWord", "value": 1, "revert": "snapshot" } ] }
          ]
        }
        """);

        using var fx = new EngineFixture(catalog);
        var session = fx.Engine.Apply(catalog.Tweaks);

        // Il file deve esistere su disco senza attendere la fine della sessione.
        var store = new SessionStore(fx.SessionDir);
        Assert.NotNull(store.Load(session.Id));
    }
}

public class NvidiaProfileTests
{
    private static NvidiaProfile Competitive() =>
        NvidiaProfileCatalog.Parse(File.ReadAllText(TestData.NvidiaProfiles), TestData.NvidiaProfiles)
            .Find("competitive")!;

    [Fact]
    public void IlProfiloCompetitivoHaLeImpostazioniAttese()
    {
        var profile = Competitive();

        Assert.Equal("Base Profile", profile.ProfileName);
        Assert.Equal(71, profile.Settings.Count);
        Assert.All(profile.Settings, s => Assert.Contains(s.Type, new[] { "Dword", "String" }));
    }

    [Fact]
    public void IlFileGeneratoHaIntestazioneEDichiarazioneDellOriginale()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nip-{Guid.NewGuid():N}.nip");
        try
        {
            NipWriter.Write(Competitive(), path);
            var bytes = File.ReadAllBytes(path);

            // UTF-8 con BOM, ma la dichiarazione annuncia utf-16: e' l'artefatto
            // verificato che Profile Inspector accetta, non un errore.
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());

            var testo = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n<ArrayOfProfile>", testo);
            Assert.EndsWith("</ArrayOfProfile>", testo);
            Assert.Contains("<Executeables />", testo);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScritturaELetturaDelNipSonoSimmetriche()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nip-{Guid.NewGuid():N}.nip");
        try
        {
            var originale = Competitive();
            NipWriter.Write(originale, path);
            var riletto = NipWriter.Read(path);

            Assert.Equal(originale.ProfileName, riletto.ProfileName);
            Assert.Equal(originale.Settings.Count, riletto.Settings.Count);

            foreach (var (a, b) in originale.Settings.Zip(riletto.Settings))
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Value, b.Value);
                Assert.Equal(a.Type, b.Type);
                Assert.Equal(a.Name, b.Name);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IValoriOltreIntMaxValueRestanoIntatti()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nip-{Guid.NewGuid():N}.nip");
        try
        {
            NipWriter.Write(Competitive(), path);
            var riletto = NipWriter.Read(path);

            // Vertical Sync Tear Control e altri superano int.MaxValue: se venissero
            // trattati come int diventerebbero negativi e scriverebbero il valore sbagliato.
            var grandi = riletto.Settings.Where(s => ulong.TryParse(s.Value, out var v) && v > int.MaxValue).ToList();
            Assert.NotEmpty(grandi);
            Assert.Contains(grandi, s => s.Value == "2525368439");
            Assert.Contains(grandi, s => s.Value == "4294967295");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnProfiloConTipoNonValidoVieneRifiutato()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "profiles": [
            { "id": "x", "profileName": "Base Profile",
              "settings": [ { "id": 1, "name": "n", "type": "Float", "value": "1" } ] }
          ]
        }
        """;

        var ex = Assert.Throws<InvalidDataException>(() => NvidiaProfileCatalog.Parse(json));
        Assert.Contains("Float", ex.Message);
    }

    [Fact]
    public void SenzaProfileInspectorLOperazioneVieneSaltataSenzaFallire()
    {
        var catalog = CatalogLoader.Parse("""
        {
          "categories": [ { "id": "gpu", "name": "GPU" } ],
          "tweaks": [
            { "id": "t.nvidia", "category": "gpu", "name": "Profilo", "risk": "medium",
              "ops": [ { "type": "nvapi-profile", "profile": "competitive" } ] }
          ]
        }
        """);

        using var fx = new EngineFixture(catalog);
        fx.Engine.Inspector = new ProfileInspector(@"C:\percorso\inesistente.exe");

        var entry = Assert.Single(fx.Engine.Apply(catalog.Tweaks).Entries);

        Assert.Equal(EntryStatus.Skipped, entry.Status);
        Assert.Equal("none", entry.RevertMode);
        Assert.False(entry.CanRollback);
    }
}
