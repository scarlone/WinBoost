using System.Net;
using System.Net.Http;
using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("v0.1.0", 0, 1, 0)]
    [InlineData("V2.10.3", 2, 10, 3)]
    [InlineData("1.2", 1, 2, 0)]                 // patch assente vale zero
    [InlineData("0.1.0+95046d6", 0, 1, 0)]       // metadati di build ignorati
    [InlineData(" 1.0.0 ", 1, 0, 0)]
    public void ParseFormeValide(string text, int major, int minor, int patch)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(new SemVer(major, minor, patch, null), v);
    }

    [Fact]
    public void ParseConservaIlSuffissoPrerelease()
    {
        Assert.True(SemVer.TryParse("v1.2.3-rc.1+abc", out var v));
        Assert.Equal("rc.1", v.PreRelease);
        Assert.True(v.IsPreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]                      // quattro numeri non sono semver
    [InlineData("v-1.0.0")]
    [InlineData("1.2.3-rc 1")]                   // lo spazio non e' ammesso
    [InlineData("99999999999.0.0")]              // fuori dai limiti di int
    public void ParseRifiutaFormeNonValide(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.0", "1.1.0")]
    [InlineData("1.9.0", "2.0.0")]
    [InlineData("0.1.0", "0.10.0")]              // confronto numerico, non testuale
    public void OrdinamentoNumerico(string minore, string maggiore)
    {
        Assert.True(SemVer.TryParse(minore, out var a));
        Assert.True(SemVer.TryParse(maggiore, out var b));
        Assert.True(b > a);
        Assert.True(a < b);
    }

    [Fact]
    public void LaFinaleBatteLaSuaPrerelease()
    {
        Assert.True(SemVer.TryParse("1.0.0-rc1", out var rc));
        Assert.True(SemVer.TryParse("1.0.0", out var finale));

        Assert.True(finale > rc);
        Assert.False(rc > finale);
    }

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")] // meno identificativi vale meno
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2")]
    [InlineData("1.0.0-alpha.9", "1.0.0-alpha.10")]  // numerico, non lessicografico
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void OrdinamentoDellePrerelease(string minore, string maggiore)
    {
        Assert.True(SemVer.TryParse(minore, out var a));
        Assert.True(SemVer.TryParse(maggiore, out var b));
        Assert.True(b > a);
    }

    [Fact]
    public void VersioniUgualiNonSonoUnAggiornamento()
    {
        Assert.True(SemVer.TryParse("0.1.0", out var a));
        Assert.True(SemVer.TryParse("v0.1.0+altrocommit", out var b));

        Assert.Equal(0, a.CompareTo(b));
        Assert.False(b > a);
    }

    [Fact]
    public void ToStringRicostruisceLaForma()
    {
        Assert.True(SemVer.TryParse("v1.2.3-rc.1+sha", out var v));
        Assert.Equal("1.2.3-rc.1", v.ToString());
    }
}

public class InstallOriginTests
{
    private static readonly string[] Roots =
    {
        @"C:\Users\tizio\AppData\Local\Microsoft\WinGet\Packages"
    };

    [Fact]
    public void RiconosceUnaInstallazioneWinget()
    {
        var exe = @"C:\Users\tizio\AppData\Local\Microsoft\WinGet\Packages\scarlone.WinBoost_abc\WinBoost.exe";
        Assert.Equal(InstallKind.Winget, InstallOrigin.Detect(exe, Roots));
    }

    [Fact]
    public void UnaCartellaQualsiasiEStandalone()
    {
        Assert.Equal(InstallKind.Standalone, InstallOrigin.Detect(@"C:\Strumenti\WinBoost.exe", Roots));
    }

    /// <summary>
    /// Il caso che uno StartsWith sul testo sbaglierebbe: una cartella il cui nome
    /// inizia come la radice di winget ma che non le sta dentro.
    /// </summary>
    [Fact]
    public void NonConfondeUnPrefissoConUnaCartellaContenitrice()
    {
        Assert.False(InstallOrigin.IsUnder(@"C:\Packages-vecchi\WinBoost.exe", @"C:\Packages"));
        Assert.True(InstallOrigin.IsUnder(@"C:\Packages\WinBoost.exe", @"C:\Packages"));
    }

    [Fact]
    public void ConfrontoInsensibileAMaiuscoleEBarreFinali()
    {
        var exe = @"c:\users\tizio\appdata\local\microsoft\winget\packages\x\WinBoost.exe";
        var roots = new[] { @"C:\Users\tizio\AppData\Local\Microsoft\WinGet\Packages\" };

        Assert.Equal(InstallKind.Winget, InstallOrigin.Detect(exe, roots));
    }

    [Fact]
    public void PercorsoVuotoNonFaEsplodereNulla()
    {
        Assert.Equal(InstallKind.Standalone, InstallOrigin.Detect("", Roots));
        Assert.False(InstallOrigin.IsUnder(@"C:\x\WinBoost.exe", ""));
    }
}

public class UpdateCheckerTests
{
    private static string Payload(string tag, bool draft = false, bool prerelease = false) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://esempio-ostile.invalid/scarica-questo"
        }
        """;

    private static SemVer V(string text)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        return v;
    }

    [Fact]
    public void UnaVersionePiuRecenteEUnAggiornamento()
    {
        var info = UpdateChecker.Evaluate(Payload("v0.2.0"), V("0.1.0"), InstallKind.Standalone);

        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal("0.2.0", info.Latest.ToString());
        Assert.Equal("0.1.0", info.Current.ToString());
    }

    [Fact]
    public void LaStessaVersioneNonEUnAggiornamento()
    {
        var info = UpdateChecker.Evaluate(Payload("v0.1.0"), V("0.1.0"), InstallKind.Standalone);

        Assert.NotNull(info);
        Assert.False(info!.IsNewer);
    }

    [Fact]
    public void UnaVersionePiuVecchiaNonEUnAggiornamento()
    {
        var info = UpdateChecker.Evaluate(Payload("v0.0.9"), V("0.1.0"), InstallKind.Standalone);

        Assert.NotNull(info);
        Assert.False(info!.IsNewer);
    }

    /// <summary>
    /// Il cuore della scelta di sicurezza: l'URL di destinazione nasce da una
    /// costante del programma, non dal campo html_url della risposta. Un canale
    /// compromesso puo' mentire sul numero di versione, non su dove mandare
    /// l'utente a scaricare.
    /// </summary>
    [Fact]
    public void LUrlIgnoraQuantoDichiaratoDalServer()
    {
        var info = UpdateChecker.Evaluate(Payload("v0.2.0"), V("0.1.0"), InstallKind.Standalone);

        Assert.NotNull(info);
        Assert.Equal("https://github.com/scarlone/WinBoost/releases/tag/v0.2.0", info!.ReleasePageUrl);
        Assert.DoesNotContain("esempio-ostile", info.ReleasePageUrl);
    }

    [Theory]
    [InlineData("v0.2.0/../../altro")]
    [InlineData("https://altrosito.invalid")]
    [InlineData("v0.2.0 con spazi")]
    [InlineData("")]
    public void UnaTagFuoriDallAlfabetoAmmessoVieneRifiutata(string tag)
    {
        Assert.Null(UpdateChecker.Evaluate(Payload(tag), V("0.1.0"), InstallKind.Standalone));
    }

    [Fact]
    public void BozzeEPrereleaseVengonoIgnorate()
    {
        Assert.Null(UpdateChecker.Evaluate(Payload("v0.2.0", draft: true), V("0.1.0"), InstallKind.Standalone));
        Assert.Null(UpdateChecker.Evaluate(Payload("v0.2.0", prerelease: true), V("0.1.0"), InstallKind.Standalone));
    }

    [Theory]
    [InlineData("")]
    [InlineData("non json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\": null}")]
    [InlineData("{\"tag_name\": 42}")]
    [InlineData("{\"tag_name\": \"nightly\"}")]   // tag valida ma non una versione
    public void UnaRispostaInutilizzabileNonProduceNulla(string json)
    {
        Assert.Null(UpdateChecker.Evaluate(json, V("0.1.0"), InstallKind.Standalone));
    }

    [Fact]
    public void SottoWingetIlComandoSuggeritoEQuelloDiWinget()
    {
        var info = UpdateChecker.Evaluate(Payload("v0.2.0"), V("0.1.0"), InstallKind.Winget);

        Assert.NotNull(info);
        Assert.Equal(InstallKind.Winget, info!.Install);
        Assert.Equal("winget upgrade --id scarlone.WinBoost", info.WingetUpgradeCommand);
    }
}

/// <summary>
/// Il percorso di rete vero e proprio, con un handler finto al posto di GitHub:
/// quello che conta e' che nessun intoppo arrivi fino all'utente.
/// </summary>
public class UpdateCheckerNetworkTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _reply;

        public StubHandler(HttpStatusCode status, string body)
            => _reply = (_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });

        public StubHandler(Exception failure)
            => _reply = (_, _) => Task.FromException<HttpResponseMessage>(failure);

        public StubHandler(TimeSpan delay)
            => _reply = async (_, ct) =>
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => _reply(request, cancellationToken);
    }

    private static UpdateChecker Checker(HttpMessageHandler handler, TimeSpan? timeout = null) =>
        new(new HttpClient(handler), timeout);

    private static SemVer V(string text)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        return v;
    }

    [Fact]
    public async Task UnaRispostaValidaProduceIlVerdetto()
    {
        var body = """{"tag_name": "v0.3.0", "draft": false, "prerelease": false}""";
        var info = await Checker(new StubHandler(HttpStatusCode.OK, body))
            .CheckAsync(V("0.1.0"), InstallKind.Standalone);

        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal("0.3.0", info.Latest.ToString());
    }

    /// <summary>Il caso di oggi: nessuna release pubblicata, l'endpoint risponde 404.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]          // limite di richieste superato
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UnoStatoNonRiuscitoNonProduceNulla(HttpStatusCode status)
    {
        var info = await Checker(new StubHandler(status, "{}"))
            .CheckAsync(V("0.1.0"), InstallKind.Standalone);

        Assert.Null(info);
    }

    [Fact]
    public async Task LaReteAssenteNonPropagaEccezioni()
    {
        var info = await Checker(new StubHandler(new HttpRequestException("nessuna rete")))
            .CheckAsync(V("0.1.0"), InstallKind.Standalone);

        Assert.Null(info);
    }

    [Fact]
    public async Task IlTimeoutVieneRispettatoESiRisolveInSilenzio()
    {
        var checker = Checker(new StubHandler(TimeSpan.FromSeconds(30)), TimeSpan.FromMilliseconds(150));

        var started = DateTimeOffset.UtcNow;
        var info = await checker.CheckAsync(V("0.1.0"), InstallKind.Standalone);
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Null(info);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"scaduto dopo {elapsed}, troppo tardi");
    }

    [Fact]
    public async Task LAnnullamentoEsternoNonPropagaEccezioni()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var info = await Checker(new StubHandler(TimeSpan.FromSeconds(30)))
            .CheckAsync(V("0.1.0"), InstallKind.Standalone, cancellation.Token);

        Assert.Null(info);
    }
}

public class PreferencesStoreTests
{
    [Fact]
    public void SenzaFileValgonoIPredefiniti()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winboost-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(new PreferencesStore(dir).Load().CheckOnStartup);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LaSceltaSopravviveAlRiavvio()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winboost-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PreferencesStore(dir);
            store.Save(new UpdatePreferences { CheckOnStartup = false });

            // Istanza nuova: e' il caso reale, non la stessa in memoria.
            Assert.False(new PreferencesStore(dir).Load().CheckOnStartup);

            // Il nome della chiave e' documentato nel README come modo per
            // riattivare il controllo a mano: se cambia, il README mente.
            Assert.Contains("\"checkOnStartup\": false", File.ReadAllText(store.FilePath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void UnFileIllegibileRicadeSuiPredefinitiSenzaCancellarlo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "winboost-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PreferencesStore(dir);
            File.WriteAllText(store.FilePath, "{ questo non e' json");

            Assert.True(store.Load().CheckOnStartup);
            Assert.True(File.Exists(store.FilePath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
