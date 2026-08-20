using System.Management;
using System.Runtime.Versioning;

namespace WinBoost.Core;

/// <summary>
/// Operazioni di rete. Il DNS passa da WMI (API stabile e tipizzata); le proprieta'
/// avanzate della scheda usano i cmdlet NetAdapter, che sono l'unica superficie
/// documentata e stabile per quei driver.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NetworkHelper
{
    // ---------- DNS ----------

    public static DnsState CaptureDns(string adapterDescription)
    {
        var state = new DnsState { InterfaceAlias = adapterDescription, WasAutomatic = true };
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DNSServerSearchOrder, IPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                if (!string.Equals(mo["Description"]?.ToString(), adapterDescription, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (mo["DNSServerSearchOrder"] is string[] servers && servers.Length > 0)
                {
                    state.Servers.AddRange(servers);
                    state.WasAutomatic = false;
                }
                break;
            }
        }
        catch (ManagementException) { }
        return state;
    }

    /// <summary>servers vuoto o null = ritorno a DHCP.</summary>
    public static void SetDns(string adapterDescription, IReadOnlyList<string>? servers)
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Description, IPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");

        foreach (var o in searcher.Get())
        {
            using var mo = (ManagementObject)o;
            if (!string.Equals(mo["Description"]?.ToString(), adapterDescription, StringComparison.OrdinalIgnoreCase))
                continue;

            using var parameters = mo.GetMethodParameters("SetDNSServerSearchOrder");
            parameters["DNSServerSearchOrder"] = servers is null || servers.Count == 0
                ? null
                : servers.ToArray();

            using var result = mo.InvokeMethod("SetDNSServerSearchOrder", parameters, null);
            var code = Convert.ToUInt32(result["ReturnValue"]);
            if (code != 0 && code != 1)   // 1 = riuscito, richiede riavvio
                throw new InvalidOperationException($"SetDNSServerSearchOrder ha restituito {code} su '{adapterDescription}'.");
            return;
        }

        throw new InvalidOperationException($"Scheda di rete non trovata o senza IP: '{adapterDescription}'.");
    }

    // ---------- proprieta' avanzate della scheda ----------

    private const string CimNamespace = @"\\.\root\StandardCimv2";

    /// <summary>
    /// Legge le proprieta' avanzate di tutte le schede con una sola query CIM.
    ///
    /// La lettura non passa piu' dai cmdlet NetAdapter: ogni invocazione di
    /// powershell.exe costa qualche secondo, e l'anteprima ne faceva una per proprieta'
    /// per scheda. Le scritture restano sui cmdlet, dove il costo e' irrilevante e la
    /// semantica di riavvio del driver e' gestita per noi.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> CaptureAllAdvProperties()
    {
        var byAdapter = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var scope = new ManagementScope(CimNamespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT Name, DisplayName, DisplayValue FROM MSFT_NetAdapterAdvancedPropertySettingData"));

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var adapter = mo["Name"]?.ToString();
                var display = mo["DisplayName"]?.ToString();
                if (string.IsNullOrEmpty(adapter) || string.IsNullOrEmpty(display)) continue;

                if (!byAdapter.TryGetValue(adapter, out var props))
                    byAdapter[adapter] = props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                props[display] = mo["DisplayValue"]?.ToString() ?? "";
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return byAdapter.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stato RSC IPv4 di tutte le schede, con una sola query CIM.</summary>
    public static IReadOnlyDictionary<string, string> CaptureAllRsc()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var scope = new ManagementScope(CimNamespace);
            scope.Connect();

            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT Name, IPv4Enabled FROM MSFT_NetAdapterRscSettingData"));

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var adapter = mo["Name"]?.ToString();
                if (string.IsNullOrEmpty(adapter)) continue;

                result[adapter] = mo["IPv4Enabled"]?.ToString() ?? "";
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }

        return result;
    }

    /// <summary>Applica un pattern con jolly nello stile di -like di PowerShell.</summary>
    public static KeyValuePair<string, string>? MatchProperty(
        IReadOnlyDictionary<string, string> properties, string pattern)
    {
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$";

        foreach (var kv in properties)
            if (System.Text.RegularExpressions.Regex.IsMatch(kv.Key, regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return kv;

        return null;
    }

    public static ProcessResult SetRsc(string adapterName, bool enabled)
    {
        var cmdlet = enabled ? "Enable-NetAdapterRsc" : "Disable-NetAdapterRsc";
        return RunPs($"{cmdlet} -Name '{Escape(adapterName)}' -ErrorAction Stop");
    }

    public static (string? DisplayName, string? Value) CaptureAdvProperty(string adapterName, string pattern)
    {
        if (!CaptureAllAdvProperties().TryGetValue(adapterName, out var props)) return (null, null);

        var match = MatchProperty(props, pattern);
        return match is null ? (null, null) : (match.Value.Key, match.Value.Value);
    }

    public static string? CaptureRsc(string adapterName) =>
        CaptureAllRsc().TryGetValue(adapterName, out var value) ? value : null;

    public static ProcessResult SetAdvProperty(string adapterName, string displayName, string displayValue) =>
        RunPs($"Set-NetAdapterAdvancedProperty -Name '{Escape(adapterName)}' " +
              $"-DisplayName '{Escape(displayName)}' -DisplayValue '{Escape(displayValue)}' " +
              "-NoRestart -ErrorAction Stop");

    private static ProcessResult RunPs(string command) =>
        ProcessRunner.Run("powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
            timeoutSeconds: 60);

    /// <summary>Raddoppia gli apici singoli: dentro una stringa PowerShell letterale e' l'unico escape necessario.</summary>
    private static string Escape(string s) => s.Replace("'", "''");
}

[SupportedOSPlatform("windows")]
public static class PowerPlanHelper
{
    public static string? GetActiveSchemeGuid()
    {
        var r = ProcessRunner.Run("powercfg.exe", new[] { "/getactivescheme" }, timeoutSeconds: 20);
        if (!r.Success) return null;
        var m = System.Text.RegularExpressions.Regex.Match(r.StdOut, "([a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12})");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static bool SchemeExists(string guid)
    {
        var r = ProcessRunner.Run("powercfg.exe", new[] { "/list" }, timeoutSeconds: 20);
        return r.Success && r.StdOut.Contains(guid, StringComparison.OrdinalIgnoreCase);
    }

    public static ProcessResult Activate(string guid) =>
        ProcessRunner.Run("powercfg.exe", new[] { "/setactive", guid }, timeoutSeconds: 20);

    public static ProcessResult Duplicate(string sourceGuid, string targetGuid) =>
        ProcessRunner.Run("powercfg.exe", new[] { "-duplicatescheme", sourceGuid, targetGuid }, timeoutSeconds: 20);

    public static string DescribeActive()
    {
        var r = ProcessRunner.Run("powercfg.exe", new[] { "/getactivescheme" }, timeoutSeconds: 20);
        return r.Success ? r.StdOut.Trim() : "(sconosciuto)";
    }
}
