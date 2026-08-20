using System.Text.Json;

namespace WinBoost.Core;

public sealed record ParameterOption(string Key, string Label);

/// <summary>Parametro configurabile di un tweak, letto da "parameters" nel catalogo.</summary>
public sealed class ParameterDef
{
    public string Key { get; init; } = "";
    public string Type { get; init; } = "choice";
    public string? Note { get; init; }

    /// <summary>Valore predefinito per i parametri di tipo "choice".</summary>
    public string? DefaultChoice { get; init; }

    /// <summary>Elementi preselezionati per i parametri di tipo "multi-select".</summary>
    public List<string> DefaultList { get; init; } = new();

    public List<ParameterOption> Options { get; init; } = new();

    public bool IsChoice => Type == "choice";
    public bool IsMultiSelect => Type == "multi-select";
}

public static class ParameterParser
{
    public static List<ParameterDef> Parse(Tweak tweak)
    {
        var defs = new List<ParameterDef>();
        if (tweak.Parameters is not { ValueKind: JsonValueKind.Object } root) return defs;

        foreach (var prop in root.EnumerateObject())
        {
            var body = prop.Value;
            if (body.ValueKind != JsonValueKind.Object) continue;

            var type = body.TryGetProperty("type", out var t) ? t.GetString() ?? "choice" : "choice";
            var note = body.TryGetProperty("note", out var n) ? n.GetString() : null;

            if (type == "choice")
                defs.Add(ParseChoice(prop.Name, body, note));
            else if (type == "multi-select")
                defs.Add(ParseMultiSelect(prop.Name, body, note));
        }

        return defs;
    }

    private static ParameterDef ParseChoice(string key, JsonElement body, string? note)
    {
        var options = new List<ParameterOption>();

        if (body.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Object)
        {
            foreach (var c in choices.EnumerateObject())
            {
                var label = c.Value.ValueKind == JsonValueKind.Object
                            && c.Value.TryGetProperty("label", out var l)
                    ? l.GetString() ?? c.Name
                    : c.Name;
                options.Add(new ParameterOption(c.Name, label));
            }
        }

        return new ParameterDef
        {
            Key = key,
            Type = "choice",
            Note = note,
            DefaultChoice = body.TryGetProperty("default", out var d) ? d.GetString() : options.FirstOrDefault()?.Key,
            Options = options
        };
    }

    private static ParameterDef ParseMultiSelect(string key, JsonElement body, string? note)
    {
        var selected = ReadStringArray(body, "default");
        var available = ReadStringArray(body, "available");

        // L'elenco completo e' l'unione dei due, preservando l'ordine e senza duplicati:
        // "default" e' cio' che parte selezionato, "available" cio' che si puo' aggiungere.
        var all = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in selected.Concat(available))
            if (seen.Add(s)) all.Add(s);

        return new ParameterDef
        {
            Key = key,
            Type = "multi-select",
            Note = note,
            DefaultList = selected,
            Options = all.Select(s => new ParameterOption(s, Prettify(s))).ToList()
        };
    }

    private static List<string> ReadStringArray(JsonElement body, string name) =>
        body.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
            : new List<string>();

    /// <summary>"Microsoft.BingNews" -> "Bing News": etichetta leggibile per la UI.</summary>
    private static string Prettify(string packageName)
    {
        var name = packageName.Contains('.')
            ? packageName[(packageName.LastIndexOf('.') + 1)..]
            : packageName;

        var spaced = System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return spaced.Length == 0 ? packageName : spaced;
    }
}

/// <summary>
/// Scelte dell'utente, sovrapposte ai default del catalogo.
/// Il catalogo resta immutabile: le preferenze vivono qui.
/// </summary>
public sealed class ParameterOverrides
{
    private readonly Dictionary<string, string> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.OrdinalIgnoreCase);

    private static string K(string tweakId, string param) => $"{tweakId}.{param}";

    public void SetChoice(string tweakId, string param, string value) => _choices[K(tweakId, param)] = value;

    public string? GetChoice(string tweakId, string param) =>
        _choices.TryGetValue(K(tweakId, param), out var v) ? v : null;

    public void SetList(string tweakId, string param, IEnumerable<string> values) =>
        _lists[K(tweakId, param)] = values.ToList();

    public IReadOnlyList<string>? GetList(string tweakId, string param) =>
        _lists.TryGetValue(K(tweakId, param), out var v) ? v : null;
}
