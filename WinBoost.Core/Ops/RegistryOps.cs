using System.Runtime.Versioning;

namespace WinBoost.Core;

/// <summary>
/// Scrittura di un valore di registro. Descrizione, esecuzione e rollback sono comuni
/// a "reg" e "reg-template": cambia solo come si arriva ai bersagli concreti.
/// </summary>
[SupportedOSPlatform("windows")]
public abstract class RegistryOpHandlerBase : OpHandler
{
    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new($@"{r.Path}\{r.Name}",
            RegistryHelper.Describe(RegistryHelper.Capture(r.Path!, r.Name!)),
            r.Op.Value?.ToString() ?? "(nessun valore)");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var op = r.Op;

        entry.Target = r.Path!;
        entry.ValueName = r.Name;
        entry.RevertKey = op.RevertKey;

        // Lo snapshot precede la scrittura: e' l'unica cosa che rende il rollback possibile.
        entry.RegistryBefore = RegistryHelper.Capture(r.Path!, r.Name!);

        var value = RegistryHelper.Coerce(op.Value!.Value, op.ValueType ?? "String");
        RegistryHelper.Write(r.Path!, r.Name!, op.ValueType ?? "String", value);

        entry.AppliedValue = op.Value?.ToString();
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (string.Equals(entry.RevertMode, "delete-key", StringComparison.OrdinalIgnoreCase)
            && entry.RevertKey is not null)
        {
            RegistryHelper.DeleteKeyTree(entry.RevertKey);
            return true;
        }

        if (entry.RegistryBefore is null) return false;

        RegistryHelper.Restore(entry.Target, entry.ValueName ?? "", entry.RegistryBefore);
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegOpHandler : RegistryOpHandlerBase
{
    public override string Type => "reg";

    public override IEnumerable<ResolvedOp> Resolve(Tweak tweak, TweakOp op, int index, IOpServices svc)
    {
        // Una chiave assente puo' essere normale (driver non installato): il tweak
        // dichiara se saltare invece di creare rami di registro inutili.
        if (op.SkipIfKeyMissing && !RegistryHelper.KeyExists(op.Path!)) yield break;

        foreach (var name in NamesOf(op))
        {
            if (op.SkipIfValueMissing && !RegistryHelper.Capture(op.Path!, name).ValueExists) continue;
            yield return new ResolvedOp(op, index, op.Path, name);
        }
    }
}

/// <summary>
/// Percorso con segnaposto, risolto sull'hardware reale. Il tipo di espansione e'
/// dichiarato dal tweak nel campo "dynamic".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegTemplateOpHandler : RegistryOpHandlerBase
{
    public override string Type => "reg-template";

    public override IEnumerable<ResolvedOp> Resolve(Tweak tweak, TweakOp op, int index, IOpServices svc)
    {
        switch (tweak.Dynamic)
        {
            case "gpu-device-enum":
                foreach (var gpu in svc.Probe.Gpus.Where(g => g.IsActive))
                {
                    // Solo la GPU del vendore richiesto, se il tweak ne dichiara uno.
                    if (!string.IsNullOrWhiteSpace(tweak.Vendor) &&
                        !string.Equals(gpu.Vendor, tweak.Vendor, StringComparison.OrdinalIgnoreCase)) continue;

                    var path = op.Path!.Replace("{PNPDeviceID}", gpu.PnpDeviceId, StringComparison.Ordinal);
                    foreach (var name in NamesOf(op))
                        yield return new ResolvedOp(op, index, path, name, Label: gpu.Name);
                }
                break;

            case "amd-driver-key-enum":
                foreach (var key in HardwareProbe.ProbeAmdDriverKeys())
                    foreach (var name in key.UlpsValueNames)
                        yield return new ResolvedOp(op, index, key.RegistryPath, name, Label: key.DriverDesc);
                break;

            case "netadapter-guid-enum":
                foreach (var adapter in svc.Probe.ActiveAdapters)
                {
                    var path = op.Path!.Replace("{InterfaceGuid}", adapter.Guid, StringComparison.Ordinal);
                    foreach (var name in NamesOf(op))
                        yield return new ResolvedOp(op, index, path, name,
                            AdapterName: adapter.Name, Label: adapter.Name);
                }
                break;

            default:
                svc.Log($"[{tweak.Id}] espansione dinamica sconosciuta: '{tweak.Dynamic}', operazione ignorata.");
                break;
        }
    }
}
