# SamaDash

Dashboard a scomparsa in alto sullo schermo per Windows 11. Si apre e si chiude dall'icona nella tray.

- **Ricerca istantanea** di app e file su tutto il PC (motore [Everything](https://www.voidtools.com/)), con i filtri Tutto / App / Documenti / Immagini / Cartelle
- **Suggeriti**: le app che usi di più a quest'ora e in questo giorno, più gli ultimi file aperti
- **Shortcut** personalizzati: trascina sulla barra file, cartelle o link, oppure usa "+"
- **Azioni di sistema**: Blocca, Sospendi, Riavvia, Spegni, Svuota cestino, Impostazioni, Gestione attività
- **Calcoli nella ricerca**: `15*3`, `10 km in mi`, `20 eur in usd` (tassi BCE); in fondo ai risultati "Cerca su Google"
- **Salute PC**: barre per disco, RAM e CPU; "Libera spazio" per Temp, Cestino e cache dei browser, con anteprima dei GB recuperabili
- **Controlli rapidi**: uscita audio, volume e muto, tema chiaro/scuro, Non disturbare
- **Mini barra** sempre visibile, spostabile ovunque, con le app fissate sulla taskbar (sincronizzate) e le suggerite

## Test
```powershell
dotnet test SamaDash.Tests
```

## Requisiti
- Windows 11, .NET 10 SDK
- Everything: `winget install voidtools.Everything` e `winget install voidtools.Everything.Cli`

## Installazione
```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```
Compila il progetto, lo installa in `%LOCALAPPDATA%\Programs\SamaDash`, crea il collegamento nel menu Start e lo avvia. Al primo avvio attiva anche l'avvio con Windows (lo puoi disattivare dal menu della tray).

## Uso
| Azione | Come |
|---|---|
| Aprire/chiudere | clic sull'icona nella tray (o avvia SamaDash dal menu Start) |
| Aprire il risultato | Invio o clic |
| Aprire la cartella del file | Ctrl+Invio |
| Altre opzioni | tasto destro su un elemento |
| Tenerla aperta (es. per trascinare file) | puntina in alto a destra |
| Chiudere | Esc o clic fuori dalla barra |

## Dati
I dati restano solo su questo PC, in `%LOCALAPPDATA%\SamaDash`:
- `shortcuts.json`: gli shortcut, modificabili anche a mano
- `usage.json`: le abitudini d'uso, cioè i secondi in primo piano per app, divisi per giorno e ora
- `errors.log`: gli eventuali errori
