<#
.SYNOPSIS
    Produce i pacchetti di distribuzione di WinBoost.

.DESCRIPTION
    Due profili, per due canali diversi:

    SelfContained  eseguibile singolo con il runtime .NET incluso (~68 MB).
                   Per il download diretto da GitHub Releases: l'utente scarica
                   e apre, senza prerequisiti.

    Framework      eseguibile singolo che richiede il .NET 8 Desktop Runtime (~3 MB).
                   Per winget, che sa installare il runtime come dipendenza dichiarata.

    In entrambi i casi il catalogo e' incorporato nell'eseguibile: l'output non
    contiene tweaks.json affiancato.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Mode Framework
    .\publish.ps1 -Mode Both -Version 0.2.0
#>
[CmdletBinding()]
param(
    [ValidateSet('SelfContained', 'Framework', 'Both')]
    [string]$Mode = 'SelfContained',

    [string]$Version = '',

    # Deliberatamente senza default: con [CmdletBinding()] i default di param()
    # vengono valutati prima che $PSScriptRoot sia popolato, quindi
    # "$PSScriptRoot\publish" diventerebbe "\publish", cioe' la radice del disco.
    # Il valore viene calcolato nel corpo, dove $PSScriptRoot e' affidabile.
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

if (-not $PSScriptRoot) { throw 'Impossibile determinare la cartella dello script.' }
if (-not $OutputRoot)   { $OutputRoot = Join-Path $PSScriptRoot 'publish' }

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $PSScriptRoot 'WinBoost.App\WinBoost.App.csproj'
if (-not (Test-Path $project)) { throw "Progetto non trovato: $project" }

Write-Host "Output: $OutputRoot" -ForegroundColor DarkGray

function Publish-Variant {
    param([string]$Name, [bool]$SelfContained)

    $out = Join-Path $OutputRoot $Name
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host "==> $Name" -ForegroundColor Cyan

    # Non chiamarlo $args: dentro una funzione e' una variabile automatica.
    $dotnetArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        # Non usare --self-contained:<bool>: gli SDK recenti (10.x) scartano il
        # valore in silenzio e con -r l'output diventa comunque self-contained,
        # quindi il profilo "framework" usciva da 162 MB. I flag dedicati vengono
        # inoltrati correttamente su tutti gli SDK.
        $(if ($SelfContained) { '--self-contained' } else { '--no-self-contained' }),
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
        '-o', $out,
        '--nologo'
    )

    # La compressione ha senso solo col runtime incorporato: senza, non c'e' nulla da comprimere.
    if ($SelfContained) { $dotnetArgs += '-p:EnableCompressionInSingleFile=true' }
    if ($Version)       { $dotnetArgs += "-p:Version=$Version" }

    & dotnet @dotnetArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish fallito per $Name (exit $LASTEXITCODE)" }

    $exe = Join-Path $out 'WinBoost.exe'
    if (-not (Test-Path $exe)) { throw "eseguibile non prodotto in $out" }

    # Lo SHA256 va pubblicato accanto al binario: per un tool che gira da amministratore
    # e' meta' della credibilita', soprattutto finche' il binario non e' firmato.
    $hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
    $size = (Get-Item $exe).Length
    "$hash  WinBoost.exe" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

    $stray = Get-ChildItem $out -File | Where-Object { $_.Name -notin 'WinBoost.exe', 'SHA256SUMS.txt' }
    if ($stray) { Write-Warning "file inattesi nell'output: $($stray.Name -join ', ')" }

    [pscustomobject]@{
        Profilo = $Name
        MB      = [math]::Round($size / 1MB, 1)
        SHA256  = $hash
        Percorso = $out
    }
}

$results = @()
if ($Mode -in 'SelfContained', 'Both') { $results += Publish-Variant -Name 'self-contained' -SelfContained $true }
if ($Mode -in 'Framework',    'Both') { $results += Publish-Variant -Name 'framework-dependent' -SelfContained $false }

Write-Host ''
$results | Format-List
Write-Host 'Firma: signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 <exe>' -ForegroundColor DarkGray
