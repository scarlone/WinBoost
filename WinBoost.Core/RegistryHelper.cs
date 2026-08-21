using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;

namespace WinBoost.Core;

/// <summary>Valore letto dal registro prima di una modifica.</summary>
public sealed class RegistryState
{
    public bool KeyExists { get; set; }
    public bool ValueExists { get; set; }
    public string? Kind { get; set; }
    /// <summary>Serializzato come stringa per sopravvivere al round-trip JSON della sessione.</summary>
    public string? Value { get; set; }
}

public static class RegistryHelper
{
    public const string DefaultValueName = "(default)";

    /// <summary>Spezza "HKLM:\SOFTWARE\Foo" in base key + sottochiave.</summary>
    public static (RegistryKey Root, string SubPath) Split(string path)
    {
        var p = path.Replace('/', '\\').Trim();
        var idx = p.IndexOf('\\');
        var hive = (idx < 0 ? p : p[..idx]).TrimEnd(':').ToUpperInvariant();
        var sub = idx < 0 ? "" : p[(idx + 1)..];

        var hiveEnum = hive switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            _ => throw new ArgumentException($"Hive di registro non riconosciuto: '{hive}' in '{path}'")
        };

        // Vista a 64 bit esplicita: evita la redirezione WOW64 se il processo
        // dovesse girare a 32 bit.
        return (RegistryKey.OpenBaseKey(hiveEnum, RegistryView.Registry64), sub);
    }

    private static string NormalizeName(string name) =>
        string.Equals(name, DefaultValueName, StringComparison.OrdinalIgnoreCase) ? "" : name;

    public static RegistryState Capture(string path, string name)
    {
        var state = new RegistryState();
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub, writable: false))
        {
            if (key is null) return state;
            state.KeyExists = true;

            var valueName = NormalizeName(name);
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return state;

            state.ValueExists = true;
            var kind = key.GetValueKind(valueName);
            state.Kind = kind.ToString();
            state.Value = Serialize(raw, kind);
        }
        return state;
    }

    public static bool KeyExists(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub, writable: false))
            return key is not null;
    }

    public static void Write(string path, string name, string valueType, object value)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.CreateSubKey(sub, writable: true)
                         ?? throw new InvalidOperationException($"Impossibile creare la chiave {path}"))
        {
            key.SetValue(NormalizeName(name), value, ParseKind(valueType));
        }
    }

    public static void DeleteValue(string path, string name)
    {
        var (root, sub) = Split(path);
        using (root)
        using (var key = root.OpenSubKey(sub, writable: true))
            key?.DeleteValue(NormalizeName(name), throwOnMissingValue: false);
    }

    public static void DeleteKeyTree(string path)
    {
        var (root, sub) = Split(path);
        using (root)
        {
            try { root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); }
            catch (ArgumentException) { /* gia' assente */ }
        }
    }

    /// <summary>Riporta la voce allo stato catturato prima della modifica.</summary>
    public static void Restore(string path, string name, RegistryState state)
    {
        if (!state.ValueExists)
        {
            // Non esisteva: la cancelliamo invece di scrivere un default inventato.
            DeleteValue(path, name);
            return;
        }

        var kind = ParseKind(state.Kind ?? "String");
        Write(path, name, kind.ToString(), Deserialize(state.Value, kind));
    }

    public static RegistryValueKind ParseKind(string valueType) => valueType.ToLowerInvariant() switch
    {
        "dword" or "registryvaluekind.dword" => RegistryValueKind.DWord,
        "qword" => RegistryValueKind.QWord,
        "string" => RegistryValueKind.String,
        "expandstring" => RegistryValueKind.ExpandString,
        "multistring" => RegistryValueKind.MultiString,
        "binary" => RegistryValueKind.Binary,
        _ => RegistryValueKind.String
    };

    /// <summary>Converte il campo "value" del JSON nel tipo .NET atteso dal registro.</summary>
    public static object Coerce(JsonElement element, string valueType)
    {
        var kind = ParseKind(valueType);
        switch (kind)
        {
            case RegistryValueKind.DWord:
                {
                    // I DWORD del catalogo possono superare int.MaxValue (es. 4294967295):
                    // li leggiamo come uint e li reinterpretiamo, come fa regedit.
                    if (element.ValueKind == JsonValueKind.Number)
                    {
                        if (element.TryGetInt32(out var i)) return i;
                        if (element.TryGetUInt32(out var u)) return unchecked((int)u);
                        if (element.TryGetInt64(out var l)) return unchecked((int)l);
                    }
                    var s = element.ToString();
                    return unchecked((int)ulong.Parse(s, CultureInfo.InvariantCulture));
                }
            case RegistryValueKind.QWord:
                return element.ValueKind == JsonValueKind.Number
                    ? element.GetInt64()
                    : long.Parse(element.ToString(), CultureInfo.InvariantCulture);
            case RegistryValueKind.Binary:
                return ParseHex(element.GetString() ?? "");
            case RegistryValueKind.MultiString:
                return element.ValueKind == JsonValueKind.Array
                    ? element.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                    : new[] { element.ToString() };
            default:
                return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        }
    }

    public static byte[] ParseHex(string hex)
    {
        hex = hex.Replace(" ", "").Replace("-", "");
        if (hex.Length % 2 != 0) throw new FormatException($"Stringa esadecimale di lunghezza dispari: '{hex}'");
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static string Serialize(object raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToHexString((byte[])raw),
        RegistryValueKind.MultiString => string.Join("\0", (string[])raw),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? ""
    };

    private static object Deserialize(string? value, RegistryValueKind kind)
    {
        value ??= "";
        return kind switch
        {
            RegistryValueKind.DWord => unchecked((int)long.Parse(value, CultureInfo.InvariantCulture)),
            RegistryValueKind.QWord => long.Parse(value, CultureInfo.InvariantCulture),
            RegistryValueKind.Binary => ParseHex(value),
            RegistryValueKind.MultiString => value.Split('\0'),
            _ => value
        };
    }

    /// <summary>Rappresentazione leggibile per l'anteprima delle modifiche.</summary>
    public static string Describe(RegistryState state)
    {
        if (!state.KeyExists) return "(chiave assente)";
        if (!state.ValueExists) return "(valore assente)";
        if (state.Kind == "Binary") return $"0x{state.Value}";

        return DescribeValue(state.Value);
    }

    /// <summary>
    /// Una stringa vuota e' un valore legittimo, non un'assenza: e' proprio cio' che
    /// scrivono i tweak che disattivano un handler (il menu contestuale classico di
    /// Windows 11, per esempio). Renderla come cella bianca nell'anteprima la
    /// farebbe sembrare un difetto di visualizzazione invece della modifica vera.
    /// </summary>
    public static string DescribeValue(string? value) =>
        string.IsNullOrEmpty(value) ? "(stringa vuota)" : value;
}
