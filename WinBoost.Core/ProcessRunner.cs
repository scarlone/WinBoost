using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace WinBoost.Core;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => ExitCode == 0 && !TimedOut;
    public string Combined => string.Join(Environment.NewLine,
        new[] { StdOut, StdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class ProcessRunner
{
    /// <summary>
    /// Esegue un eseguibile raccogliendo stdout/stderr. Nessuna shell di mezzo:
    /// gli argomenti sono passati come lista, quindi non esiste quoting da sbagliare.
    /// </summary>
    public static ProcessResult Run(
        string exe,
        IEnumerable<string> args,
        int timeoutSeconds = 120,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExpandExe(exe),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.SystemDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(Environment.ExpandEnvironmentVariables(a));

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutSeconds * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: true);
        }

        // Attende lo svuotamento dei buffer asincroni dopo l'uscita del processo.
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), TimedOut: false);
    }

    private static string ExpandExe(string exe)
    {
        var expanded = Environment.ExpandEnvironmentVariables(exe);
        // Gli eseguibili di sistema vanno risolti esplicitamente in System32:
        // su un processo a 32 bit il PATH potrebbe puntare a SysWOW64.
        if (!Path.IsPathRooted(expanded))
        {
            var system32 = Path.Combine(Environment.SystemDirectory, expanded);
            if (File.Exists(system32)) return system32;
        }
        return expanded;
    }
}

/// <summary>
/// Riavvio dei processi di shell. Molti tweak di Explorer scrivono nel registro ma
/// diventano visibili solo quando il processo rilegge le impostazioni: senza questo
/// passo l'utente applica la modifica e non vede accadere nulla fino al logout.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellRestarter
{
    /// <summary>Nomi riconosciuti nel campo "restart" del catalogo.</summary>
    public static bool IsKnownTarget(string target) =>
        target.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    public static string Restart(string target)
    {
        if (!IsKnownTarget(target))
            return $"bersaglio di riavvio sconosciuto: '{target}'";

        return RestartExplorer();
    }

    private static string RestartExplorer()
    {
        var running = System.Diagnostics.Process.GetProcessesByName("explorer");
        if (running.Length == 0)
        {
            foreach (var p in running) p.Dispose();
            return "Explorer non era in esecuzione";
        }

        foreach (var p in running)
        {
            try { p.Kill(); p.WaitForExit(5000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }

        // Windows di norma rilancia la shell da solo. Diamogli un momento e, se non
        // succede, la riavviamo noi: lasciare l'utente senza desktop non e' accettabile.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            System.Threading.Thread.Sleep(500);
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length > 0)
                return "Explorer riavviato";
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = true
            })?.Dispose();
            return "Explorer riavviato manualmente";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return $"Explorer non riavviato: {ex.Message}";
        }
    }
}

[SupportedOSPlatform("windows")]
public static class ServiceHelper
{
    private const string ServicesRoot = @"HKLM:\SYSTEM\CurrentControlSet\Services";

    /// <param name="includeRunningState">
    /// Interrogare lo stato di esecuzione costa un processo <c>sc.exe</c> per servizio.
    /// L'anteprima non ne ha bisogno e con tredici servizi la differenza si vede.
    /// </param>
    public static ServiceState Capture(string name, bool includeRunningState = true)
    {
        var state = new ServiceState { Name = name };
        var path = $@"{ServicesRoot}\{name}";
        if (!RegistryHelper.KeyExists(path)) return state;

        state.Existed = true;
        var start = RegistryHelper.Capture(path, "Start");
        state.StartType = start.ValueExists ? StartCodeToName(start.Value) : null;
        state.WasRunning = includeRunningState && IsRunning(name);
        return state;
    }

    public static bool IsRunning(string name)
    {
        var result = ProcessRunner.Run("sc.exe", new[] { "query", name }, timeoutSeconds: 15);
        return result.StdOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessResult SetStartup(string name, string startup)
    {
        var scValue = startup.ToLowerInvariant() switch
        {
            "automatic" or "auto" => "auto",
            "manual" or "demand" => "demand",
            "disabled" => "disabled",
            "delayed" => "delayed-auto",
            _ => throw new ArgumentException($"Tipo di avvio non riconosciuto: {startup}")
        };
        // sc.exe richiede lo spazio dopo "start=" come token separato.
        return ProcessRunner.Run("sc.exe", new[] { "config", name, "start=", scValue }, timeoutSeconds: 30);
    }

    public static ProcessResult Stop(string name) =>
        ProcessRunner.Run("sc.exe", new[] { "stop", name }, timeoutSeconds: 60);

    public static ProcessResult Start(string name) =>
        ProcessRunner.Run("sc.exe", new[] { "start", name }, timeoutSeconds: 60);

    private static string StartCodeToName(string? code) => code switch
    {
        "0" => "boot",
        "1" => "system",
        "2" => "automatic",
        "3" => "manual",
        "4" => "disabled",
        _ => "manual"
    };
}
