using System.Runtime.Versioning;

namespace WinBoost.Core;

/// <summary>Cosa cambierebbe un'operazione, senza applicarla.</summary>
public readonly record struct ChangeDescription(string Target, string Current, string Proposed);

/// <summary>
/// Letture sulle schede di rete valide per una singola passata di anteprima.
/// Deliberatamente non usata in applicazione: dopo una scrittura il valore in cache
/// sarebbe obsoleto, e gli handler rileggono direttamente.
/// </summary>
public interface IAdapterReadCache
{
    IReadOnlyDictionary<string, string> Properties(string adapter);
    string? Rsc(string adapter);
}

/// <summary>Quello che un handler puo' chiedere al motore, e nulla di piu'.</summary>
public interface IOpServices
{
    HardwareProbe Probe { get; }
    ParameterOverrides Overrides { get; }
    NvidiaProfileCatalog? NvidiaProfiles { get; }
    ProfileInspector? Inspector { get; }

    /// <summary>Cartella dei backup dei profili GPU.</summary>
    string GpuBackupDir { get; }

    IAdapterReadCache Adapters { get; }

    void Log(string message);
}

/// <summary>
/// Un tipo di operazione, con tutto il suo ciclo di vita in un solo posto.
///
/// Prima queste responsabilita' erano sparse su tre <c>switch</c> paralleli dentro
/// TweakEngine (risoluzione, descrizione, esecuzione) piu' un quarto per il rollback e
/// un elenco separato dei tipi supportati. Bastava dimenticarne uno perche' un tweak
/// venisse mostrato ma non applicato, in silenzio.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class OpHandler
{
    /// <summary>Valore del campo "type" nel catalogo.</summary>
    public abstract string Type { get; }

    /// <summary>
    /// Espande un'operazione nei bersagli concreti presenti su questa macchina.
    /// Il default e' un bersaglio solo: la maggior parte delle operazioni non si moltiplica.
    /// </summary>
    public virtual IEnumerable<ResolvedOp> Resolve(Tweak tweak, TweakOp op, int index, IOpServices svc)
        => new[] { new ResolvedOp(op, index) };

    public abstract ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc);

    /// <summary>
    /// Applica l'operazione. La voce di journal arriva gia' compilata con id, tipo e
    /// modalita' di rollback dichiarata: l'handler riempie bersaglio ed esito, e puo'
    /// declassare lo stato a Skipped o Failed.
    /// </summary>
    public abstract void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc);

    /// <summary>
    /// Annulla l'operazione. Il default non fa nulla: le operazioni irreversibili non
    /// devono fingere il contrario.
    /// </summary>
    /// <returns>true se qualcosa e' stato effettivamente ripristinato.</returns>
    public virtual bool Rollback(SessionEntry entry, IOpServices svc) => false;

    /// <summary>Nomi di valore su cui l'operazione agisce: "names" se presente, altrimenti "name".</summary>
    protected static IEnumerable<string> NamesOf(TweakOp op) =>
        op.Names is { Count: > 0 } ? op.Names : new List<string> { op.Name ?? "" };
}

/// <summary>Elenco degli handler disponibili. Unica fonte di verita' sui tipi supportati.</summary>
[SupportedOSPlatform("windows")]
public static class OpRegistry
{
    private static readonly Dictionary<string, OpHandler> Handlers =
        new OpHandler[]
        {
            new RegOpHandler(),
            new RegTemplateOpHandler(),
            new ServiceOpHandler(),
            new CmdOpHandler(),
            new ProcessKillOpHandler(),
            new ClearDirOpHandler(),
            new PowerPlanOpHandler(),
            new DnsOpHandler(),
            new NetAdapterRscOpHandler(),
            new NetAdapterPropertyOpHandler(),
            new AppxRemoveOpHandler(),
            new UninstallerOpHandler(),
            new WingetOpHandler(),
            new WingetUpgradeAllOpHandler(),
            new StoreUpdateOpHandler(),
            new WindowsUpdateOpHandler(),
            new NvapiProfileOpHandler()
        }.ToDictionary(h => h.Type, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> Types { get; } =
        new HashSet<string>(Handlers.Keys, StringComparer.OrdinalIgnoreCase);

    public static OpHandler? Find(string type) =>
        Handlers.TryGetValue(type, out var handler) ? handler : null;
}
