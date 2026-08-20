using System.Text.Json;
using Microsoft.Win32;
using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

/// <summary>
/// Chiave di lavoro sotto HKCU, creata e distrutta dal test.
/// Niente HKLM: i test non devono richiedere privilegi ne' toccare lo stato di sistema.
/// </summary>
public sealed class ScratchKey : IDisposable
{
    public ScratchKey()
    {
        Path = $@"HKCU:\Software\WinBoost.Tests\{Guid.NewGuid():N}";
    }

    public string Path { get; }

    public void Dispose() => RegistryHelper.DeleteKeyTree(Path);
}

public class RegistryHelperTests
{
    [Theory]
    [InlineData(@"HKCU:\Software\Test", @"Software\Test")]
    [InlineData(@"HKLM:\SYSTEM\Foo", @"SYSTEM\Foo")]
    [InlineData(@"HKEY_CURRENT_USER\Software\Test", @"Software\Test")]
    public void SplitEstraeHiveESottochiave(string input, string expectedSub)
    {
        var (root, sub) = RegistryHelper.Split(input);
        using (root) Assert.Equal(expectedSub, sub);
    }

    [Fact]
    public void SplitRifiutaUnHiveSconosciuto()
    {
        var ex = Assert.Throws<ArgumentException>(() => RegistryHelper.Split(@"HKXX:\Foo"));
        Assert.Contains("HKXX", ex.Message);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("0", 0)]
    [InlineData("4294967295", -1)]        // 0xFFFFFFFF reinterpretato come int, come fa regedit
    [InlineData("2525368439", -1769598857)]
    public void CoerceGestisceDwordOltreIntMaxValue(string json, int expected)
    {
        var element = JsonDocument.Parse(json).RootElement;
        Assert.Equal(expected, RegistryHelper.Coerce(element, "DWord"));
    }

    [Fact]
    public void CoerceConverteBinarioDaEsadecimale()
    {
        var element = JsonDocument.Parse("\"9012038010000000\"").RootElement;
        var bytes = Assert.IsType<byte[]>(RegistryHelper.Coerce(element, "Binary"));
        Assert.Equal(new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void ParseHexRifiutaLunghezzaDispari()
        => Assert.Throws<FormatException>(() => RegistryHelper.ParseHex("ABC"));

    [Fact]
    public void ScritturaELetturaDiUnDwordSonoSimmetriche()
    {
        using var scratch = new ScratchKey();
        RegistryHelper.Write(scratch.Path, "Numero", "DWord", 42);

        var state = RegistryHelper.Capture(scratch.Path, "Numero");
        Assert.True(state.KeyExists);
        Assert.True(state.ValueExists);
        Assert.Equal("DWord", state.Kind);
        Assert.Equal("42", state.Value);
    }

    [Fact]
    public void IlRipristinoRimetteIlValorePrecedente()
    {
        using var scratch = new ScratchKey();
        RegistryHelper.Write(scratch.Path, "Valore", "DWord", 7);

        var before = RegistryHelper.Capture(scratch.Path, "Valore");
        RegistryHelper.Write(scratch.Path, "Valore", "DWord", 99);
        Assert.Equal("99", RegistryHelper.Capture(scratch.Path, "Valore").Value);

        RegistryHelper.Restore(scratch.Path, "Valore", before);
        Assert.Equal("7", RegistryHelper.Capture(scratch.Path, "Valore").Value);
    }

    [Fact]
    public void IlRipristinoCancellaUnValoreCheNonEsisteva()
    {
        using var scratch = new ScratchKey();

        // Snapshot di una voce assente: il rollback deve cancellare, non inventare un default.
        var before = RegistryHelper.Capture(scratch.Path, "Nuovo");
        Assert.False(before.ValueExists);

        RegistryHelper.Write(scratch.Path, "Nuovo", "DWord", 1);
        Assert.True(RegistryHelper.Capture(scratch.Path, "Nuovo").ValueExists);

        RegistryHelper.Restore(scratch.Path, "Nuovo", before);
        Assert.False(RegistryHelper.Capture(scratch.Path, "Nuovo").ValueExists);
    }

    [Fact]
    public void IlRipristinoSopravviveAlRoundTripJsonDellaSessione()
    {
        using var scratch = new ScratchKey();
        RegistryHelper.Write(scratch.Path, "Bin", "Binary", new byte[] { 1, 2, 3, 250 });

        var before = RegistryHelper.Capture(scratch.Path, "Bin");

        // Il journal viene serializzato: lo stato deve reggere la conversione.
        var json = JsonSerializer.Serialize(before, CatalogLoader.Options);
        var revived = JsonSerializer.Deserialize<RegistryState>(json, CatalogLoader.Options)!;

        RegistryHelper.Write(scratch.Path, "Bin", "Binary", new byte[] { 9, 9 });
        RegistryHelper.Restore(scratch.Path, "Bin", revived);

        var (root, sub) = RegistryHelper.Split(scratch.Path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            Assert.Equal(new byte[] { 1, 2, 3, 250 }, Assert.IsType<byte[]>(key!.GetValue("Bin")));
    }

    [Fact]
    public void UnaStringaMultiplaSopravviveAlRipristino()
    {
        using var scratch = new ScratchKey();
        RegistryHelper.Write(scratch.Path, "Multi", "MultiString", new[] { "uno", "due" });

        var before = RegistryHelper.Capture(scratch.Path, "Multi");
        RegistryHelper.Write(scratch.Path, "Multi", "MultiString", new[] { "altro" });
        RegistryHelper.Restore(scratch.Path, "Multi", before);

        var (root, sub) = RegistryHelper.Split(scratch.Path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            Assert.Equal(new[] { "uno", "due" }, Assert.IsType<string[]>(key!.GetValue("Multi")));
    }

    [Fact]
    public void IlValorePredefinitoDellaChiaveEGestito()
    {
        using var scratch = new ScratchKey();
        RegistryHelper.Write(scratch.Path, RegistryHelper.DefaultValueName, "String", "contenuto");

        var (root, sub) = RegistryHelper.Split(scratch.Path);
        using (root)
        using (var key = root.OpenSubKey(sub))
            Assert.Equal("contenuto", key!.GetValue(""));

        Assert.Equal("contenuto", RegistryHelper.Capture(scratch.Path, RegistryHelper.DefaultValueName).Value);
    }

    [Fact]
    public void KeyExistsDistingueChiavePresenteEAssente()
    {
        using var scratch = new ScratchKey();
        Assert.False(RegistryHelper.KeyExists(scratch.Path));

        RegistryHelper.Write(scratch.Path, "x", "DWord", 1);
        Assert.True(RegistryHelper.KeyExists(scratch.Path));

        RegistryHelper.DeleteKeyTree(scratch.Path);
        Assert.False(RegistryHelper.KeyExists(scratch.Path));
    }
}
