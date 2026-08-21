using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinBoost.Core;

/// <summary>
/// Versione semantica ridotta a cio' che serve qui: confrontare la versione in
/// esecuzione con quella dell'ultima release. Tre numeri e un suffisso non
/// giustificano una dipendenza esterna.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemVer>
{
    private static readonly Regex Shape = new(
        // Il prefisso 'v' e' accettato in entrambe le grafie: una tag scritta 'V1.0.0'
        // e' la stessa intenzione, e rifiutarla spegnerebbe le notifiche in silenzio.
        @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var candidate = text.Trim();

        // I metadati di build ('0.1.0+95046d6') non partecipano all'ordinamento e
        // arrivano sempre: AssemblyInformationalVersion accoda l'hash del commit.
        var plus = candidate.IndexOf('+');
        if (plus >= 0) candidate = candidate[..plus];

        var m = Shape.Match(candidate);
        if (!m.Success) return false;

        // TryParse e non Parse: una tag come 'v99999999999.0.0' e' malformata,
        // non un motivo per far esplodere il controllo aggiornamenti.
        if (!int.TryParse(m.Groups[1].Value, out var major)) return false;
        if (!int.TryParse(m.Groups[2].Value, out var minor)) return false;

        var patch = 0;
        if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, out patch)) return false;

        version = new SemVer(major, minor, patch, m.Groups[4].Success ? m.Groups[4].Value : null);
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var byNumber = Major.CompareTo(other.Major);
        if (byNumber != 0) return byNumber;

        byNumber = Minor.CompareTo(other.Minor);
        if (byNumber != 0) return byNumber;

        byNumber = Patch.CompareTo(other.Patch);
        if (byNumber != 0) return byNumber;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    /// <summary>
    /// Regola 11 di semver: una release finale batte sempre la sua prerelease
    /// (1.0.0 e' maggiore di 1.0.0-rc1). Senza questo confronto chi gira una rc
    /// non verrebbe avvisato della finale, e chi gira la finale si vedrebbe
    /// proporre la rc come se fosse un aggiornamento.
    /// </summary>
    private static int ComparePreRelease(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) && string.IsNullOrEmpty(right)) return 0;
        if (string.IsNullOrEmpty(left)) return 1;
        if (string.IsNullOrEmpty(right)) return -1;

        var a = left.Split('.');
        var b = right.Split('.');

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            // Chi finisce prima ha precedenza minore: 1.0.0-rc precede 1.0.0-rc.1
            if (i >= a.Length) return -1;
            if (i >= b.Length) return 1;

            var numericA = int.TryParse(a[i], out var na);
            var numericB = int.TryParse(b[i], out var nb);

            int comparison;
            if (numericA && numericB) comparison = na.CompareTo(nb);
            else if (numericA) comparison = -1;          // il numerico precede l'alfanumerico
            else if (numericB) comparison = 1;
            else comparison = string.CompareOrdinal(a[i], b[i]);

            if (comparison != 0) return comparison;
        }

        return 0;
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        IsPreRelease ? $"{Major}.{Minor}.{Patch}-{PreRelease}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>Come e' arrivato l'eseguibile su questa macchina.</summary>
public enum InstallKind
{
    /// <summary>Scaricato da GitHub Releases e messo in una cartella qualsiasi.</summary>
    Standalone,

    /// <summary>Installato da winget, che sa aggiornarlo da solo.</summary>
    Winget
}

public static class InstallOrigin
{
    /// <summary>
    /// winget installa i pacchetti 'portable' sotto una radice nota, in ambito
    /// utente o macchina. Se siamo li' dentro l'aggiornamento non e' affare
    /// nostro: sostituire l'exe a mano desincronizzerebbe il database dei
    /// pacchetti, e winget si ritroverebbe piu' avanti in uno stato incoerente.
    /// </summary>
    public static IReadOnlyList<string> DefaultWingetRoots() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WinGet", "Packages")
    };

    public static InstallKind Detect(string? exePath = null, IEnumerable<string>? wingetRoots = null)
    {
        var path = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return InstallKind.Standalone;

        foreach (var root in wingetRoots ?? DefaultWingetRoots())
            if (IsUnder(path, root)) return InstallKind.Winget;

        return InstallKind.Standalone;
    }

    /// <summary>
    /// Contenimento vero, non StartsWith sul testo: senza il separatore finale
    /// "C:\Packages-vecchi" risulterebbe dentro "C:\Packages".
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            var prefix = Path.GetFullPath(root)
                             .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;

            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>Esito del confronto fra la versione locale e l'ultima release pubblicata.</summary>
public sealed record UpdateInfo(SemVer Current, SemVer Latest, string Tag, InstallKind Install)
{
    public bool IsNewer => Latest > Current;

    /// <summary>
    /// L'URL viene costruito qui da una costante, NON letto dal campo html_url
    /// della risposta. E' la differenza fra "il server dice che esiste una
    /// versione nuova" e "il server dice dove andarla a prendere": la seconda e'
    /// il difetto strutturale da cui nasce questo progetto.
    /// </summary>
    public string ReleasePageUrl => $"https://github.com/{UpdateChecker.RepoSlug}/releases/tag/{Tag}";

    public string WingetUpgradeCommand => $"winget upgrade --id {UpdateChecker.PackageIdentifier}";
}

/// <summary>
/// Controllo aggiornamenti in sola notifica: interroga l'API di GitHub, confronta
/// i numeri di versione e riferisce. Non scarica e non esegue nulla.
///
/// La scelta e' deliberata. Un auto-updater che verifica il binario scaricato per
/// hash pubblicato sullo stesso canale non aggiunge sicurezza: chi controlla il
/// canale controlla la macchina. Finche' l'eseguibile non e' firmato e la verifica
/// non avviene per firma Authenticode, l'unica cosa onesta e' avvisare e lasciare
/// il download all'utente.
/// </summary>
public sealed class UpdateChecker
{
    public const string RepoSlug = "scarlone/WinBoost";
    public const string PackageIdentifier = "scarlone.WinBoost";

    private const string LatestReleaseUrl = $"https://api.github.com/repos/{RepoSlug}/releases/latest";

    /// <summary>Le tag finiscono dentro un URL: quelle fuori da questo alfabeto sono rifiutate.</summary>
    private static readonly Regex SafeTag = new(@"^[A-Za-z0-9._-]{1,64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public UpdateChecker(HttpClient? http = null, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        _http = http ?? CreateClient();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();

        // Senza User-Agent l'API di GitHub risponde 403: non e' opzionale.
        client.DefaultRequestHeaders.Add("User-Agent", "WinBoost");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>
    /// Restituisce null per qualunque intoppo: rete assente, endpoint irraggiungibile,
    /// risposta malformata, limite di richieste superato. Un controllo aggiornamenti
    /// che disturba l'utente quando fallisce e' peggio di uno che non c'e'.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(SemVer current, InstallKind install,
        CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(_timeout);

            using var response = await _http.GetAsync(LatestReleaseUrl, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Evaluate(json, current, install);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Parte pura: dalla risposta grezza al verdetto. Separata per poterla provare.</summary>
    public static UpdateInfo? Evaluate(string json, SemVer current, InstallKind install)
    {
        if (!TryParseLatestTag(json, out var tag)) return null;
        if (!SemVer.TryParse(tag, out var latest)) return null;

        return new UpdateInfo(current, latest, tag, install);
    }

    internal static bool TryParseLatestTag(string json, out string tag)
    {
        tag = "";

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // /releases/latest esclude gia' bozze e prerelease, ma la risposta arriva
            // dalla rete: se dichiara di essere una delle due, la ignoriamo comunque.
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease")) return false;

            if (!root.TryGetProperty("tag_name", out var name) || name.ValueKind != JsonValueKind.String)
                return false;

            var value = name.GetString();
            if (string.IsNullOrWhiteSpace(value) || !SafeTag.IsMatch(value)) return false;

            tag = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsTrue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}
