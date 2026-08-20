using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

public class AdapterPropertyMatchingTests
{
    private static readonly Dictionary<string, string> Proprieta = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Large Send Offload V2 (IPv4)"] = "Enabled",
        ["Large Send Offload V2 (IPv6)"] = "Enabled",
        ["TCP Checksum Offload (IPv4)"] = "Rx & Tx Enabled",
        ["Jumbo Packet"] = "Disabled",
        ["Interrupt Moderation"] = "Enabled"
    };

    [Fact]
    public void UnJollyFinaleCorrispondeAlPrefisso()
    {
        var match = NetworkHelper.MatchProperty(Proprieta, "Large Send Offload*");

        Assert.NotNull(match);
        Assert.StartsWith("Large Send Offload", match!.Value.Key);
    }

    [Fact]
    public void IlConfrontoIgnoraLeMaiuscole()
    {
        Assert.NotNull(NetworkHelper.MatchProperty(Proprieta, "jumbo packet"));
    }

    [Fact]
    public void UnNomeEsattoCorrisponde()
    {
        var match = NetworkHelper.MatchProperty(Proprieta, "Interrupt Moderation");

        Assert.NotNull(match);
        Assert.Equal("Enabled", match!.Value.Value);
    }

    [Fact]
    public void UnPatternSenzaCorrispondenzeRestituisceNull()
    {
        Assert.Null(NetworkHelper.MatchProperty(Proprieta, "Receive Side Scaling*"));
    }

    [Fact]
    public void LeParentesiNonVengonoInterpretateComeRegex()
    {
        // "TCP Checksum Offload (IPv4)" contiene parentesi: se il pattern finisse
        // in una regex non sfuggita, il gruppo cambierebbe il significato del confronto.
        var match = NetworkHelper.MatchProperty(Proprieta, "TCP Checksum Offload (IPv4)");

        Assert.NotNull(match);
        Assert.Equal("TCP Checksum Offload (IPv4)", match!.Value.Key);
    }

    [Fact]
    public void UnPuntoNonFungeDaJollyDiUnCarattere()
    {
        // Con una regex non sfuggita "Jumbo.Packet" corrisponderebbe: qui non deve.
        Assert.Null(NetworkHelper.MatchProperty(Proprieta, "Jumbo.Packet"));
    }

    [Fact]
    public void IlJollyDiUnSingoloCarattereFunziona()
    {
        Assert.NotNull(NetworkHelper.MatchProperty(Proprieta, "Jumbo Packe?"));
    }

    [Fact]
    public void IlJollyIsolatoCorrispondeAQualsiasiProprieta()
    {
        Assert.NotNull(NetworkHelper.MatchProperty(Proprieta, "*"));
    }

    [Fact]
    public void SuUnElencoVuotoNonEsplode()
    {
        Assert.Null(NetworkHelper.MatchProperty(new Dictionary<string, string>(), "qualsiasi*"));
    }

    [Fact]
    public void TuttiIPatternDelCatalogoSonoConfrontabili()
    {
        // Un pattern malformato lancerebbe solo al momento dell'anteprima.
        var patterns = TestData.LoadCatalog().Tweaks
            .SelectMany(t => t.Ops)
            .Where(o => o.Type == "netadapter-property")
            .Select(o => o.Pattern ?? "*");

        foreach (var p in patterns)
            Assert.Null(Record.Exception(() => NetworkHelper.MatchProperty(Proprieta, p)));
    }
}
