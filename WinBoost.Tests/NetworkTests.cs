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

/// <summary>
/// Le istanze dei filtri NDIS non sono schede di rete. Su una macchina reale con
/// VPN e Hyper-V ne comparivano venti accanto a tre schede vere, e ogni tweak di
/// rete veniva risolto su tutte: anteprima illeggibile e scritture tentate contro
/// oggetti che non esistono. L'enumerazione ora passa da MSFT_NetAdapter; questa
/// e' la rete di sicurezza del percorso di ripiego.
/// </summary>
public class NdisFilterInstanceTests
{
    private static readonly string[] Reali = { "Ethernet", "vEthernet (Default Switch)", "Wi-Fi" };

    [Theory]
    [InlineData("Ethernet-QoS Packet Scheduler-0000")]
    [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("Ethernet-Fortinet NDIS 6.0 LightWeight Filter-0000")]
    [InlineData("vEthernet (Default Switch)-QoS Packet Scheduler-0000")]
    public void UnIstanzaDiFiltroVieneRiconosciuta(string nome)
    {
        Assert.True(HardwareProbe.IsNdisFilterInstance(nome, Reali));
    }

    [Theory]
    [InlineData("Ethernet")]
    [InlineData("Wi-Fi")]
    [InlineData("vEthernet (Default Switch)")]
    [InlineData("NordLynx")]
    public void UnaSchedaVeraNonVieneScartata(string nome)
    {
        Assert.False(HardwareProbe.IsNdisFilterInstance(nome, Reali));
    }

    /// <summary>
    /// Non basta che il nome finisca con quattro cifre: serve che il prefisso sia
    /// davvero una scheda dell'elenco. Una scheda chiamata "Realtek 2500" non e'
    /// l'istanza di un filtro di qualcosa.
    /// </summary>
    [Fact]
    public void QuattroCifreFinaliDaSoleNonBastano()
    {
        Assert.False(HardwareProbe.IsNdisFilterInstance("Realtek 2500", Reali));
        Assert.False(HardwareProbe.IsNdisFilterInstance("Scheda-1234", Reali));
    }

    /// <summary>Il prefisso non deve poter essere la scheda stessa.</summary>
    [Fact]
    public void UnaSchedaNonEIstanzaDiSeStessa()
    {
        Assert.False(HardwareProbe.IsNdisFilterInstance("Ethernet-0000", new[] { "Ethernet-0000" }));
    }

    [Fact]
    public void SuUnElencoVuotoNessunNomeEUnFiltro()
    {
        Assert.False(HardwareProbe.IsNdisFilterInstance("Ethernet-QoS Packet Scheduler-0000",
            Array.Empty<string>()));
    }
}
