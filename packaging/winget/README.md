# Pubblicazione su winget

Questa cartella contiene tutto cio' che serve per pubblicare WinBoost su
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs):

```
packaging/winget/
├─ manifests/s/scarlone/WinBoost/0.1.0/   manifest multi-file (version, installer, defaultLocale)
├─ update-winget.ps1                      aggiornamento a ogni release successiva (wingetcreate)
└─ generated/                             output locale di update-winget.ps1, non versionato
```

L'albero `manifests/...` replica il percorso richiesto da winget-pkgs
(`manifests/<iniziale>/<publisher>/<pacchetto>/<versione>/`), cosi' la copia nel
fork e' un copia-incolla senza rinomine.

Su winget va il profilo **framework-dependent** (~0,6 MB): il .NET 8 Desktop
Runtime e' dichiarato come `PackageDependencies` e lo installa winget. I nomi
degli asset della GitHub Release sono un contratto col workflow di release:

```
WinBoost-{version}-win-x64.exe            self-contained (download diretto)
WinBoost-{version}-win-x64-framework.exe  framework-dependent  <-- questo va su winget
SHA256SUMS.txt
```

I manifest usano lo schema **1.12.0**, non l'ultimo pubblicato.

Le due validazioni non coincidono, e conta solo la seconda: `winget validate` del
client accetta la 1.28.0 senza obiezioni, ma la pipeline di `winget-pkgs` la rifiuta
con la label `Manifest-Version-Error`. I pacchetti mergiati oggi — PowerToys,
GitHub CLI, 7zip — sono tutti a 1.12.0. Prima di alzare la versione dello schema,
guarda cosa usano i pacchetti appena mergiati: la presenza di uno schema piu' recente
in `winget-cli/schemas/` non significa che il repository lo accetti.

Attenzione a un secondo dettaglio verificato a caro prezzo: winget deduce il tipo di
manifesto dal commento `yaml-language-server` in testa al file, e li' vuole
`defaultLocale` in camelCase anche se il `$id` ufficiale dello schema e' tutto
minuscolo. Con la minuscola `winget validate` fallisce con
"Unsupported ManifestType: defaultlocale".

## Prima pubblicazione (0.1.0)

1. **Pubblica la release** `v0.1.0` su GitHub con gli asset del contratto sopra.
2. **Riempi lo SHA256**: in `scarlone.WinBoost.installer.yaml` il campo
   `InstallerSha256` e' un segnaposto di 64 zeri (passa lo schema, ma nessun
   file ha quell'hash: un submit per errore fallirebbe in pipeline). Il valore
   reale e' in `SHA256SUMS.txt` accanto all'asset, oppure:
   `(Get-FileHash WinBoost-0.1.0-win-x64-framework.exe -Algorithm SHA256).Hash`
3. **Valida in locale**:
   ```powershell
   winget validate --manifest packaging\winget\manifests\s\scarlone\WinBoost\0.1.0
   # l'avviso sulla dipendenza Microsoft.DotNet.DesktopRuntime.8 e' atteso:
   # la convalida locale non risolve i pacchetti remoti. L'ID esiste.
   ```
4. **Prova l'installazione dal manifest** (richiede una tantum, da admin,
   `winget settings --enable LocalManifestFiles`):
   ```powershell
   winget install --manifest packaging\winget\manifests\s\scarlone\WinBoost\0.1.0
   ```
   Deve installare il runtime come dipendenza, copiare l'exe sotto
   `%LOCALAPPDATA%\Microsoft\WinGet\Packages` e creare l'alias `winboost` in
   `%LOCALAPPDATA%\Microsoft\WinGet\Links`. Poi `winget uninstall scarlone.WinBoost`.
5. **Apri la PR**: fork di winget-pkgs, copia della cartella
   `manifests/s/scarlone/WinBoost/0.1.0/` nello stesso percorso del fork, PR con
   titolo `New package: scarlone.WinBoost version 0.1.0`. In alternativa
   `wingetcreate submit packaging\winget\manifests\s\scarlone\WinBoost\0.1.0`
   fa fork, branch e PR da solo (token in `WINGET_CREATE_GITHUB_TOKEN`).

Cosa aspettarsi: la pipeline automatica valida lo schema, scarica l'installer,
ne verifica lo SHA256 e lo installa in sandbox passandolo a Defender e
SmartScreen; poi un moderatore umano approva. Tempi tipici: da ore a qualche
giorno; le PR con etichette di errore (`Validation-*`) restano in attesa di
correzioni dell'autore.

## Release successive

```powershell
.\packaging\winget\update-winget.ps1 -Version 0.2.0          # genera e valida in locale
.\packaging\winget\update-winget.ps1 -Version 0.2.0 -Submit  # apre la PR
```

Funziona identico in CI: serve solo `WINGET_CREATE_GITHUB_TOKEN` nell'ambiente.
wingetcreate riparte dall'ultima versione pubblicata su winget-pkgs, quindi i
manifest in questa cartella restano la fotografia della prima submission, non
vanno aggiornati a ogni release.

## Rischi in review, detti onestamente

Questo pacchetto e' il profilo che la review di winget-pkgs guarda con piu'
sospetto: **un eseguibile non firmato che scrive nel registro (anche HKLM),
modifica servizi e impostazioni di rete, e chiede elevazione**. E' lo stesso
profilo comportamentale del malware, come nota il README principale a proposito
di SmartScreen.

In concreto puo' succedere:

- **Defender o SmartScreen segnalano il binario in pipeline**
  (`Validation-Defender-Error`): per un exe .NET single-file non firmato e
  senza reputazione e' un falso positivo plausibile. La strada e' il modulo di
  [segnalazione falsi positivi a Microsoft](https://www.microsoft.com/wdsi/filesubmission),
  poi la richiesta di ri-validazione nella PR. Non e' un muro, ma puo' costare
  giorni.
- **Domande del moderatore sul comportamento**: e' legittimo che chieda cosa
  tocca il tool e perche'. Le risposte esistono gia' e conviene linkarle nella
  descrizione della PR: repository pubblico, catalogo dichiarativo incorporato
  (non scaricato a runtime), anteprima prima di ogni scrittura, journal con
  rollback, manifest `asInvoker` con elevazione solo all'applicazione. Il campo
  `InstallationNotes` del manifest dichiara tutto questo anche all'utente.
- **La mancanza di firma resta il punto debole strutturale**: winget-pkgs non
  la richiede, ma abbassa la soglia di sospetto di Defender ad ogni release.
  La sezione Distribuzione del README principale la indica come primo passo;
  ogni release firmata riduce il rischio di questa lista.

Non e' invece un problema l'elevazione: winget installa il pacchetto senza
privilegi (portable, per-utente) e l'app parte `asInvoker`. Nessun campo
`ElevationRequirement` e' dichiarato perche' riguarda l'installazione, che qui
non eleva.
