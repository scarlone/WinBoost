using System.Runtime.Versioning;

namespace WinBoost.Core;

public sealed record Applicability(bool CanApply, string? Reason)
{
    public static readonly Applicability Ok = new(true, null);
    public static Applicability No(string reason) => new(false, reason);
}

/// <summary>Operazione con i segnaposto gia' risolti su hardware reale.</summary>
public sealed record ResolvedOp(
    TweakOp Op,
    int OpIndex,
    string? Path = null,
    string? Name = null,
    string? AdapterName = null,
    string? AdapterDescription = null,
    string? Label = null);

public sealed record PlannedChange(
    string TweakId,
    string TweakName,
    string Kind,
    string Target,
    string Current,
    string Proposed,
    RiskLevel Risk,
    string? Warning);

/// <summary>
/// Orchestrazione: decide cosa e' applicabile, in che ordine, e tiene il journal.
/// Il "come" di ogni tipo di operazione vive nel rispettivo <see cref="OpHandler"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TweakEngine
{
    private readonly Catalog _catalog;
    private readonly HardwareProbe _probe;
    private readonly SessionStore _store;

    public event Action<string>? Log;

    public TweakEngine(Catalog catalog, HardwareProbe probe, SessionStore store)
    {
        _catalog = catalog;
        _probe = probe;
        _store = store;
    }

    public Catalog Catalog => _catalog;
    public HardwareProbe Probe => _probe;
    public SessionStore Store => _store;

    /// <summary>Scelte dell'utente che prevalgono sui default del catalogo.</summary>
    public ParameterOverrides Overrides { get; } = new();

    /// <summary>Profili NVIDIA verificati. Se null, le operazioni nvapi-profile vengono saltate.</summary>
    public NvidiaProfileCatalog? NvidiaProfiles { get; set; }

    /// <summary>Integrazione con NVIDIA Profile Inspector, se disponibile sul sistema.</summary>
    public ProfileInspector? Inspector { get; set; }

    /// <summary>
    /// Se true, a fine sessione riavvia i processi di shell dichiarati dai tweak applicati.
    /// Disattivabile: chiudere Explorer chiude anche le finestre aperte dall'utente.
    /// </summary>
    public bool AutoRestartShell { get; set; } = true;

    /// <summary>
    /// Tipi di operazione eseguibili. E' un contratto verso il catalogo: un tipo dichiarato
    /// nei dati e assente qui verrebbe saltato in silenzio, ed e' cio' che i test controllano.
    /// </summary>
    public static IReadOnlySet<string> SupportedTypes => OpRegistry.Types;

    /// <summary>Cartella dove finiscono i backup dei profili GPU, accanto alle sessioni.</summary>
    private string GpuBackupDir => Path.Combine(
        Path.GetDirectoryName(_store.Root.TrimEnd(Path.DirectorySeparatorChar)) ?? _store.Root, "gpu-profiles");

    private void Emit(string message) => Log?.Invoke(message);

    // ------------------------------------------------------------------
    // Applicabilita'
    // ------------------------------------------------------------------

    /// <summary>
    /// Vincoli hardware: se falliscono, i bersagli non esistono proprio su questa macchina
    /// e non c'e' nulla da mostrare nemmeno in anteprima.
    /// </summary>
    public Applicability CheckHardware(Tweak tweak)
    {
        if (!string.IsNullOrWhiteSpace(tweak.Vendor) && !_probe.HasVendor(tweak.Vendor))
            return Applicability.No($"nessuna GPU {tweak.Vendor.ToUpperInvariant()} rilevata");

        if (!string.IsNullOrWhiteSpace(tweak.Condition))
        {
            var met = tweak.Condition switch
            {
                "storage.system-drive-is-ssd" => _probe.SystemDriveIsSsd,
                _ => true
            };
            if (!met) return Applicability.No($"condizione non soddisfatta: {tweak.Condition}");
        }

        return Applicability.Ok;
    }

    /// <summary>
    /// Vincoli per l'applicazione effettiva: hardware piu' elevazione.
    /// Deliberatamente non usato dall'anteprima: l'anteprima serve a decidere
    /// SE elevare, quindi deve poter mostrare anche cio' che richiede privilegi.
    /// </summary>
    public Applicability CheckApplicable(Tweak tweak)
    {
        if (tweak.Admin && !HardwareProbe.IsElevated())
            return Applicability.No("richiede privilegi di amministratore");

        return CheckHardware(tweak);
    }

    public List<Tweak> ResolvePreset(Preset preset)
    {
        var result = new List<Tweak>();

        foreach (var t in _catalog.Tweaks)
        {
            if (!t.IsDefaultOn) continue;                     // opt-in esplicito richiesto
            var inc = preset.Include;

            if (!inc.All)
            {
                if (inc.Categories is { Count: > 0 } &&
                    !inc.Categories.Contains(t.Category, StringComparer.OrdinalIgnoreCase)) continue;

                if (inc.Risk is { Count: > 0 } &&
                    !inc.Risk.Contains(t.Risk.ToString().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)) continue;

                if (!string.IsNullOrWhiteSpace(inc.MaxRisk))
                {
                    var max = inc.MaxRisk.ToLowerInvariant() switch
                    {
                        "low" => RiskLevel.Low,
                        "medium" => RiskLevel.Medium,
                        _ => RiskLevel.High
                    };
                    if (t.Risk > max) continue;
                }
            }

            result.Add(t);
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Risoluzione dei bersagli
    // ------------------------------------------------------------------

    public List<ResolvedOp> Resolve(Tweak tweak)
    {
        var services = new Services(this, new AdapterCache());
        var resolved = new List<ResolvedOp>();

        for (var i = 0; i < tweak.Ops.Count; i++)
        {
            var op = tweak.Ops[i];
            var handler = OpRegistry.Find(op.Type);

            // Un tipo sconosciuto produce comunque un bersaglio: cosi' l'anteprima puo'
            // dichiararlo non supportato invece di farlo sparire.
            if (handler is null) resolved.Add(new ResolvedOp(op, i));
            else resolved.AddRange(handler.Resolve(tweak, op, i, services));
        }

        return resolved;
    }

    // ------------------------------------------------------------------
    // Anteprima
    // ------------------------------------------------------------------

    public List<PlannedChange> Preview(IEnumerable<Tweak> tweaks)
    {
        var changes = new List<PlannedChange>();

        // Una sola cache per passata: le letture sulle schede di rete sono le piu' costose.
        var services = new Services(this, new AdapterCache());

        foreach (var tweak in tweaks)
        {
            // Solo i vincoli hardware fanno saltare la riga: la mancanza di privilegi
            // viene segnalata come nota, non nasconde cosa verrebbe modificato.
            var hardware = CheckHardware(tweak);
            if (!hardware.CanApply)
            {
                changes.Add(Skip(tweak, $"ignorato: {hardware.Reason}"));
                continue;
            }

            var resolved = Resolve(tweak);
            if (resolved.Count == 0)
            {
                changes.Add(Skip(tweak, "nessun bersaglio applicabile su questo sistema"));
                continue;
            }

            foreach (var r in resolved)
                changes.Add(Describe(tweak, r, services));
        }

        return changes;
    }

    private PlannedChange Skip(Tweak tweak, string reason) =>
        new(tweak.Id, tweak.Name, "skip", "-", "-", reason, tweak.Risk, tweak.Warning);

    private PlannedChange Describe(Tweak tweak, ResolvedOp r, Services services)
    {
        var handler = OpRegistry.Find(r.Op.Type);

        var description = handler?.Describe(tweak, r, services)
            ?? new ChangeDescription(r.Op.Type, "-", "NON SUPPORTATO in questa build");

        var label = r.Label is null ? tweak.Name : $"{tweak.Name} [{r.Label}]";

        // L'anteprima mostra comunque la modifica, ma dichiara che servira' l'elevazione.
        var warning = tweak.Warning;
        if (tweak.Admin && !HardwareProbe.IsElevated())
            warning = string.IsNullOrWhiteSpace(warning)
                ? "richiede privilegi di amministratore per essere applicata"
                : $"{warning} (richiede privilegi di amministratore)";

        return new PlannedChange(tweak.Id, label, r.Op.Type,
            description.Target, description.Current, description.Proposed, tweak.Risk, warning);
    }

    // ------------------------------------------------------------------
    // Applicazione
    // ------------------------------------------------------------------

    public Session Apply(IEnumerable<Tweak> tweaks, string? presetId = null)
    {
        var session = Session.New(presetId);
        var list = tweaks.ToList();
        var pendingRestarts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var services = new Services(this, new AdapterCache());

        Emit($"Sessione {session.Id}: {list.Count} tweak selezionati.");

        foreach (var tweak in list)
        {
            var applicable = CheckApplicable(tweak);
            if (!applicable.CanApply)
            {
                Emit($"SALTATO {tweak.Id}: {applicable.Reason}");
                session.Entries.Add(new SessionEntry
                {
                    TweakId = tweak.Id, TweakName = tweak.Name, Kind = "skip",
                    Target = "-", Status = EntryStatus.Skipped, Message = applicable.Reason,
                    RevertMode = "none"
                });
                continue;
            }

            var appliedAny = false;

            foreach (var r in Resolve(tweak))
            {
                var entry = ExecuteOp(tweak, r, services);
                session.Entries.Add(entry);
                if (entry.Status == EntryStatus.Applied) appliedAny = true;

                // Il journal viene salvato dopo ogni operazione: se il processo muore
                // a meta' sessione, il rollback resta comunque possibile.
                _store.Save(session);
            }

            if (tweak.Reboot) session.RebootRequired = true;

            // I bersagli si accumulano e si riavviano una volta sola a fine sessione:
            // riavviare Explorer una volta per tweak sarebbe intollerabile.
            if (appliedAny)
                foreach (var target in tweak.Restart)
                    pendingRestarts.Add(target);

            Emit($"OK {tweak.Id} - {tweak.Name}");
        }

        RestartShellTargets(pendingRestarts, session);

        _store.Save(session);
        Emit($"Sessione {session.Id} completata: {session.AppliedCount} applicate, "
           + $"{session.SkippedCount} saltate, {session.FailedCount} fallite.");

        return session;
    }

    private SessionEntry ExecuteOp(Tweak tweak, ResolvedOp r, Services services)
    {
        var entry = new SessionEntry
        {
            TweakId = tweak.Id,
            TweakName = tweak.Name,
            OpIndex = r.OpIndex,
            Kind = r.Op.Type,
            RevertMode = r.Op.Revert,
            RevertNote = r.Op.RevertNote,
            Status = EntryStatus.Applied
        };

        var handler = OpRegistry.Find(r.Op.Type);
        if (handler is null)
        {
            entry.Target = r.Op.Type;
            entry.Status = EntryStatus.Skipped;
            entry.RevertMode = "none";
            entry.Message = $"tipo di operazione '{r.Op.Type}' non implementato in questa build";
            return entry;
        }

        try
        {
            handler.Execute(tweak, r, entry, services);
        }
        catch (Exception ex) when (IsOperational(ex))
        {
            entry.Target = r.Path ?? r.Label ?? r.Op.Type;
            entry.Status = EntryStatus.Failed;
            entry.Message = ex.Message;
            entry.RevertMode = "none";
            Emit($"ERRORE {tweak.Id} [{r.Op.Type}]: {ex.Message}");
        }

        return entry;
    }

    private void RestartShellTargets(HashSet<string> targets, Session session)
    {
        if (targets.Count == 0) return;

        if (!AutoRestartShell)
        {
            Emit($"Riavvio della shell disattivato: le modifiche a {string.Join(", ", targets)} "
               + "saranno visibili dopo il logout.");
            session.ShellRestartPending = targets.ToList();
            return;
        }

        foreach (var target in targets)
        {
            Emit($"Riavvio di {target} per rendere effettive le modifiche...");
            Emit("  " + ShellRestarter.Restart(target));
        }
    }

    // ------------------------------------------------------------------
    // Rollback
    // ------------------------------------------------------------------

    public Session Rollback(Session session)
    {
        Emit($"Rollback della sessione {session.Id}...");
        var services = new Services(this, new AdapterCache());

        // In ordine inverso: le operazioni successive possono dipendere dalle precedenti.
        foreach (var entry in Enumerable.Reverse(session.Entries).ToList())
        {
            if (!entry.CanRollback) continue;

            var handler = OpRegistry.Find(entry.Kind);
            if (handler is null) continue;

            try
            {
                if (!handler.Rollback(entry, services)) continue;

                entry.Status = EntryStatus.RolledBack;
                Emit($"Ripristinato: {entry.TweakName} - {entry.Target}");
            }
            catch (Exception ex) when (IsOperational(ex))
            {
                entry.Message = $"rollback fallito: {ex.Message}";
                Emit($"ERRORE rollback {entry.TweakName}: {ex.Message}");
            }
        }

        session.RolledBackAt = DateTimeOffset.Now;
        _store.Save(session);
        Emit($"Rollback della sessione {session.Id} completato.");

        return session;
    }

    /// <summary>
    /// Errori che ci si aspetta da un sistema reale: permessi, file in uso, chiavi sparite.
    /// Tutto il resto e' un difetto nostro e deve emergere, non essere assorbito.
    /// </summary>
    private static bool IsOperational(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidOperationException
           or ArgumentException or System.Security.SecurityException
           or System.ComponentModel.Win32Exception;

    // ------------------------------------------------------------------
    // Servizi verso gli handler
    // ------------------------------------------------------------------

    private sealed class Services : IOpServices
    {
        private readonly TweakEngine _engine;

        public Services(TweakEngine engine, IAdapterReadCache adapters)
        {
            _engine = engine;
            Adapters = adapters;
        }

        public HardwareProbe Probe => _engine._probe;
        public ParameterOverrides Overrides => _engine.Overrides;
        public NvidiaProfileCatalog? NvidiaProfiles => _engine.NvidiaProfiles;
        public ProfileInspector? Inspector => _engine.Inspector;
        public string GpuBackupDir => _engine.GpuBackupDir;
        public IAdapterReadCache Adapters { get; }

        public void Log(string message) => _engine.Emit(message);
    }

    /// <summary>
    /// Letture sulle schede di rete valide per una singola passata. Interrogare il sistema
    /// per ogni proprieta' di ogni scheda costava secondi; qui si paga una query e basta.
    /// </summary>
    private sealed class AdapterCache : IAdapterReadCache
    {
        private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _properties;
        private IReadOnlyDictionary<string, string>? _rsc;

        public IReadOnlyDictionary<string, string> Properties(string adapter)
        {
            _properties ??= NetworkHelper.CaptureAllAdvProperties();
            return _properties.TryGetValue(adapter, out var props) ? props : new Dictionary<string, string>();
        }

        public string? Rsc(string adapter)
        {
            _rsc ??= NetworkHelper.CaptureAllRsc();
            return _rsc.TryGetValue(adapter, out var value) ? value : null;
        }
    }
}
