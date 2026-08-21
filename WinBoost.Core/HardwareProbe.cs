using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinBoost.Core;

public sealed record GpuDevice(string Name, string Vendor, string PnpDeviceId, bool IsActive, string State);

public sealed record NetAdapterInfo(string Name, string Guid, string Description);

/// <summary>Chiave driver AMD sotto la classe Display, con i valori ULPS presenti.</summary>
public sealed record AmdDriverKey(string RegistryPath, string DriverDesc, List<string> UlpsValueNames);

/// <summary>
/// Rilevazione hardware. Ogni sonda e' difensiva: un errore WMI non deve impedire
/// l'uso del resto dell'applicazione, quindi restituisce lista vuota / null.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareProbe
{
    private const string DisplayClassKey =
        @"HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private List<GpuDevice>? _gpus;
    private List<NetAdapterInfo>? _adapters;
    private bool? _systemDriveIsSsd;

    public IReadOnlyList<GpuDevice> Gpus => _gpus ??= ProbeGpus();
    public IReadOnlyList<NetAdapterInfo> ActiveAdapters => _adapters ??= ProbeAdapters();
    public bool SystemDriveIsSsd => _systemDriveIsSsd ??= ProbeSystemDriveIsSsd();

    public bool HasVendor(string vendor) =>
        Gpus.Any(g => string.Equals(g.Vendor, vendor, StringComparison.OrdinalIgnoreCase));

    public static string ClassifyVendor(string name)
    {
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "nvidia";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ATI ", StringComparison.OrdinalIgnoreCase)) return "amd";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel";
        return "unknown";
    }

    private static List<GpuDevice> ProbeGpus()
    {
        var list = new List<GpuDevice>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_VideoController");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var name = mo["Name"]?.ToString() ?? "GPU sconosciuta";
                var pnp = mo["PNPDeviceID"]?.ToString() ?? "";

                // Solo dispositivi PCI: gli adattatori virtuali (RDP, Meta, ecc.)
                // non hanno chiavi Interrupt Management valide.
                if (!pnp.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)) continue;

                var code = mo["ConfigManagerErrorCode"] is null
                    ? -1
                    : Convert.ToInt32(mo["ConfigManagerErrorCode"]);
                var active = code == 0;
                var state = code switch
                {
                    0 => "attiva",
                    22 => "disabilitata",
                    -1 => "stato sconosciuto",
                    _ => $"errore driver {code}"
                };

                list.Add(new GpuDevice(name, ClassifyVendor(name), pnp, active, state));
            }
        }
        catch (ManagementException) { /* WMI non disponibile: nessuna GPU rilevata */ }
        catch (UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>
    /// Le schede si enumerano da MSFT_NetAdapter, cioe' dalla stessa sorgente su cui
    /// lavorano i cmdlet NetAdapter usati dalle scritture: i nomi che restituisce sono
    /// esattamente quelli che il percorso di applicazione sa indirizzare.
    ///
    /// NetworkInterface.GetAllNetworkInterfaces() sembrava equivalente e non lo e':
    /// espone anche un'istanza per ogni filtro NDIS legato a ogni scheda
    /// ("Ethernet-QoS Packet Scheduler-0000", "Ethernet-WFP Native MAC Layer
    /// LightWeight Filter-0000", ...), che condividono MAC e velocita' della scheda
    /// sottostante ma non sono schede. Su una macchina reale con VPN e Hyper-V si
    /// passava da 3 schede vere a 24 voci, e ogni tweak di rete veniva risolto su
    /// tutte: anteprima illeggibile e scritture tentate contro oggetti inesistenti.
    /// </summary>
    private static List<NetAdapterInfo> ProbeAdapters()
    {
        try { return ProbeAdaptersViaCim(); }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        return ProbeAdaptersViaDotNet();
    }

    private static List<NetAdapterInfo> ProbeAdaptersViaCim()
    {
        var list = new List<NetAdapterInfo>();

        var scope = new ManagementScope(@"\\.\root\StandardCimv2");
        scope.Connect();

        // InterfaceOperationalStatus 1 = Up, l'equivalente di "Status: Up" in Get-NetAdapter.
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(
            "SELECT Name, InterfaceGuid, InterfaceDescription FROM MSFT_NetAdapter "
            + "WHERE InterfaceOperationalStatus = 1"));

        foreach (var o in searcher.Get())
        {
            using var mo = (ManagementObject)o;
            var name = mo["Name"] as string;
            if (string.IsNullOrWhiteSpace(name)) continue;

            list.Add(new NetAdapterInfo(
                name,
                mo["InterfaceGuid"] as string ?? "",
                mo["InterfaceDescription"] as string ?? ""));
        }

        return list;
    }

    /// <summary>
    /// Ripiego se CIM non risponde. Applica comunque il filtro sulle istanze dei
    /// filtri NDIS: senza, sarebbe una botola che riapre il difetto.
    /// </summary>
    private static List<NetAdapterInfo> ProbeAdaptersViaDotNet()
    {
        var list = new List<NetAdapterInfo>();
        try
        {
            var candidate = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => ni.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToList();

            var names = candidate.Select(ni => ni.Name).ToList();

            foreach (var ni in candidate.Where(ni => !IsNdisFilterInstance(ni.Name, names)))
                list.Add(new NetAdapterInfo(ni.Name, ni.Id, ni.Description));
        }
        catch (NetworkInformationException) { }
        return list;
    }

    /// <summary>
    /// Un'istanza di filtro NDIS si chiama "&lt;scheda&gt;-&lt;nome filtro&gt;-0000": nome di una
    /// scheda presente nell'elenco, un trattino, il filtro, e un indice a quattro cifre.
    /// Si pretende che il prefisso corrisponda a una scheda vera, non solo che il nome
    /// finisca con quattro cifre: una scheda potrebbe legittimamente chiamarsi cosi'.
    /// </summary>
    public static bool IsNdisFilterInstance(string name, IEnumerable<string> allNames)
    {
        if (!FilterSuffix.IsMatch(name)) return false;

        return allNames.Any(other => other.Length < name.Length
            && !string.Equals(other, name, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(other + "-", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex FilterSuffix = new(@"-\d{4}$", RegexOptions.Compiled);

    private static bool ProbeSystemDriveIsSsd()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT MediaType, DeviceId FROM MSFT_PhysicalDisk"));

            // MediaType: 3 = HDD, 4 = SSD, 5 = SCM, 0 = sconosciuto.
            // In presenza di dischi misti consideriamo SSD solo se lo sono tutti,
            // perche' non sappiamo con certezza dove risieda il volume di sistema.
            var types = searcher.Get().Cast<ManagementObject>()
                .Select(mo => { using (mo) return mo["MediaType"] is null ? 0 : Convert.ToInt32(mo["MediaType"]); })
                .Where(t => t != 0)
                .ToList();

            return types.Count > 0 && types.All(t => t is 4 or 5);
        }
        catch (ManagementException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Chiavi driver AMD che espongono EnableUlps / EnableUlps_NA.</summary>
    public static List<AmdDriverKey> ProbeAmdDriverKeys()
    {
        var result = new List<AmdDriverKey>();
        try
        {
            var (root, sub) = RegistryHelper.Split(DisplayClassKey);
            using (root)
            using (var classKey = root.OpenSubKey(sub, writable: false))
            {
                if (classKey is null) return result;

                foreach (var childName in classKey.GetSubKeyNames())
                {
                    // Le sottochiavi driver sono numeriche a 4 cifre: 0000, 0001, ...
                    if (childName.Length != 4 || !childName.All(char.IsDigit)) continue;

                    using var child = classKey.OpenSubKey(childName, writable: false);
                    if (child is null) continue;

                    var desc = child.GetValue("DriverDesc")?.ToString() ?? "";
                    var names = child.GetValueNames();
                    var ulps = names.Where(n =>
                        n.Equals("EnableUlps", StringComparison.OrdinalIgnoreCase) ||
                        n.Equals("EnableUlps_NA", StringComparison.OrdinalIgnoreCase)).ToList();

                    var looksAmd = desc.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                                || desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                                || desc.Contains("ATI", StringComparison.OrdinalIgnoreCase);

                    if (ulps.Count > 0 && (looksAmd || ulps.Count > 0))
                        result.Add(new AmdDriverKey($@"{DisplayClassKey}\{childName}", desc, ulps));
                }
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public string Summary()
    {
        var gpu = Gpus.Count == 0 ? "nessuna GPU PCI rilevata" : string.Join(", ", Gpus.Select(g => g.Name));
        var net = ActiveAdapters.Count == 0 ? "nessuna scheda attiva" : string.Join(", ", ActiveAdapters.Select(a => a.Name));
        return $"GPU: {gpu}\nSchede di rete attive: {net}\nDisco di sistema: {(SystemDriveIsSsd ? "SSD" : "HDD o misto")}\n"
             + $"Privilegi: {(IsElevated() ? "amministratore" : "utente standard")}";
    }
}
