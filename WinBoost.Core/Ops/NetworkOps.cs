using System.Runtime.Versioning;
using System.Text.Json;

namespace WinBoost.Core;

/// <summary>Operazione che si moltiplica su ogni scheda di rete attiva.</summary>
[SupportedOSPlatform("windows")]
public abstract class AdapterOpHandler : OpHandler
{
    public override IEnumerable<ResolvedOp> Resolve(Tweak tweak, TweakOp op, int index, IOpServices svc)
        => svc.Probe.ActiveAdapters.Select(a =>
            new ResolvedOp(op, index, AdapterName: a.Name, AdapterDescription: a.Description, Label: a.Name));
}

[SupportedOSPlatform("windows")]
public sealed class DnsOpHandler : AdapterOpHandler
{
    public override string Type => "dns";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        var current = NetworkHelper.CaptureDns(r.AdapterDescription ?? "");

        var proposed = ResolveServers(tweak, svc) switch
        {
            null => "(nessuna modifica: provider 'keep')",
            { Count: 0 } => "automatico (DHCP)",
            var servers => string.Join(", ", servers)
        };

        return new($"DNS su {r.AdapterName}",
            current.WasAutomatic ? "automatico (DHCP)" : string.Join(", ", current.Servers),
            proposed);
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = $"DNS su {r.AdapterName}";

        var servers = ResolveServers(tweak, svc);
        if (servers is null)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "provider impostato su 'keep': nessuna modifica";
            entry.RevertMode = "none";
            return;
        }

        entry.DnsBefore = NetworkHelper.CaptureDns(r.AdapterDescription ?? "");
        NetworkHelper.SetDns(r.AdapterDescription ?? "", servers);
        entry.AppliedValue = string.Join(", ", servers);
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.DnsBefore is not { } before) return false;

        NetworkHelper.SetDns(before.InterfaceAlias, before.WasAutomatic ? null : before.Servers);
        return true;
    }

    /// <summary>
    /// Tre stati distinti, e la differenza conta: null significa non toccare la scheda,
    /// lista vuota significa tornare a DHCP (che e' una modifica), lista piena significa
    /// server statici.
    /// </summary>
    internal static List<string>? ResolveServers(Tweak tweak, IOpServices svc)
    {
        if (tweak.Parameters is not { } parameters) return null;
        if (!parameters.TryGetProperty("provider", out var provider)) return null;

        // La scelta dell'utente prevale sul default del catalogo.
        var selected = svc.Overrides.GetChoice(tweak.Id, "provider")
                       ?? (provider.TryGetProperty("default", out var def) ? def.GetString() : "keep");

        if (selected is null or "keep") return null;

        if (!provider.TryGetProperty("choices", out var choices)) return null;
        if (!choices.TryGetProperty(selected, out var choice)) return null;
        if (!choice.TryGetProperty("servers", out var servers)) return null;
        if (servers.ValueKind != JsonValueKind.Array) return null;

        return servers.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetAdapterRscOpHandler : AdapterOpHandler
{
    public override string Type => "netadapter-rsc";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new($"RSC su {r.AdapterName}",
            svc.Adapters.Rsc(r.AdapterName!) ?? "(sconosciuto)",
            (r.Op.Enabled ?? false) ? "abilitato" : "disabilitato");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = $"RSC su {r.AdapterName}";

        entry.AdapterBefore = new AdapterPropertyState
        {
            AdapterName = r.AdapterName!,
            DisplayName = "RSC",
            PreviousValue = NetworkHelper.CaptureRsc(r.AdapterName!)
        };

        var result = NetworkHelper.SetRsc(r.AdapterName!, r.Op.Enabled ?? false);
        if (!result.Success)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "il driver non espone RSC";
            entry.RevertMode = "none";
            return;
        }

        entry.AppliedValue = (r.Op.Enabled ?? false) ? "abilitato" : "disabilitato";
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.AdapterBefore is not { } before) return false;
        if (!bool.TryParse(before.PreviousValue, out var wasEnabled)) return false;

        NetworkHelper.SetRsc(before.AdapterName, wasEnabled);
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetAdapterPropertyOpHandler : AdapterOpHandler
{
    public override string Type => "netadapter-property";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        var match = NetworkHelper.MatchProperty(svc.Adapters.Properties(r.AdapterName!), r.Op.Pattern ?? "*");

        return new($"{r.Op.Pattern} su {r.AdapterName}",
            match is null ? "(proprieta' non esposta dal driver)" : $"{match.Value.Key} = {match.Value.Value}",
            r.Op.Value?.ToString() ?? "?");
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = $"{r.Op.Pattern} su {r.AdapterName}";

        // Lettura diretta, non dalla cache dell'anteprima: qui il valore deve essere quello attuale.
        var (displayName, value) = NetworkHelper.CaptureAdvProperty(r.AdapterName!, r.Op.Pattern ?? "*");
        if (displayName is null)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "proprieta' non esposta dal driver";
            entry.RevertMode = "none";
            return;
        }

        entry.AdapterBefore = new AdapterPropertyState
        {
            AdapterName = r.AdapterName!,
            DisplayName = displayName,
            PreviousValue = value
        };

        var newValue = r.Op.Value?.GetString() ?? "";
        var result = NetworkHelper.SetAdvProperty(r.AdapterName!, displayName, newValue);
        if (!result.Success)
        {
            entry.Status = EntryStatus.Failed;
            entry.Message = result.Combined;
            entry.RevertMode = "none";
            return;
        }

        entry.AppliedValue = newValue;
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.AdapterBefore is not { PreviousValue: not null } before) return false;

        NetworkHelper.SetAdvProperty(before.AdapterName, before.DisplayName, before.PreviousValue);
        return true;
    }
}
