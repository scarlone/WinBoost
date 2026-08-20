using System.Text.Json;
using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

/// <summary>
/// Il motore tiene due elenchi paralleli dei tipi di operazione: quello che li descrive
/// (anteprima) e quello che li esegue. Se divergono, un tweak viene mostrato ma non
/// applicato, o viceversa — in silenzio. Questi test sorvegliano il confine.
///
/// Solo l'anteprima viene esercitata: e' l'unica meta' verificabile senza modificare
/// davvero il sistema.
/// </summary>
public class OpCoverageTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    /// <summary>Operazione minima ma plausibile per ciascun tipo supportato.</summary>
    private static (TweakOp Op, string? Dynamic) Minimal(string type) => type switch
    {
        "reg" => (new TweakOp
        {
            Type = "reg", Path = @"HKCU:\Software\WinBoost.Tests\Coverage",
            Name = "V", ValueType = "DWord", Value = Json("1")
        }, null),

        "reg-template" => (new TweakOp
        {
            Type = "reg-template",
            Path = @"HKLM:\SYSTEM\CurrentControlSet\Enum\{PNPDeviceID}\Device Parameters",
            Name = "V", ValueType = "DWord", Value = Json("1")
        }, "gpu-device-enum"),

        "service" => (new TweakOp { Type = "service", Name = "Spooler", Startup = "manual" }, null),

        "cmd" => (new TweakOp { Type = "cmd", Exe = "cmd.exe", Args = new List<string> { "/c", "rem" } }, null),

        "process-kill" => (new TweakOp { Type = "process-kill", Names = new List<string> { "processo-inesistente" } }, null),

        "clear-dir" => (new TweakOp { Type = "clear-dir", Path = @"%TEMP%\WinBoost.Tests.Coverage" }, null),

        "powerplan" => (new TweakOp
        {
            Type = "powerplan", Action = "duplicate-and-activate",
            SourceGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61"
        }, null),

        "dns" => (new TweakOp { Type = "dns" }, null),

        "netadapter-rsc" => (new TweakOp { Type = "netadapter-rsc", Enabled = false }, null),

        "netadapter-property" => (new TweakOp
        {
            Type = "netadapter-property", Pattern = "Large Send Offload*", Value = Json("\"Disabled\"")
        }, null),

        "appx-remove" => (new TweakOp { Type = "appx-remove" }, null),

        "uninstaller" => (new TweakOp
        {
            Type = "uninstaller", Candidates = new List<string> { @"C:\percorso\inesistente.exe" }
        }, null),

        "winget" => (new TweakOp { Type = "winget", Id = "Esempio.Pacchetto" }, null),

        "winget-upgrade-all" => (new TweakOp { Type = "winget-upgrade-all" }, null),

        "nvapi-profile" => (new TweakOp { Type = "nvapi-profile", Profile = "competitive" }, null),

        "store-update" => (new TweakOp { Type = "store-update" }, null),

        "windows-update" => (new TweakOp { Type = "windows-update" }, null),

        _ => throw new InvalidOperationException(
            $"Tipo '{type}' dichiarato supportato ma senza operazione di prova: aggiungerla qui.")
    };

    public static TheoryData<string> TipiSupportati()
    {
        var data = new TheoryData<string>();
        foreach (var t in TweakEngine.SupportedTypes.OrderBy(t => t)) data.Add(t);
        return data;
    }

    [Theory]
    [MemberData(nameof(TipiSupportati))]
    public void OgniTipoSupportatoEDescrittoDallAnteprima(string type)
    {
        var (op, dynamicKind) = Minimal(type);

        var tweak = new Tweak
        {
            Id = $"coverage.{type}",
            Category = "test",
            Name = $"Copertura {type}",
            Risk = RiskLevel.Low,
            Dynamic = dynamicKind,
            Ops = { op }
        };

        using var fx = new EngineFixture();
        fx.Engine.NvidiaProfiles = NvidiaProfileCatalog.Parse(File.ReadAllText(TestData.NvidiaProfiles));

        var righe = fx.Engine.Preview(new[] { tweak });

        // Un tipo che finisce nel ramo default dell'anteprima si tradisce cosi'.
        Assert.DoesNotContain(righe, r => r.Proposed.Contains("NON SUPPORTATO", StringComparison.OrdinalIgnoreCase));

        // Righe zero significherebbe che il tipo non produce alcun bersaglio: accettabile
        // solo per i tipi che dipendono da hardware assente su questa macchina.
        if (righe.Count == 0)
            Assert.Contains(type, new[] { "reg-template", "dns", "netadapter-rsc", "netadapter-property" });
    }

    [Fact]
    public void OgniTipoSupportatoHaUnOperazioneDiProva()
    {
        // Se qualcuno aggiunge un tipo al motore senza aggiornare questo file,
        // Minimal lancia: meglio un test rosso che una copertura che finge.
        foreach (var type in TweakEngine.SupportedTypes)
            Assert.Null(Record.Exception(() => Minimal(type)));
    }

    [Fact]
    public void UnTipoSconosciutoVieneSegnalatoComeNonSupportato()
    {
        var tweak = new Tweak
        {
            Id = "coverage.ignoto",
            Category = "test",
            Name = "Tipo ignoto",
            Ops = { new TweakOp { Type = "tipo-che-non-esiste" } }
        };

        using var fx = new EngineFixture();
        var riga = Assert.Single(fx.Engine.Preview(new[] { tweak }));

        Assert.Contains("NON SUPPORTATO", riga.Proposed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnTipoSconosciutoNonVieneApplicatoInSilenzio()
    {
        var catalog = CatalogLoader.Parse("""
        {
          "categories": [ { "id": "x", "name": "X" } ],
          "tweaks": [
            { "id": "t.ignoto", "category": "x", "name": "Ignoto",
              "ops": [ { "type": "tipo-che-non-esiste" } ] }
          ]
        }
        """);

        using var fx = new EngineFixture(catalog);
        var entry = Assert.Single(fx.Engine.Apply(catalog.Tweaks).Entries);

        Assert.Equal(EntryStatus.Skipped, entry.Status);
        Assert.Contains("non implementato", entry.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
