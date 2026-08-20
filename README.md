# WinBoost

Ottimizzatore Windows data-driven, con anteprima non distruttiva e rollback reale.

Il catalogo dei tweak e' **dati, non codice**: `data/tweaks.json` descrive ogni modifica in
forma dichiarativa e il motore la esegue. Aggiungere un tweak significa aggiungere un oggetto
JSON, non scrivere una funzione.

## Perche' esiste

Nasce dall'analisi funzionale di un ottimizzatore commerciale distribuito via `irm ... | iex`.
Il dominio tecnico (chiavi di registro, parametri TCP, impostazioni GPU) e' fatto pubblico e
documentato; quello che mancava all'originale era l'ingegneria intorno:

| Problema dell'originale | Come lo risolve WinBoost |
|---|---|
| Nessun rollback tranne 3 casi GPU | Journal per sessione con il valore precedente di **ogni** operazione |
| Web server locale senza CSRF token ne' check `Origin` | Nessun server: app desktop, nessuna superficie di rete |
| DNS forzati a Cloudflare senza chiedere | Provider a scelta, default "non modificare" |
| Elimina i piani energetici Bilanciato e Risparmio | Li lascia intatti, attiva solo Ultimate |
| IPv6 disattivato di default | Opt-in esplicito, con avviso su VPN e Xbox |
| Rimuove i pacchetti Xbox (rompe il login Game Pass) | Esclusi dal set predefinito |
| `SysMain` disattivato senza guardare il disco | Condizione `storage.system-drive-is-ssd` verificata a runtime |
| Radice di fiducia = file Google Drive mutabile | Nessun download a runtime: il catalogo e' locale |
| Anteprima e applicazione richiedono entrambe admin | Manifest `asInvoker`: si eleva solo per applicare |

## Struttura

```
WinBoost/
├─ data/
│   ├─ tweaks.json           catalogo: 76 tweak, 141 operazioni
│   └─ nvidia-profiles.json  impostazioni NVIDIA con SettingID verificati
├─ WinBoost.Core/            motore, nessuna dipendenza da UI
│   ├─ Models.cs             modelli + validazione del catalogo all'avvio
│   ├─ RegistryHelper.cs     lettura/scrittura/snapshot registro (vista 64 bit)
│   ├─ ProcessRunner.cs      processi esterni, servizi, riavvio della shell
│   ├─ NetworkHelper.cs      letture via CIM, scritture via cmdlet, DNS via WMI
│   ├─ HardwareProbe.cs      GPU, SSD, schede di rete, chiavi driver AMD
│   ├─ Parameters.cs         parametri configurabili e override utente
│   ├─ NvidiaProfiles.cs     modelli, generatore .nip, Profile Inspector
│   ├─ Session.cs            journal delle modifiche, scrittura atomica
│   ├─ TweakEngine.cs        orchestrazione: applicabilita', ordine, journal
│   └─ Ops/                  un handler per tipo di operazione
│       ├─ OpHandler.cs      classe base, servizi, registro dei tipi
│       ├─ RegistryOps.cs    reg, reg-template
│       ├─ SystemOps.cs      service, cmd, process-kill, clear-dir, powerplan
│       ├─ NetworkOps.cs     dns, netadapter-rsc, netadapter-property
│       ├─ PackageOps.cs     appx, uninstaller, winget, store, windows-update
│       └─ GpuOps.cs         nvapi-profile
├─ WinBoost.App/             interfaccia WPF
└─ WinBoost.Tests/           98 test, nessuno tocca lo stato di sistema
```

## Build

```powershell
dotnet build
dotnet run --project WinBoost.App

.\publish.ps1                    # self-contained (default)
.\publish.ps1 -Mode Framework    # richiede il .NET 8 Desktop Runtime
.\publish.ps1 -Mode Both -Version 0.2.0
```

| Profilo | Dimensione | Prerequisiti | Canale |
|---|---|---|---|
| `self-contained` | 68,4 MB | nessuno | download diretto da GitHub Releases |
| `framework-dependent` | 0,6 MB | .NET 8 Desktop Runtime | winget, che lo installa come dipendenza |

Ogni pacchetto contiene solo `WinBoost.exe` e `SHA256SUMS.txt`. Lo script avvisa se
trova file inattesi nell'output.

## Provenienza del catalogo

`data/tweaks.json` e' **incorporato nell'eseguibile** come risorsa, non affiancato.
Il motivo e' di sicurezza: il catalogo pilota un processo che scrive in `HKLM` da
amministratore, quindi un file di testo modificabile da chiunque abbia accesso in
scrittura alla cartella di installazione sarebbe una escalation locale gratuita.

Per sviluppare basta modificare `data/tweaks.json` e ricompilare: MSBuild rileva la
modifica e riembedda la risorsa.

L'override esterno resta possibile ma e' esplicito e dichiarato nella UI:

```powershell
WinBoost.exe --catalog C:\percorso\catalogo.json
```

In quel caso l'intestazione mostra un banner rosso `CATALOGO ESTERNO: <percorso>`.
Un catalogo mancante, malformato o incoerente (categoria inesistente, operazione senza
`path`) non fa crashare l'app: viene mostrato un errore di avvio e il processo esce con
codice 1.

## Schema del catalogo

```jsonc
{
  "id": "gaming.mouse-accel-off",
  "category": "gaming",
  "name": "Disattiva accelerazione del mouse",
  "description": "Rende il movimento 1:1. Fondamentale per la mira negli FPS.",
  "risk": "low",              // low | medium | high
  "admin": false,             // richiede elevazione
  "reboot": false,            // richiede riavvio per avere effetto
  "restart": ["explorer"],    // processi da riavviare dopo
  "vendor": "nvidia",         // opzionale: solo su GPU di quel vendore
  "condition": "storage.system-drive-is-ssd",   // opzionale
  "enabledByDefault": false,  // assente = true; false = opt-in esplicito
  "warning": "Testo mostrato in giallo nella UI",
  "ops": [
    { "type": "reg", "path": "HKCU:\\Control Panel\\Mouse", "name": "MouseSpeed",
      "valueType": "String", "value": "0", "default": "1", "revert": "snapshot" }
  ]
}
```

### Come si aggiunge un tipo di operazione

Ogni tipo vive in un solo posto: una sottoclasse di `OpHandler` sotto `WinBoost.Core/Ops/`.

```csharp
public sealed class MiaOpHandler : OpHandler
{
    public override string Type => "mia-op";

    // opzionale: espande l'operazione nei bersagli reali (schede, GPU, servizi...)
    public override IEnumerable<ResolvedOp> Resolve(Tweak t, TweakOp op, int i, IOpServices svc) => ...

    public override ChangeDescription Describe(...) => new(bersaglio, valoreAttuale, valoreProposto);

    public override void Execute(...) { /* la voce di journal arriva gia' compilata */ }

    public override bool Rollback(...) => false;   // il default non finge di poter annullare
}
```

Poi basta aggiungerlo all'elenco in `OpRegistry`. `TweakEngine.SupportedTypes` deriva da
quell'elenco, quindi non esistono liste da tenere allineate a mano.

Prima queste responsabilita' erano quattro `switch` paralleli dentro `TweakEngine`
(risoluzione, descrizione, esecuzione, rollback) piu' un elenco separato dei tipi supportati:
bastava dimenticarne uno perche' un tweak venisse mostrato in anteprima e poi non applicato,
in silenzio. E' successo davvero, con `store-update` e `windows-update`. Ora il motore fa solo
orchestrazione — 366 righe invece di 955 — e due test sorvegliano il confine fra dati e codice.

### Tipi di operazione

| Tipo | Stato | Note |
|---|---|---|
| `reg` | implementato | rollback per snapshot; `skipIfKeyMissing`, `skipIfValueMissing` |
| `reg-template` | implementato | segnaposto `{PNPDeviceID}`, `{DriverIndex}`, `{InterfaceGuid}` |
| `service` | implementato | tipo di avvio via `sc.exe`, ripristina anche lo stato di esecuzione |
| `cmd` | implementato | rollback tramite `revertArgs` espliciti |
| `process-kill` | implementato | non reversibile per natura |
| `clear-dir` | implementato | non reversibile |
| `powerplan` | implementato | ripristina il GUID del piano attivo |
| `dns` | implementato | WMI `SetDNSServerSearchOrder`, ripristino incluso DHCP |
| `netadapter-rsc` | implementato | lettura via CIM; **scrittura non testata su hardware reale** |
| `netadapter-property` | implementato | lettura via CIM; **scrittura non testata su hardware reale** |
| `appx-remove` | implementato | non reversibile: reinstallazione manuale dallo Store |
| `uninstaller` | implementato | non reversibile |
| `winget`, `winget-upgrade-all` | implementato | non reversibile |
| `nvapi-profile` | implementato | genera un `.nip` e lo importa via NVIDIA Profile Inspector; vedi sotto |
| `store-update` | implementato | avvia la scansione Store via provider MDM |
| `windows-update` | implementato | **solo ricerca**, non installa; vedi sotto |
| `adlx-preset` | **non implementato** | richiede l'SDK AMD ADLX |

`download-run` e' stato **rimosso**: l'unico tweak che lo usava (DirectX legacy) e' passato a
winget. Meglio eliminare il percorso "scarica un eseguibile ed eseguilo" che indurirlo.

`windows-update` cerca gli aggiornamenti e li elenca, ma non li installa. Installare senza
sorveglianza, con i riavvii che comporta, non e' una decisione che uno strumento di
ottimizzazione debba prendere al posto dell'utente.

## Riavvio della shell

Molti tweak di Explorer scrivono nel registro ma diventano visibili solo quando il processo
rilegge le impostazioni. Il campo `restart` del catalogo dichiara quali processi riavviare;
il motore li accumula e li riavvia **una sola volta a fine sessione**, non una volta per
tweak. La conferma prima di applicare dice esplicitamente che le finestre aperte si
chiuderanno.

`TweakEngine.AutoRestartShell = false` disattiva il comportamento: i bersagli finiscono in
`Session.ShellRestartPending` e le modifiche diventano visibili al logout successivo.
I test girano sempre con questa impostazione a false.

## Collocazione della finestra

La finestra nasceva con la barra del titolo fuori dallo schermo, quindi non si poteva ne'
chiudere ne' ingrandire. Non era un problema di scala ma di aritmetica: WPF centra dividendo
per due la differenza fra area disponibile e dimensione richiesta, senza verificare che la
finestra ci stia. Su 1920x1080 al 150% l'area di lavoro e' 1280x672 DIP; con `Height="820"`
la differenza e' negativa e il risultato era `Top = -74` DIP, cioe' 111 pixel sopra il bordo.

`WindowPlacement.FitToWorkArea` riduce la dimensione richiesta a quella disponibile e centra
il risultato, garantendo che il rettangolo sia interamente dentro l'area di lavoro. Il monitor
considerato e' quello su cui la finestra nasce (`MonitorFromWindow`), non il primario, e i
minimi cedono se lo schermo e' piu' piccolo: una finestra piu' grande dello schermo e'
esattamente il difetto da evitare.

La funzione e' pura e sta in `WinBoost.Core`: la matematica che aveva sbagliato e' cosi'
verificabile senza aprire una finestra, ed e' coperta da 8 test fra cui il caso reale che
produceva `Top = -74`.

## Prestazioni dell'anteprima

L'anteprima leggeva le proprieta' delle schede di rete tramite i cmdlet `NetAdapter`, cioe'
un processo `powershell.exe` per ogni proprieta' di ogni scheda. Ogni avvio costa qualche
secondo: l'anteprima completa del catalogo impiegava **17 secondi**.

Ora le letture passano da due query CIM su `root\StandardCimv2`
(`MSFT_NetAdapterAdvancedPropertySettingData` e `MSFT_NetAdapterRscSettingData`), eseguite
una volta sola per passata e messe in cache. Le **scritture** restano sui cmdlet, dove il
costo e' irrilevante e la semantica di riavvio del driver e' gestita per noi.

| | prima | dopo |
|---|---|---|
| anteprima categoria rete | 13.742 ms | 229 ms |
| anteprima completa (76 tweak) | 17.392 ms | 291 ms |

Nello stesso passaggio l'anteprima ha smesso di interrogare lo stato di esecuzione dei
servizi (`sc.exe query`, uno per servizio): serve solo in fase di applicazione.

## Test

```powershell
dotnet test
```

98 test su catalogo, registro, sessioni, rollback, parametri, collocazione finestra, pattern delle
schede di rete e formato `.nip`. Non toccano lo stato di sistema: le prove sul registro
lavorano sotto una chiave usa-e-getta in `HKCU\Software\WinBoost.Tests\<guid>`, quindi non
richiedono privilegi, e il motore di test gira con `AutoRestartShell = false`.

Due test sorvegliano il confine fra dati e codice: ogni tipo di operazione dichiarato nel
catalogo dev'essere eseguibile dal motore (confrontato con `TweakEngine.SupportedTypes`, non
con una lista duplicata), e ogni lacuna dichiarata dev'essere ancora tale — se viene
implementata, il test obbliga a toglierla dall'elenco.

I tipi non implementati vengono registrati come `Skipped` con messaggio esplicito: non
interrompono la sessione e non fingono di aver funzionato.

### Parametri

Un tweak puo' esporre scelte configurabili. La UI le rende nella scheda del tweak e le
preferenze finiscono in `TweakEngine.Overrides`, sovrapposte ai default: **il catalogo resta
immutabile**.

```jsonc
"parameters": {
  "provider": {                      // type "choice" -> ComboBox
    "type": "choice", "default": "keep",
    "choices": {
      "keep":       { "label": "Non modificare", "servers": null },
      "cloudflare": { "label": "Cloudflare", "servers": ["1.1.1.1", "1.0.0.1"] },
      "dhcp":       { "label": "Torna a DHCP", "servers": [] }
    }
  },
  "packages": {                      // type "multi-select" -> lista di checkbox
    "type": "multi-select",
    "default":   ["Microsoft.BingNews"],    // preselezionati
    "available": ["Microsoft.XboxApp"],     // aggiungibili, non preselezionati
    "note": "Testo esplicativo mostrato sotto il titolo del parametro"
  }
}
```

Per il DNS la distinzione e' a tre stati e conta: `null` = non toccare, lista vuota = torna a
DHCP, lista piena = server statici.

### Anteprima ed elevazione

`CheckHardware` e `CheckApplicable` sono deliberatamente separati:

- **anteprima** applica solo i vincoli hardware. Un tweak che richiede l'elevazione viene
  comunque mostrato con i valori reali, annotato "richiede privilegi di amministratore".
  L'anteprima serve a decidere *se* elevare: nasconderle sarebbe circolare.
- **applicazione** richiede anche l'elevazione, e il tweak viene registrato come `Skipped`.

### Impostazioni NVIDIA

`data/nvidia-profiles.json` contiene i profili applicabili. `SettingID`, `ValueType` e
`SettingValue` sono **estratti da un file `.nip` reale e funzionante**, non ricostruiti a
memoria: Profile Inspector applica in base a `SettingID`, quindi un identificativo sbagliato
scriverebbe l'impostazione sbagliata.

Il flusso di `nvapi-profile` e':

1. esporta le impostazioni attualmente personalizzate (`-exportCustomized`) e ne conserva il
   `.nip` sotto `%LOCALAPPDATA%\WinBoost\gpu-profiles\` — senza questo passo il rollback non
   esisterebbe;
2. genera il `.nip` del profilo dal catalogo;
3. lo importa (`-silentImport`).

Il rollback reimporta il backup.

**Perche' non NVAPI diretta.** Le funzioni DRS di NVAPI non sono esportate per nome:
si ottengono passando identificativi magici a `nvapi_QueryInterface`. Non ho modo di
verificare quegli identificativi su questa macchina, e un identificativo sbagliato non
fallisce in modo pulito — restituisce il puntatore a un'altra funzione, che viene poi
chiamata con la firma sbagliata. In un processo che gira da amministratore non e' un
rischio accettabile per del codice non verificato.

**NVIDIA Profile Inspector non e' distribuito con WinBoost**: e' software di terze parti
non firmato, e incorporarlo significherebbe farsene garanti. Va indicato con
`--profile-inspector <percorso>` oppure messo in
`tools\nvidiaProfileInspector\nvidiaProfileInspector.exe` accanto all'eseguibile. Se manca,
l'operazione viene riportata come saltata con l'indicazione di dove procurarselo.

**Formato `.nip`.** Il generatore riproduce byte per byte l'artefatto di riferimento,
inclusa un'incoerenza che sembra un errore: il file e' UTF-8 con BOM ma la dichiarazione XML
annuncia `utf-16`. Profile Inspector deserializza da stringa e ignora la dichiarazione;
`XDocument.Load` invece la rispetta e rifiuta il file. Abbiamo scelto la fedelta'
all'artefatto che funziona, e `NipWriter.Read` aggira la dichiarazione in lettura.

### Strategie di rollback

- `snapshot` — il valore precedente viene letto e salvato **prima** della scrittura.
  Se la voce non esisteva, il rollback la cancella invece di inventare un default.
- `cmd` — comando inverso esplicito in `revertArgs`.
- `delete-key` — rimuove l'intero sottoalbero indicato da `revertKey`.
- `none` — irreversibile; la UI e il journal lo dichiarano.

## Sessioni

Ogni applicazione crea un journal in
`%LOCALAPPDATA%\WinBoost\sessions\<timestamp>.json`, salvato **dopo ogni singola operazione**:
se il processo muore a meta' sessione il rollback resta possibile.
Il rollback percorre le voci in ordine inverso.

## Distribuzione

L'ostacolo non e' l'hosting, e' SmartScreen: un eseguibile non firmato che chiede
privilegi di amministratore mostra "Windows ha protetto il PC", ed e' esattamente il
profilo comportamentale del malware.

1. **Firma** il binario. Senza firma la reputazione non si costruisce mai.
   `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 WinBoost.exe`
   Un certificato EV da' reputazione immediata; OV la costruisce coi download.
   Verifica costi ed eleggibilita' correnti prima di acquistare: cambiano spesso.
2. **GitHub Releases** come base: URL stabile, changelog, e lo `SHA256SUMS.txt`
   pubblicato accanto al binario.
3. **winget** come canale principale, col profilo `framework-dependent` e il runtime
   dichiarato come dipendenza nel manifest.
4. **Microsoft Store: non praticabile.** Un'app che scrive in `HKLM` e modifica servizi
   non supera la certificazione.

Se in futuro aggiungi aggiornamenti automatici, verifica il binario scaricato **per
firma**, non per hash pubblicato sullo stesso canale: altrimenti chi controlla il canale
controlla la macchina, che e' il difetto strutturale da cui questo progetto e' nato.

## Licenza

MIT — vedi [LICENSE](LICENSE).

### Componenti di terze parti

Nulla di terze parti viene distribuito con WinBoost, ma due cose meritano di essere dette
esplicitamente:

- **NVIDIA Profile Inspector** non e' incluso. E' software di terze parti non firmato:
  incorporarlo significherebbe farsene garanti. Va procurato separatamente e indicato con
  `--profile-inspector`, e resta soggetto alla propria licenza.
- Gli identificativi in `data/nvidia-profiles.json` (`SettingID`) sono **costanti del driver
  NVIDIA**, non nostre: sono dati di fatto necessari a parlare con NVAPI, non espressione
  coperta dalla licenza di questo progetto. La licenza MIT copre il codice di WinBoost.

## Limiti noti

- **Il percorso NVIDIA non e' stato provato su una GPU NVIDIA.** Il `.nip` generato e'
  verificato identico byte per byte all'artefatto di riferimento, ma l'invocazione di
  Profile Inspector e l'effetto sul driver restano da confermare su hardware reale.
- `-exportCustomized` esporta solo le impostazioni **gia' personalizzate**. Un'impostazione
  che prima era al valore predefinito del driver non finisce nel backup, quindi il rollback
  non la riporta indietro: resta personalizzata. Per un ripristino completo serve
  "Ripristina impostazioni predefinite" nel Pannello di controllo NVIDIA.
- Il preset AMD ADLX e' ancora solo documentazione: richiede l'SDK AMD.
- Le **scritture** sulle proprieta' avanzate della scheda di rete compilano e hanno il
  percorso di rollback, ma non sono state verificate su hardware reale: modificarle avrebbe
  significato toccare la connessione della macchina di sviluppo. Le letture sono verificate.
- Nessun punto di ripristino di sistema: il journal e' piu' preciso, ma per i tweak `high`
  conviene comunque crearne uno a mano prima di applicare.
- L'eseguibile non e' firmato: SmartScreen mostrera' un avviso al primo avvio.
