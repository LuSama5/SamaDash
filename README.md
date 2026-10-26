# SamaDash

Dashboard a scomparsa in alto sullo schermo per Windows 11. Si apre con una scorciatoia (Alt+Spazio), dall'icona nella tray o dalla mini barra.

![Home: brano in riproduzione, app suggerite, salute del PC, controlli rapidi e shortcut](docs/screenshots/home.png)

<details>
<summary>Altre schermate</summary>

**Schermi**: luminosità, contrasto, HDR e frequenza per ogni monitor collegato

![Scheda Schermi](docs/screenshots/schermi.png)

**Calcoli nella ricerca**

![Calcolo nella barra di ricerca](docs/screenshots/ricerca.png)

**Mini barra** sempre visibile, con app fissate, suggerite, musica e accesso agli schermi

![Mini barra](docs/screenshots/minibarra.png)

</details>

- **Ricerca istantanea** di app e file su tutto il PC (motore [Everything](https://www.voidtools.com/)), con i filtri Tutto / App / Documenti / Immagini / Cartelle
- **Suggeriti**: le app che usi di più a quest'ora e in questo giorno, più gli ultimi file aperti
- **Shortcut** personalizzati: trascina sulla barra file, cartelle o link, oppure usa "+"
- **Azioni di sistema**: Blocca, Sospendi, Riavvia, Spegni, Svuota cestino, Impostazioni, Gestione attività
- **Calcoli nella ricerca**: `15*3`, `10 km in mi`, `20 eur in usd` (tassi BCE); in fondo ai risultati "Cerca su Google"
- **Salute PC**: barre per disco, RAM e CPU; "Libera spazio" per Temp, Cestino e cache dei browser, con anteprima dei GB recuperabili
- **Controlli rapidi**: uscita audio, volume e muto, tema chiaro/scuro, Non disturbare
- **Schermi** (scheda dedicata): per ogni monitor collegato luminosità e contrasto via DDC/CI, luminosità WMI sui portatili, oppure oscuramento software se lo schermo non accetta comandi; HDR on/off, frequenza di aggiornamento, modalità di proiezione (come Win+P), Luce notturna. Tutto rilevato in automatico
- **In riproduzione**: brano, copertina e controlli di qualsiasi app che usa i controlli multimediali di Windows (Spotify, browser, lettori), con scelta della fonte se ce n'è più d'una
- **Mini barra** sempre visibile con app fissate sulla taskbar (sincronizzate), suggerite, brano in riproduzione e accesso rapido agli schermi. Trascinala dove vuoi: vicino al bordo in basso la dashboard si apre verso l'alto, sui bordi laterali diventa verticale e la dashboard si apre verso il centro, altrove si apre verso il basso da lì, anche sul secondo monitor
- **Mini barra compatta**: a riposo è una piccola pillola che si espande al passaggio del mouse; si attiva e disattiva dal pulsante sulla barra o dal menu della tray

## Download
Scarica l'ultima versione da **[Releases](https://github.com/LuSama5/SamaDash/releases/latest)**:
- `SamaDash-x.y.z-win-x64.zip`: estrai `SamaDash.exe` in una cartella (es. `%LOCALAPPDATA%\Programs\SamaDash`) e avvialo. Non serve installare nient'altro.
- `SamaDash-x.y.z-win-x64-net10.exe`: versione leggera, richiede il [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

Al primo avvio SamaDash si aggiunge all'avvio con Windows (lo puoi disattivare dal menu della tray). L'exe non è firmato: Windows SmartScreen può mostrare un avviso, clicca "Ulteriori informazioni" → "Esegui comunque".

Per la ricerca dei file serve [Everything](https://www.voidtools.com/):
```powershell
winget install voidtools.Everything; winget install voidtools.Everything.Cli
```

## Requisiti
- Windows 11 (Windows 10 2004+ per la maggior parte delle funzioni)
- Everything per la ricerca dei file (le altre funzioni vanno anche senza)

## Compilare dal codice
Serve il .NET 10 SDK.
```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```
Compila il progetto, lo installa in `%LOCALAPPDATA%\Programs\SamaDash`, crea il collegamento nel menu Start e lo avvia.

Test: `dotnet test SamaDash.Tests`

## Uso
| Azione | Come |
|---|---|
| Aprire/chiudere | Alt+Spazio (modificabile), clic sull'icona nella tray o SamaDash dal menu Start |
| Aprire il risultato | Invio o clic |
| Aprire la cartella del file | Ctrl+Invio |
| Altre opzioni | tasto destro su un elemento |
| Spostare la mini barra | trascinala dalla maniglia; per riportarla in alto: menu della tray → "Riporta la mini barra in alto al centro" |
| Tenerla aperta (es. per trascinare file) | puntina in alto a destra |
| Chiudere | Esc o clic fuori dalla barra |

## Dati
I dati restano solo su questo PC, in `%LOCALAPPDATA%\SamaDash`:
- `shortcuts.json`: gli shortcut, modificabili anche a mano
- `settings.json`: impostazioni (scorciatoia, posizione della mini barra, oscuramento software per schermo)
- `usage.json`: le abitudini d'uso, cioè i secondi in primo piano per app, divisi per giorno e ora
- `errors.log`: gli eventuali errori

## Note
- L'interfaccia è in italiano.
- DDC/CI va attivato nel menu del monitor su alcuni modelli; se non risponde, SamaDash usa l'oscuramento software.
- Il design dell'interfaccia è su Figma (file "SamaDash – Schermi").

## Licenza
[MIT](LICENSE)
