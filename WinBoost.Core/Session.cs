using System.Text.Json;

namespace WinBoost.Core;

public enum EntryStatus { Applied, Failed, Skipped, RolledBack }

public sealed class ServiceState
{
    public string Name { get; set; } = "";
    public string? StartType { get; set; }
    public bool WasRunning { get; set; }
    public bool Existed { get; set; }
}

public sealed class DnsState
{
    public string InterfaceAlias { get; set; } = "";
    public List<string> Servers { get; set; } = new();
    public bool WasAutomatic { get; set; }
}

public sealed class AdapterPropertyState
{
    public string AdapterName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? PreviousValue { get; set; }
}

/// <summary>
/// Una voce di journal. Contiene tutto il necessario per annullare la singola
/// operazione senza dover riconsultare il catalogo.
/// </summary>
public sealed class SessionEntry
{
    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public int OpIndex { get; set; }
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string? ValueName { get; set; }
    public string? RevertKey { get; set; }
    public string? AppliedValue { get; set; }
    public string RevertMode { get; set; } = "snapshot";
    public string? RevertNote { get; set; }

    public RegistryState? RegistryBefore { get; set; }
    public ServiceState? ServiceBefore { get; set; }
    public DnsState? DnsBefore { get; set; }
    public AdapterPropertyState? AdapterBefore { get; set; }
    public string? PowerPlanBefore { get; set; }

    public string? RevertExe { get; set; }
    public List<string>? RevertArgs { get; set; }

    /// <summary>File di backup prodotto prima della modifica (es. .nip NVIDIA).</summary>
    public string? BackupFilePath { get; set; }

    public EntryStatus Status { get; set; } = EntryStatus.Applied;
    public string? Message { get; set; }

    public bool CanRollback =>
        Status == EntryStatus.Applied && !string.Equals(RevertMode, "none", StringComparison.OrdinalIgnoreCase);
}

public sealed class Session
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }
    public string Machine { get; set; } = Environment.MachineName;
    public string User { get; set; } = Environment.UserName;
    public string? PresetId { get; set; }
    public bool RebootRequired { get; set; }

    /// <summary>Processi di shell che andrebbero riavviati e non lo sono stati.</summary>
    public List<string> ShellRestartPending { get; set; } = new();

    public List<SessionEntry> Entries { get; set; } = new();

    public int AppliedCount => Entries.Count(e => e.Status == EntryStatus.Applied);
    public int FailedCount => Entries.Count(e => e.Status == EntryStatus.Failed);
    public int SkippedCount => Entries.Count(e => e.Status == EntryStatus.Skipped);
    public bool IsRolledBack => RolledBackAt is not null;

    public IEnumerable<string> TweakNames => Entries.Select(e => e.TweakName).Distinct();

    public static Session New(string? presetId) => new()
    {
        Id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"),
        StartedAt = DateTimeOffset.Now,
        PresetId = presetId
    };
}

/// <summary>Persistenza delle sessioni su disco, sotto il profilo utente.</summary>
public sealed class SessionStore
{
    private readonly string _dir;

    public SessionStore(string? baseDir = null)
    {
        _dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinBoost", "sessions");
        Directory.CreateDirectory(_dir);
    }

    public string Root => _dir;

    public void Save(Session session)
    {
        var path = Path.Combine(_dir, $"{session.Id}.json");
        var json = JsonSerializer.Serialize(session, CatalogLoader.Options);

        // Scrittura atomica: un crash a meta' salvataggio lascerebbe un journal
        // corrotto, cioe' un rollback impossibile.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public Session? Load(string id)
    {
        var path = Path.Combine(_dir, $"{id}.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Session>(File.ReadAllText(path), CatalogLoader.Options); }
        catch (JsonException) { return null; }
    }

    public List<Session> LoadAll()
    {
        var sessions = new List<Session>();
        foreach (var file in System.IO.Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(file), CatalogLoader.Options);
                if (s is not null) sessions.Add(s);
            }
            catch (JsonException) { /* journal illeggibile: lo ignoriamo, non lo cancelliamo */ }
        }
        return sessions.OrderByDescending(s => s.StartedAt).ToList();
    }
}
