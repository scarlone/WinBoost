using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace WinBoost.Core;

public sealed class NvidiaSetting
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>"Dword" oppure "String", come nel formato .nip.</summary>
    public string Type { get; set; } = "Dword";
    public string Value { get; set; } = "";
}

public sealed class NvidiaProfile
{
    public string Id { get; set; } = "";
    /// <summary>"Base Profile" per le impostazioni globali del driver.</summary>
    public string ProfileName { get; set; } = "Base Profile";
    public string? Description { get; set; }
    public List<NvidiaSetting> Settings { get; set; } = new();
}

public sealed class NvidiaProfileCatalog
{
    public int SchemaVersion { get; set; }
    public string? Source { get; set; }
    public List<NvidiaProfile> Profiles { get; set; } = new();

    public NvidiaProfile? Find(string id) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static NvidiaProfileCatalog Parse(string json, string origin = "(memoria)")
    {
        var catalog = JsonSerializer.Deserialize<NvidiaProfileCatalog>(json, CatalogLoader.Options)
            ?? throw new InvalidDataException($"Profili NVIDIA non deserializzabili: {origin}");

        foreach (var p in catalog.Profiles)
        {
            if (p.Settings.Count == 0)
                throw new InvalidDataException($"Profilo NVIDIA '{p.Id}' senza impostazioni.");

            foreach (var s in p.Settings)
                if (s.Type is not ("Dword" or "String"))
                    throw new InvalidDataException(
                        $"Profilo '{p.Id}', impostazione {s.Id}: tipo '{s.Type}' non valido (attesi Dword o String).");
        }

        return catalog;
    }
}

/// <summary>
/// Scrive un file .nip nel formato letto da NVIDIA Profile Inspector.
///
/// Il formato riproduce byte per byte un file reale e funzionante, inclusa una stranezza
/// che sembra un errore ma non lo e': il file e' codificato in UTF-8 con BOM mentre la
/// dichiarazione XML annuncia utf-16. Profile Inspector deserializza da stringa, quindi
/// ignora la dichiarazione; un parser che invece la rispetta (XDocument.Load) rifiuta il
/// file. Scriviamo UTF-8 reale perche' e' cio' che il tool bersaglio accetta, e
/// manteniamo la dichiarazione originale per non divergere dall'artefatto verificato.
///
/// Profile Inspector applica in base a SettingID; SettingNameInfo e' solo documentazione.
/// </summary>
public static class NipWriter
{
    private const string Declaration = "<?xml version=\"1.0\" encoding=\"utf-16\"?>";

    public static void Write(NvidiaProfile profile, string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\r\n",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            OmitXmlDeclaration = true   // la scriviamo a mano, con la codifica dichiarata dall'originale
        };

        using var stream = File.Create(path);
        using var writer = XmlWriter.Create(stream, settings);

        // La newline fa parte del raw: con OmitXmlDeclaration l'indenter non ne inserisce
        // una prima della radice, e l'originale ha CRLF dopo la dichiarazione.
        writer.WriteRaw(Declaration + "\r\n");
        writer.WriteStartElement("ArrayOfProfile");
        writer.WriteStartElement("Profile");

        writer.WriteElementString("ProfileName", profile.ProfileName);
        writer.WriteStartElement("Executeables");   // vuoto: profilo globale, non per-applicazione
        writer.WriteEndElement();

        writer.WriteStartElement("Settings");
        foreach (var s in profile.Settings)
        {
            writer.WriteStartElement("ProfileSetting");
            writer.WriteElementString("SettingNameInfo", s.Name);
            writer.WriteElementString("SettingID", s.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString("SettingValue", s.Value);
            writer.WriteElementString("ValueType", s.Type);
            writer.WriteEndElement();
        }
        writer.WriteEndElement();   // Settings

        writer.WriteEndElement();   // Profile
        writer.WriteEndElement();   // ArrayOfProfile
        writer.WriteEndDocument();
    }

    /// <summary>
    /// Legge un .nip aggirando la dichiarazione di codifica sbagliata: il contenuto viene
    /// caricato come testo e poi deserializzato, come fa Profile Inspector.
    /// </summary>
    public static NvidiaProfile Read(string path, string id = "")
    {
        var text = File.ReadAllText(path).TrimStart('﻿');
        var doc = System.Xml.Linq.XDocument.Parse(text);

        var profileNode = doc.Root?.Element("Profile")
            ?? throw new InvalidDataException($"File .nip senza elemento Profile: {path}");

        var settings = profileNode.Element("Settings")?.Elements("ProfileSetting")
            .Select(e => new NvidiaSetting
            {
                Name = e.Element("SettingNameInfo")?.Value ?? "",
                Id = uint.Parse(e.Element("SettingID")?.Value ?? "0",
                                System.Globalization.CultureInfo.InvariantCulture),
                Value = e.Element("SettingValue")?.Value ?? "",
                Type = e.Element("ValueType")?.Value ?? "Dword"
            }).ToList() ?? new List<NvidiaSetting>();

        return new NvidiaProfile
        {
            Id = id,
            ProfileName = profileNode.Element("ProfileName")?.Value ?? "Base Profile",
            Settings = settings
        };
    }
}

public sealed record InspectorResult(bool Success, string? Message, string? BackupPath);

/// <summary>
/// Integrazione con NVIDIA Profile Inspector.
///
/// Il tool NON viene distribuito con WinBoost: e' software di terze parti e non firmato,
/// e incorporarlo significherebbe farsene garanti. Se non e' presente, l'operazione viene
/// riportata come saltata con l'indicazione di dove procurarselo.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileInspector
{
    private const string ExeName = "nvidiaProfileInspector.exe";

    public ProfileInspector(string? explicitPath = null) => ExecutablePath = Locate(explicitPath);

    public string? ExecutablePath { get; }
    public bool IsAvailable => ExecutablePath is not null;

    public static string DownloadHint =>
        "NVIDIA Profile Inspector non trovato. Scaricalo dal repository ufficiale "
        + "(Orbmu2k/nvidiaProfileInspector) e indicalo con --profile-inspector <percorso>, "
        + $@"oppure mettilo in tools\nvidiaProfileInspector\{ExeName} accanto a WinBoost.exe.";

    private static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "nvidiaProfileInspector", ExeName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinBoost", "tools", "nvidiaProfileInspector", ExeName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Esporta le impostazioni attualmente personalizzate, per poterle ripristinare.
    /// Restituisce il percorso del .nip prodotto, oppure null se il tool non ne ha creato uno.
    /// </summary>
    public string? ExportCurrent(string destinationDirectory)
    {
        if (ExecutablePath is null) return null;
        Directory.CreateDirectory(destinationDirectory);

        var before = DateTime.UtcNow.AddSeconds(-2);

        // -exportCustomized scrive nella working directory: gliene diamo una nostra,
        // ma controlliamo anche la cartella dell'eseguibile per sicurezza.
        var result = ProcessRunner.Run(ExecutablePath, new[] { "-exportCustomized" },
            timeoutSeconds: 90, workingDirectory: destinationDirectory);

        if (!result.Success) return null;

        var searchDirs = new[] { destinationDirectory, Path.GetDirectoryName(ExecutablePath)! };
        var exported = searchDirs
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.nip"))
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTimeUtc >= before)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (exported is null) return null;

        // Se il tool ha scritto altrove, portiamo il file sotto il nostro controllo:
        // il rollback deve poterlo ritrovare anche mesi dopo.
        var final = Path.Combine(destinationDirectory, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.nip");
        if (!string.Equals(exported.FullName, final, StringComparison.OrdinalIgnoreCase))
            exported.MoveTo(final, overwrite: true);

        return final;
    }

    public InspectorResult Import(string nipPath)
    {
        if (ExecutablePath is null) return new InspectorResult(false, DownloadHint, null);
        if (!File.Exists(nipPath)) return new InspectorResult(false, $"File .nip non trovato: {nipPath}", null);

        // Un'istanza gia' aperta tiene il database dei profili: va chiusa prima di importare.
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(
                     Path.GetFileNameWithoutExtension(ExeName)))
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }

        var result = ProcessRunner.Run(ExecutablePath, new[] { "-silentImport", nipPath },
            timeoutSeconds: 120, workingDirectory: Path.GetDirectoryName(ExecutablePath));

        return result.Success
            ? new InspectorResult(true, null, null)
            : new InspectorResult(false,
                result.TimedOut ? "timeout durante l'importazione" : $"exit {result.ExitCode}: {result.Combined}", null);
    }
}
