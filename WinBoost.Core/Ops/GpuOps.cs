using System.Runtime.Versioning;

namespace WinBoost.Core;

/// <summary>
/// Applica un profilo globale del pannello NVIDIA generando un file .nip e facendolo
/// importare da NVIDIA Profile Inspector, che non viene distribuito con WinBoost.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NvapiProfileOpHandler : OpHandler
{
    public override string Type => "nvapi-profile";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        var profile = svc.NvidiaProfiles?.Find(r.Op.Profile ?? "");

        return new($"profilo NVIDIA globale ({r.Op.Profile})",
            svc.Inspector is { IsAvailable: true }
                ? "(impostazioni attuali del driver)"
                : "(NVIDIA Profile Inspector non disponibile)",
            profile is null
                ? $"profilo '{r.Op.Profile}' non trovato nel catalogo NVIDIA"
                : $"{profile.Settings.Count} impostazioni sul profilo '{profile.ProfileName}'");
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = $"profilo NVIDIA globale ({r.Op.Profile})";

        if (svc.Inspector is not { IsAvailable: true } inspector)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = ProfileInspector.DownloadHint;
            entry.RevertMode = "none";
            return;
        }

        var profile = svc.NvidiaProfiles?.Find(r.Op.Profile ?? "");
        if (profile is null)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = $"profilo '{r.Op.Profile}' assente dal catalogo NVIDIA";
            entry.RevertMode = "none";
            return;
        }

        Directory.CreateDirectory(svc.GpuBackupDir);

        // Il backup precede l'importazione: senza, il rollback non esiste.
        entry.BackupFilePath = inspector.ExportCurrent(svc.GpuBackupDir);
        if (entry.BackupFilePath is null)
        {
            entry.RevertMode = "none";
            entry.RevertNote = "Impossibile esportare le impostazioni precedenti: "
                             + "usa 'Ripristina impostazioni predefinite' nel Pannello di controllo NVIDIA.";
        }

        var nipPath = Path.Combine(svc.GpuBackupDir, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.nip");
        NipWriter.Write(profile, nipPath);

        var import = inspector.Import(nipPath);
        if (!import.Success)
        {
            entry.Status = EntryStatus.Failed;
            entry.Message = import.Message;
            entry.RevertMode = "none";
            return;
        }

        entry.AppliedValue = $"{profile.Settings.Count} impostazioni su '{profile.ProfileName}'";
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.BackupFilePath is null) return false;
        if (svc.Inspector is not { IsAvailable: true } inspector) return false;

        var result = inspector.Import(entry.BackupFilePath);
        if (result.Success) return true;

        entry.Message = $"rollback fallito: {result.Message}";
        return false;
    }
}
