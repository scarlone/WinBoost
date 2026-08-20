using System.Runtime.Versioning;

namespace WinBoost.Core;

[SupportedOSPlatform("windows")]
public sealed class ServiceOpHandler : OpHandler
{
    public override string Type => "service";

    public override IEnumerable<ResolvedOp> Resolve(Tweak tweak, TweakOp op, int index, IOpServices svc)
    {
        foreach (var name in op.Names ?? new List<string> { op.Name ?? "" })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            yield return new ResolvedOp(op, index, Name: name, Label: name);
        }
    }

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        // Lo stato di esecuzione costa un sc.exe per servizio e all'anteprima non serve.
        var state = ServiceHelper.Capture(r.Name!, includeRunningState: false);

        return new($"servizio {r.Name}",
            state.Existed ? state.StartType ?? "?" : "(non installato)",
            r.Op.Startup ?? "?");
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        entry.Target = $"servizio {r.Name}";

        var before = ServiceHelper.Capture(r.Name!);
        entry.ServiceBefore = before;

        if (!before.Existed)
        {
            entry.Status = EntryStatus.Skipped;
            entry.Message = "servizio non installato";
            entry.RevertMode = "none";
            return;
        }

        var result = ServiceHelper.SetStartup(r.Name!, r.Op.Startup ?? "manual");
        if (!result.Success)
        {
            entry.Status = EntryStatus.Failed;
            entry.Message = result.Combined;
            entry.RevertMode = "none";
            return;
        }

        if (r.Op.Stop && before.WasRunning) ServiceHelper.Stop(r.Name!);
        entry.AppliedValue = r.Op.Startup;
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.ServiceBefore is not { Existed: true } before) return false;

        if (before.StartType is not null) ServiceHelper.SetStartup(before.Name, before.StartType);

        // Ripristina anche lo stato di esecuzione: fermare un servizio e lasciarlo
        // fermo non sarebbe un annullamento.
        if (before.WasRunning && !ServiceHelper.IsRunning(before.Name)) ServiceHelper.Start(before.Name);

        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class CmdOpHandler : OpHandler
{
    public override string Type => "cmd";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new($"{r.Op.Exe} {string.Join(' ', r.Op.Args ?? new List<string>())}",
            "(comando esterno)",
            "esecuzione");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var op = r.Op;
        entry.Target = $"{op.Exe} {string.Join(' ', op.Args ?? new List<string>())}";

        var args = (op.Args ?? new List<string>())
            .Select(a => r.AdapterName is null
                ? a
                : a.Replace("{AdapterName}", r.AdapterName, StringComparison.Ordinal));

        var result = ProcessRunner.Run(op.Exe!, args);

        if (!result.Success && !op.ContinueOnError)
        {
            entry.Status = EntryStatus.Failed;
            entry.Message = result.TimedOut ? "timeout" : result.Combined;
            entry.RevertMode = "none";
            return;
        }

        entry.RevertExe = op.RevertExe ?? op.Exe;
        entry.RevertArgs = op.RevertArgs;

        // Senza un comando inverso esplicito non c'e' modo di annullare: dirlo, non fingerlo.
        if (op.RevertArgs is null) entry.RevertMode = "none";

        entry.AppliedValue = "eseguito";
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.RevertArgs is not { Count: > 0 }) return false;

        ProcessRunner.Run(entry.RevertExe ?? "cmd.exe", entry.RevertArgs);
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProcessKillOpHandler : OpHandler
{
    public override string Type => "process-kill";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new(string.Join(", ", r.Op.Names ?? new List<string>()),
            "(processi in esecuzione)",
            "terminazione");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var names = r.Op.Names ?? new List<string>();
        entry.Target = string.Join(", ", names);
        entry.RevertMode = "none";

        var killed = 0;
        foreach (var name in names)
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                try { process.Kill(); killed++; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                finally { process.Dispose(); }
            }

        entry.AppliedValue = $"{killed} processi terminati";
    }
}

[SupportedOSPlatform("windows")]
public sealed class ClearDirOpHandler : OpHandler
{
    public override string Type => "clear-dir";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc)
    {
        var dir = Environment.ExpandEnvironmentVariables(r.Op.Path ?? "");
        return new(dir, Directory.Exists(dir) ? "(presente)" : "(assente)", "svuotamento contenuto");
    }

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var dir = Environment.ExpandEnvironmentVariables(r.Op.Path ?? "");
        entry.Target = dir;
        entry.RevertMode = "none";
        entry.AppliedValue = Clear(dir);
    }

    private static string Clear(string dir)
    {
        if (!Directory.Exists(dir)) return "(cartella assente)";

        var removed = 0;
        var skipped = 0;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            try { Directory.Delete(sub, recursive: true); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }

        return $"{removed} elementi rimossi, {skipped} in uso";
    }
}

[SupportedOSPlatform("windows")]
public sealed class PowerPlanOpHandler : OpHandler
{
    public override string Type => "powerplan";

    public override ChangeDescription Describe(Tweak tweak, ResolvedOp r, IOpServices svc) =>
        new("piano energetico attivo",
            PowerPlanHelper.DescribeActive(),
            $"duplica e attiva {r.Op.SourceGuid}");

    public override void Execute(Tweak tweak, ResolvedOp r, SessionEntry entry, IOpServices svc)
    {
        var op = r.Op;
        entry.Target = "piano energetico";
        entry.PowerPlanBefore = PowerPlanHelper.GetActiveSchemeGuid();

        var target = op.SourceGuid!;
        if (!PowerPlanHelper.SchemeExists(target))
            PowerPlanHelper.Duplicate(op.SourceGuid!, target);

        var result = PowerPlanHelper.Activate(target);
        if (!result.Success && op.FallbackGuid is not null)
            result = PowerPlanHelper.Activate(op.FallbackGuid);

        if (!result.Success)
        {
            entry.Status = EntryStatus.Failed;
            entry.Message = result.Combined;
            entry.RevertMode = "none";
            return;
        }

        entry.AppliedValue = target;
    }

    public override bool Rollback(SessionEntry entry, IOpServices svc)
    {
        if (entry.PowerPlanBefore is null) return false;

        PowerPlanHelper.Activate(entry.PowerPlanBefore);
        return true;
    }
}
