using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinBoost.Core;

public enum RiskLevel { Low = 0, Medium = 1, High = 2 }

/// <summary>Radice del file data/tweaks.json.</summary>
public sealed class Catalog
{
    public int SchemaVersion { get; set; }
    public string? Generated { get; set; }
    public List<string> Notes { get; set; } = new();
    public List<Category> Categories { get; set; } = new();
    public List<Preset> Presets { get; set; } = new();
    public List<Tweak> Tweaks { get; set; } = new();

    public Category? FindCategory(string id) =>
        Categories.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public Tweak? FindTweak(string id) =>
        Tweaks.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class Preset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool RequiresConfirmation { get; set; }
    public PresetInclude Include { get; set; } = new();
}

public sealed class PresetInclude
{
    public bool All { get; set; }
    public List<string>? Categories { get; set; }
    public List<string>? Risk { get; set; }
    public string? MaxRisk { get; set; }
}

public sealed class Tweak
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Warning { get; set; }
    public string? Note { get; set; }

    [JsonConverter(typeof(RiskConverter))]
    public RiskLevel Risk { get; set; } = RiskLevel.Medium;

    public bool Admin { get; set; }
    public bool Reboot { get; set; }
    public List<string> Restart { get; set; } = new();

    /// <summary>Vincola il tweak a un vendor GPU: "nvidia" | "amd" | "intel".</summary>
    public string? Vendor { get; set; }

    /// <summary>Nome dell'espansione dinamica richiesta (enumerazione GPU, adapter, ...).</summary>
    public string? Dynamic { get; set; }

    /// <summary>Condizione hardware/software che deve risultare vera.</summary>
    public string? Condition { get; set; }

    /// <summary>Se assente vale true: il tweak entra nei preset che lo includono.</summary>
    public bool? EnabledByDefault { get; set; }

    public string? FallbackNote { get; set; }

    public List<TweakOp> Ops { get; set; } = new();

    /// <summary>Documentazione delle impostazioni vendor (NVAPI/ADLX): informativa.</summary>
    public JsonElement? Settings { get; set; }

    /// <summary>Parametri configurabili dall'utente.</summary>
    public JsonElement? Parameters { get; set; }

    [JsonIgnore]
    public bool IsDefaultOn => EnabledByDefault ?? true;
}

/// <summary>
/// Operazione singola. Un tipo unico con campi opzionali invece di una gerarchia
/// polimorfica: il JSON resta leggibile e la deserializzazione non richiede
/// discriminatori custom.
/// </summary>
public sealed class TweakOp
{
    public string Type { get; set; } = "";

    // --- registro ---
    public string? Path { get; set; }
    public string? Name { get; set; }
    public List<string>? Names { get; set; }
    public string? ValueType { get; set; }
    public JsonElement? Value { get; set; }
    public JsonElement? Default { get; set; }
    public string? RevertKey { get; set; }
    public bool SkipIfKeyMissing { get; set; }
    public bool SkipIfValueMissing { get; set; }

    // --- processo esterno ---
    public string? Exe { get; set; }
    public List<string>? Args { get; set; }
    public string? RevertExe { get; set; }
    public List<string>? RevertArgs { get; set; }
    public bool ContinueOnError { get; set; }

    // --- servizi ---
    public string? Startup { get; set; }
    public bool Stop { get; set; }

    // --- scheda di rete ---
    public string? Pattern { get; set; }
    public bool? Enabled { get; set; }

    // --- disinstallatori ---
    public List<string>? Candidates { get; set; }
    public string? Discover { get; set; }
    public List<string>? KillFirst { get; set; }

    // --- pacchetti ---
    public string? Id { get; set; }
    public string? Url { get; set; }
    public string? Verify { get; set; }

    // --- piano energetico ---
    public string? Action { get; set; }
    public string? SourceGuid { get; set; }
    public string? FallbackGuid { get; set; }

    // --- vendor ---
    public string? Profile { get; set; }
    public string? PresetName { get; set; }

    /// <summary>"snapshot" | "none" | "cmd" | "delete-key"</summary>
    public string Revert { get; set; } = "snapshot";
    public string? RevertNote { get; set; }
    public string? Note { get; set; }
}

/// <summary>Accetta sia "low"/"medium"/"high" sia il nome dell'enum.</summary>
public sealed class RiskConverter : JsonConverter<RiskLevel>
{
    public override RiskLevel Read(ref Utf8JsonReader reader, Type t, JsonSerializerOptions o)
    {
        var s = reader.GetString();
        return s?.ToLowerInvariant() switch
        {
            "low" => RiskLevel.Low,
            "medium" => RiskLevel.Medium,
            "high" => RiskLevel.High,
            _ => RiskLevel.Medium
        };
    }

    public override void Write(Utf8JsonWriter writer, RiskLevel value, JsonSerializerOptions o)
        => writer.WriteStringValue(value.ToString().ToLowerInvariant());
}

public static class CatalogLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static Catalog Load(string path) => Parse(File.ReadAllText(path), path);

    public static Catalog Parse(string json, string origin = "(memoria)")
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(json, Options)
            ?? throw new InvalidDataException($"Catalogo non deserializzabile: {origin}");
        Validate(catalog);
        return catalog;
    }

    /// <summary>Fallisce subito su un catalogo incoerente, invece che a meta' apply.</summary>
    private static void Validate(Catalog catalog)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in catalog.Tweaks)
        {
            if (string.IsNullOrWhiteSpace(t.Id)) { errors.Add("Tweak senza id."); continue; }
            if (!seen.Add(t.Id)) errors.Add($"Id duplicato: {t.Id}");
            if (catalog.FindCategory(t.Category) is null)
                errors.Add($"{t.Id}: categoria sconosciuta '{t.Category}'");
            if (t.Ops.Count == 0)
                errors.Add($"{t.Id}: nessuna operazione definita");

            foreach (var op in t.Ops)
            {
                if (string.IsNullOrWhiteSpace(op.Type))
                    errors.Add($"{t.Id}: operazione senza type");
                if (op.Type is "reg" or "reg-template")
                {
                    if (string.IsNullOrWhiteSpace(op.Path))
                        errors.Add($"{t.Id}: operazione '{op.Type}' senza path");
                    if (op.Name is null && (op.Names is null || op.Names.Count == 0))
                        errors.Add($"{t.Id}: operazione '{op.Type}' senza name/names");
                }
            }
        }

        if (errors.Count > 0)
            throw new InvalidDataException("Catalogo non valido:\n  " + string.Join("\n  ", errors));
    }
}
