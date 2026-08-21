<!--
  Template delle note di release, riempito da release.yml: i segnaposto fra
  graffe (versione senza "v", stato della firma, contenuto di SHA256SUMS.txt)
  vengono sostituiti li', quindi questo commento non deve nominarli in forma
  letterale. Il changelog generato da GitHub viene accodato in fondo da
  `gh release create --generate-notes`.
-->
## Quale file scaricare

| Asset | Prerequisiti | Dimensione |
|---|---|---|
| `WinBoost-{VERSION}-win-x64.exe` | nessuno | ~68 MB |
| `WinBoost-{VERSION}-win-x64-framework.exe` | .NET 8 Desktop Runtime | ~0,6 MB |

Nel dubbio scarica `WinBoost-{VERSION}-win-x64.exe`: include il runtime e funziona
senza installare nulla. La variante `-framework` e' pensata per winget, che dichiara
il runtime come dipendenza e lo installa se manca.

## Firma

{SIGNING}

## Verifica dell'integrita'

SHA256 dei file pubblicati (calcolati dopo l'eventuale firma):

```
{SHA256}
```

Per verificare su Windows:

```powershell
Get-FileHash .\WinBoost-{VERSION}-win-x64.exe -Algorithm SHA256
```

L'hash stampato deve coincidere con quello in `SHA256SUMS.txt` allegato qui sotto.

## Avvertenze

WinBoost modifica il registro di sistema e i servizi di Windows. Ogni sessione
scrive un journal con il valore precedente di ogni operazione e supporta il
rollback, ma per i tweak marcati `high` conviene creare un punto di ripristino
di sistema prima di applicare.
