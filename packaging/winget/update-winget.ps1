<#
.SYNOPSIS
    Aggiorna il pacchetto winget scarlone.WinBoost a una nuova release.

.DESCRIPTION
    Usa wingetcreate (winget install Microsoft.WingetCreate) per rigenerare i
    manifest a partire dall'ultima versione gia' pubblicata su
    microsoft/winget-pkgs e dall'asset framework-dependent della GitHub Release.
    Su winget va quel profilo, non il self-contained: pesa ~0,6 MB e il runtime
    .NET e' dichiarato come dipendenza nel manifest, quindi lo installa winget.

    Il nome dell'asset segue il contratto del workflow di release:
        WinBoost-{version}-win-x64-framework.exe
    Lo script verifica che l'asset esista davvero prima di fare qualunque cosa:
    wingetcreate lo scarica per calcolarne lo SHA256, quindi un URL sbagliato
    fallirebbe comunque, ma piu' tardi e con un errore meno chiaro.

    Senza -Submit i manifest vengono generati in packaging/winget/generated e
    validati con winget validate: e' la prova generale prima della PR.
    Con -Submit wingetcreate apre (o aggiorna) la PR su microsoft/winget-pkgs
    tramite il fork dell'account associato al token. Il token GitHub (PAT con
    scope public_repo) va nella variabile d'ambiente WINGET_CREATE_GITHUB_TOKEN:
    wingetcreate la legge da se', quindi lo script funziona identico in locale
    e in CI.

    NOTA: questo script serve dalle release successive alla prima. La prima
    pubblicazione (il pacchetto non esiste ancora su winget-pkgs) segue la
    procedura in README.md accanto a questo script.

.EXAMPLE
    .\update-winget.ps1 -Version 0.2.0
    .\update-winget.ps1 -Version 0.2.0 -Submit
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$Submit
)

$ErrorActionPreference = 'Stop'

if (-not $PSScriptRoot) { throw 'Impossibile determinare la cartella dello script.' }

$packageId = 'scarlone.WinBoost'
$installerUrl = "https://github.com/scarlone/WinBoost/releases/download/v$Version/WinBoost-$Version-win-x64-framework.exe"

if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
    throw "wingetcreate non trovato. Installalo con: winget install Microsoft.WingetCreate"
}

if ($Submit -and -not $env:WINGET_CREATE_GITHUB_TOKEN) {
    throw ("WINGET_CREATE_GITHUB_TOKEN non impostata. Serve un PAT GitHub con scope " +
           "public_repo; wingetcreate lo usa per creare il fork di winget-pkgs e aprire la PR.")
}

# HEAD, non GET: basta sapere che l'asset esiste, senza scaricare nulla.
try {
    Invoke-WebRequest -Uri $installerUrl -Method Head -MaximumRedirection 5 | Out-Null
} catch {
    throw ("Asset non trovato: $installerUrl`n" +
           "Verifica che la release v$Version esista e che il workflow abbia pubblicato " +
           "l'asset col nome contrattuale WinBoost-$Version-win-x64-framework.exe.")
}

# "$url|x64" forza l'architettura: nell'URL non c'e' nulla che permetta a
# wingetcreate di dedurla in modo affidabile ("x64" nel nome funziona oggi,
# ma l'override esplicito non dipende dall'euristica di matching).
$wcArgs = @('update', $packageId, '--version', $Version, '--urls', "$installerUrl|x64")

if ($Submit) {
    $wcArgs += @('--submit', '--prtitle', "New version: $packageId version $Version")
    & wingetcreate @wcArgs
    if ($LASTEXITCODE -ne 0) {
        throw ("wingetcreate submit fallito (exit $LASTEXITCODE). Se il pacchetto non e' mai " +
               "stato pubblicato, la prima PR segue la procedura in README.md, non questo script.")
    }
    Write-Host "PR aperta/aggiornata su microsoft/winget-pkgs per $packageId $Version" -ForegroundColor Green
} else {
    $outDir = Join-Path $PSScriptRoot 'generated'
    $wcArgs += @('--out', $outDir)
    & wingetcreate @wcArgs
    if ($LASTEXITCODE -ne 0) {
        throw ("wingetcreate update fallito (exit $LASTEXITCODE). Se il pacchetto non e' mai " +
               "stato pubblicato, la prima PR segue la procedura in README.md, non questo script.")
    }

    # wingetcreate ricrea l'albero manifests/... sotto --out: la cartella della
    # versione viene cercata invece di assumere il percorso esatto, cosi' un
    # cambio di layout nel tool non rompe la validazione in silenzio.
    $manifestDir = Get-ChildItem $outDir -Recurse -Directory -Filter $Version |
        Where-Object { $_.Parent.Name -eq 'WinBoost' } | Select-Object -First 1
    if (-not $manifestDir) { throw "manifest generati ma cartella $Version non trovata sotto $outDir" }

    winget validate --manifest $manifestDir.FullName
    if ($LASTEXITCODE -ne 0) { throw "winget validate fallito su $($manifestDir.FullName)" }

    Write-Host "Manifest generati e validati: $($manifestDir.FullName)" -ForegroundColor Green
    Write-Host "Per aprire la PR: .\update-winget.ps1 -Version $Version -Submit" -ForegroundColor DarkGray
}
