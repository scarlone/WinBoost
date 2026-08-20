using System.Runtime.Versioning;
using System.Text.Json;

namespace WinBoost.Core;

[SupportedOSPlatform("windows")]
public sealed class AppxRemoveOpHandler : OpHandler
{
    public override string Type => "appx-remove";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        var packages = ResolvePackages(tweak, svc);

        return new("pacchetti Appx",
            "(installati)",
            packages.Count == 0 ? "(nessun pacchetto selezionato)" : string.Join(", ", packages));
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var packages = ResolvePackages(tweak, svc);

        entry.Target = string.Join(", ", packages);
        entry.RevertMode = "none";
        entry.RevertNote = "Reinstallazione manuale dal Microsoft Store.";

        if (packages.Count == 0)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "nessun pacchetto selezionato";
            return;
        }

        var removed = new List<string>();
        foreach (var package in packages)
        {
            var result = ProcessRunner.Run("powershell.exe", new[]
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                $"Get-AppxPackage -Name '{package}' -AllUsers -ErrorAction SilentlyContinue | " +
                "Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue"
            }, timeoutSeconds: 120);

            if (result.Success) removed.Add(package);
        }

        entry.AppliedValue = $"{removed.Count}/{packages.Count} rimossi";
    }

    internal static List<string> ResolvePackages(Tweak tweak, IOpServices svc)
    {
        if (svc.Overrides.GetList(tweak.Id, "packages") is { } chosen) return chosen.ToList();

        var result = new List<string>();
        if (tweak.Parameters is not { } parameters) return result;
        if (!parameters.TryGetProperty("packages", out var packages)) return result;
        if (!packages.TryGetProperty("default", out var def) || def.ValueKind != JsonValueKind.Array) return result;

        result.AddRange(def.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0));
        return result;
    }
}

[SupportedOSPlatform("windows")]
public sealed class UninstallerOpHandler : OpHandler
{
    public override string Type => "uninstaller";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new(r.Op.Discover ?? string.Join(", ", r.Op.Candidates ?? new List<string>()),
            "(installato)",
            "disinstallazione");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var op = r.Op;
        entry.RevertMode = "none";

        var exe = Find(op);
        if (exe is null)
        {
            entry.Target = op.Discover ?? string.Join(", ", op.Candidates ?? new List<string>());
            entry.Status = EntryStatus.Skipped;
            entry.Message = "disinstallatore non trovato";
            return;
        }

        entry.Target = exe;

        foreach (var name in op.KillFirst ?? new List<string>())
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try { process.Kill(); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }

        var result = ProcessRunner.Run(exe, op.Args ?? new List<string>(), timeoutSeconds: 300);
        entry.AppliedValue = $"exit {result.ExitCode}";
    }

    private static string? Find(TweakOp op)
    {
        foreach (var candidate in op.Candidates ?? new List<string>())
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (File.Exists(expanded)) return expanded;
        }

        if (op.Discover is null) return null;

        // Pattern del tipo "base\**\setup.exe": separiamo la radice dal nome file.
        var pattern = Environment.ExpandEnvironmentVariables(op.Discover);
        var marker = pattern.IndexOf("**", StringComparison.Ordinal);
        if (marker < 0) return File.Exists(pattern) ? pattern : null;

        var root = pattern[..marker].TrimEnd('\\', '/');
        var fileName = Path.GetFileName(pattern);
        if (!Directory.Exists(root)) return null;

        try
        {
            // Il piu' recente: le versioni stanno in sottocartelle numerate.
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderByDescending(f => f).FirstOrDefault();
        }
        catch (UnauthorizedAccessException) { return null; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WingetOpHandler : OpHandler
{
    public override string Type => "winget";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new(r.Op.Id ?? "?", "(stato sconosciuto)", "installazione via winget");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = r.Op.Id ?? "?";
        entry.RevertMode = "none";

        var result = ProcessRunner.Run("winget.exe", new[]
        {
            "install", "--id", r.Op.Id!, "--exact", "--silent",
            "--accept-package-agreements", "--accept-source-agreements"
        }, timeoutSeconds: 600);

        if (!result.Success)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = result.TimedOut ? "timeout" : "winget non disponibile o pacchetto gia' presente";
        }

        entry.AppliedValue = $"exit {result.ExitCode}";
    }
}

[SupportedOSPlatform("windows")]
public sealed class WingetUpgradeAllOpHandler : OpHandler
{
    public override string Type => "winget-upgrade-all";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new("winget upgrade --all", "(stato sconosciuto)", "aggiornamento di tutti i pacchetti");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = "winget upgrade --all";
        entry.RevertMode = "none";

        var result = ProcessRunner.Run("winget.exe", new[]
        {
            "upgrade", "--all", "--silent",
            "--accept-package-agreements", "--accept-source-agreements"
        }, timeoutSeconds: 1800);

        entry.AppliedValue = $"exit {result.ExitCode}";

        if (!result.Success)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "winget non disponibile";
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class StoreUpdateOpHandler : OpHandler
{
    public override string Type => "store-update";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new("app Microsoft Store", "(stato sconosciuto)", "avvia scansione aggiornamenti");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = "app Microsoft Store";
        entry.RevertMode = "none";

        // Provider MDM: e' l'unica superficie documentata per far partire la scansione
        // senza portare l'app Store in primo piano.
        var result = ProcessRunner.Run("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "$c = Get-CimInstance -Namespace root\\cimv2\\mdm\\dmmap " +
            "-ClassName MDM_EnterpriseModernAppManagement_AppManagement01 -ErrorAction Stop; " +
            "($c | Invoke-CimMethod -MethodName UpdateScanMethod -ErrorAction Stop).ReturnValue"
        }, timeoutSeconds: 180);

        if (!result.Success)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = result.TimedOut ? "timeout della scansione Store" : "provider MDM non disponibile";
            return;
        }

        entry.AppliedValue = "scansione aggiornamenti avviata";
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateOpHandler : OpHandler
{
    public override string Type => "windows-update";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new("Windows Update", "(stato sconosciuto)", "cerca aggiornamenti disponibili (nessuna installazione)");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = "Windows Update";
        entry.RevertMode = "none";

        // Deliberatamente solo ricerca: installare aggiornamenti senza sorveglianza,
        // con i riavvii che comportano, non e' una decisione che questo strumento
        // debba prendere al posto dell'utente.
        var result = ProcessRunner.Run("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "$s = New-Object -ComObject Microsoft.Update.Session; " +
            "$r = $s.CreateUpdateSearcher().Search('IsInstalled=0 AND IsHidden=0'); " +
            "$r.Updates.Count; $r.Updates | ForEach-Object { $_.Title }"
        }, timeoutSeconds: 300);

        if (!result.Success)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = result.TimedOut ? "timeout della ricerca aggiornamenti" : result.Combined;
            return;
        }

        var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        var count = lines.Count > 0 ? lines[0] : "0";

        entry.AppliedValue = count == "0"
            ? "nessun aggiornamento in sospeso"
            : $"{count} aggiornamenti disponibili: {string.Join("; ", lines.Skip(1).Take(5))}";
    }
}
