# MultiTouch Agent

Ein .NET 9 Windows-Dienst plus WPF-Tray-Agent, der Touch-Digitizer dauerhaft an bestimmte
physische Monitore bindet — auch dann, wenn Windows das von sich aus nicht kann.

## Welches Problem das löst

Auf der Zielanlage hängen zwei **baugleiche** Touch-Monitore (DM7000) über
DisplayPort-auf-HDMI-Range-Extender an einer NVIDIA-Karte, dazu zwei **baugleiche**
PM1715-Digitizer über USB-Extender. Die Hardware-Messung zeigt:

| Merkmal | Monitor A | Monitor B |
|---|---|---|
| EDID-Hersteller / Produkt | CHR / 35088 | CHR / 35088 |
| Seriennummer | 880 | **880** |
| EDID-Block-Hash | `11DA5360…` | **`11DA5360…`** |
| Grafik-Anschluss | DP-2 | DP-3 |

Die Extender emulieren eine generische EDID, deshalb sind beide Panels für Windows
**identisch**. Die Digitizer melden keinen `locationPath` (die USB-Extender reichen die
Mainboard-Topologie nicht durch), sind also ebenfalls nicht unterscheidbar. Windows hat damit
nach einem Neustart oder einem DisplayPort-Event **kein Kriterium**, um links von rechts zu
trennen — Touch-Eingaben landen auf dem falschen Bildschirm.

Dieser Agent liefert das fehlende Kriterium: eine einmal durch Berührung angelernte Zuordnung,
verankert an der physischen Topologie statt an flüchtigen Windows-Bezeichnern.

## Die drei tragenden Architekturentscheidungen

### 1. Hardware-Anker statt flüchtiger Bezeichner

Weder `\\.\DISPLAYn` (GDI-Name) noch der HID-Interface-Pfad taugen als Schlüssel: Windows
vergibt beide bei jedem Boot neu, und ihre Reihenfolge hängt davon ab, welcher Extender seinen
Hot-Plug-Detect-Handshake zuerst abschließt. Gespeichert wird stattdessen:

```
ParentInstanceId (Touch)              <->  PnP-Gerätepfad (Monitor)
USB\VID_14E1&PID_3508\7&1b9afb93&0&6  <->  \\?\DISPLAY#CHR8910#5&2c72b841&0&UID250116
        ↑ USB-Controller-Port                        ↑ enthält Connector-UID
```

Beim Auflösen wird eine Kaskade durchlaufen — Gerätepfad (`Exact`) → Connector + Target-ID
(`Connector`) → Desktop-Bounds (`BoundsOnly`). Jede Stufe muss **genau einen** Treffer liefern;
bei Mehrdeutigkeit verweigert der Resolver und meldet „neu anlernen" statt zu raten.

Die **Adapter-LUID ist bewusst nicht Teil des Schlüssels** — Windows vergibt sie bei jedem Boot
neu. Sie wird nur zu Diagnosezwecken mitgeführt.

### 2. Connector-Erkennung über die CCD-API

`EnumDisplayDevices` reicht nicht: auf Adapter-Ebene ist `DISPLAY_DEVICE.DeviceString` die
Beschreibung der **Grafikkarte**, also auf jedem Ausgang derselben Karte identisch (empirisch
bestätigt — zwei verschiedene Panels meldeten beide „DisplayLink USB Device").

Deshalb kombiniert [`DisplayEnumerator`](TouchMappingAgent.Shared/Hardware/DisplayEnumerator.cs)
zwei APIs:

| API | liefert |
|---|---|
| `EnumDisplayDevices` / `EnumDisplaySettings` | GDI-Name für `tabcal`, aktueller Modus, Desktop-Position |
| `QueryDisplayConfig` + `DisplayConfigGetDeviceInfo` (CCD) | Connector (`DP-2`), Target-ID, PnP-Gerätepfad, echter Monitorname |

### 3. Session-0-Isolation bestimmt die Aufgabenteilung

Ein Windows-Dienst läuft in Session 0 und kann dort zwei Dinge **nicht**:

* **Den Desktop sehen.** `EnumDisplayDevices` liefert aus dem SYSTEM-Dienstprozess eine leere
  Monitorliste, selbst wenn Monitore angeschlossen sind (empirisch bestätigt).
* **`tabcal.exe` ausführen.** Dessen Kalibrierung braucht UI und eine physische Berührung.

Daraus folgt die Rollenverteilung:

```
┌──────────────────────────── Session 0 ────────────────────────────┐
│  TouchMappingAgent.Service (SYSTEM)                                │
│                                                                    │
│  • MappingStore        — Persistenz der Hardware-Anker (HKLM)     │
│  • ReapplyCoordinator  — entscheidet, was neu angewendet werden    │
│                          muss (Hardware-Generation)                │
│  • ResilientHardwareWatcher — pollt USB-Hotplug                    │
│  • NamedPipeServer     — beantwortet Anfragen (ACL-geschützt)      │
│                                                                    │
│  Sieht KEINE Monitore. Führt KEIN tabcal aus.                     │
└────────────────────────────┬───────────────────────────────────────┘
                             │ Named Pipe — Client ruft an,
                             │ nicht umgekehrt
┌────────────────────────────▼──── Session 1 (interaktiv) ───────────┐
│  TouchMappingAgent.WPF (Benutzer, Tray)                            │
│                                                                    │
│  • DisplayEnumerator   — sieht die Monitore (CCD-API)             │
│  • ReapplyAgent        — pollt den Dienst, führt tabcal aus       │
│  • IdentifyWindow      — Vollbild-Anlernen mit RawInput           │
│  • TrayIconManager     — resident, Beenden nur mit Warnung        │
└────────────────────────────────────────────────────────────────────┘
```

**Warum der Client pollt und nicht der Dienst ruft:** der Dienst ist der Pipe-*Server*, der
Client der Anrufer — der Dienst kann nicht hineinrufen. Der Client fragt alle 15 s und
zusätzlich sofort bei `SystemEvents.DisplaySettingsChanged` (der Fall, der zählt: ein Extender
kommt zurück). Dabei liefert er seine Monitorliste mit, weil der Dienst sie nicht selbst
ermitteln kann.

## Anlern-Flow

Windows kann die Zuordnung nicht erraten, also muss ein Mensch sie einmal zeigen:

1. Der Bediener wählt einen Monitor und startet „Bildschirm anlernen".
2. [`IdentifyWindow`](TouchMappingAgent.WPF/Views/IdentifyWindow.cs) legt sich per
   `SetWindowPos` in **physischen Pixeln** über genau diesen Monitor (WPF-DIPs wären bei
   DPI-Skalierung ≠ 100 % auf dem falschen Schirm gelandet) und zeigt
   „Bitte berühren Sie JETZT diesen Bildschirm".
3. [`RawTouchListener`](TouchMappingAgent.WPF/Services/RawTouchListener.cs) fängt die Berührung
   ab. **Warum RawInput und nicht WPFs `TouchDown`:** letzteres sagt *dass* und *wo* berührt
   wurde, aber nicht *von welchem Gerät* — und genau das ist bei zwei identischen Digitizern
   die ganze Frage. RawInput liefert auf jeder Nachricht ein Gerätehandle, das über
   `GetRawInputDeviceInfo(RIDI_DEVICENAME)` zum HID-Pfad und damit zur `ParentInstanceId` wird.
4. Die Paarung wird an den Dienst geschickt, dort validiert und persistiert.
5. `tabcal.exe` läuft im Client-Kontext und aktiviert die Zuordnung.

Ab da stellt der `ReapplyAgent` sie nach jedem Neustart und jedem DisplayPort-Event
automatisch wieder her.

## Einrichtungs-Assistent

Für die Inbetriebnahme vor Ort: Tray-Menü, Eintrag "Einrichtungs-Assistent...", per CLI mit
`--wizard`, oder über die Option auf der Abschlussseite des Installers.

| Schritt | Erfolgskriterium |
|---|---|
| 1 Rechte- und System-Prüfung | Dienst erreichbar, mindestens ein Monitor erkannt |
| 2 EDID-Strategie | Automatisch, Vorlage oder bewusst übersprungen — Schreibvorgang erfolgreich |
| 3 PnP-Verifizierung | **0 doppelte Display-Identitäten**, jeder Monitor mit eigenem PnP-Pfad |
| 4 Touch-Zuordnung | jeder Monitor angelernt, kein Digitizer doppelt vergeben |
| 5 Kalibrierung | `tabcal` gelaufen **und** Sichtprüfung durch den Techniker bestätigt |
| 6 Abschluss | `setup-report.json` exportiert |

**Der Assistent lässt sich nicht durchklicken.** Jeder Schritt schaltet die Weiter-Schaltfläche
erst frei, wenn seine Bedingung erfüllt ist. Ein Assistent, der am Ende „Inbetriebnahme
abgeschlossen" druckt, obwohl die Sichtprüfung fehlschlug, wäre schlechter als gar keiner — er
läge als unterschriebenes Protokoll zu einer Anlage vor, deren Touch weiterhin auf dem falschen
Bildschirm landet.

Schritt 5 hängt bewusst **nicht** am Exit-Code von `tabcal`. Der sagt nur, dass das Werkzeug
lief — ob der Zeiger unter dem Finger landet, kann nur ein Mensch beantworten.

### Rechte-Architektur

Empirisch auf dem Zielsystem geprüft:

| Schreibziel | Rechte für `Benutzer` | Weg |
|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Enum` (EDID_OVERRIDE) | ReadKey | **Delegation an den Dienst** (läuft als SYSTEM, FullControl) — kein UAC-Prompt |
| `HKLM\SOFTWARE\Microsoft\Wisp\Touch` (tabcal) | ReadKey, Schreiben verweigert | **`runas`** — der Dienst kann es nicht, `tabcal` braucht die interaktive Sitzung |

Der Tray-Client bleibt deshalb im Autostart auf `asInvoker` — ein `requireAdmin`-Manifest
setzte bei jedem Windows-Start eine UAC-Abfrage vor den Agenten. Elevation wird nur dort
angefordert, wo sie unvermeidbar ist: in Schritt 5, per Neustart mit `--wizard`.

### Eine Instanz pro Sitzung

Der Agent lässt sich nur einmal pro Sitzung starten. Ein zweiter Aufruf — Installer-Option,
Desktop-Verknüpfung neben dem Autostart — reicht seine Absicht per benanntem Event an die
laufende Instanz weiter und beendet sich. Ohne das liefen zwei Poller und zwei Prozesse, die
gleichzeitig `tabcal` auf dieselbe Zuordnung ansetzen.
## Tray-Verhalten

| Aktion | Verhalten |
|---|---|
| „X" am Fenster | `Hide()` — der Agent bleibt resident |
| Tray-Doppelklick / „Konfiguration öffnen" | Fenster wieder anzeigen |
| Tray → „Einrichtungs-Assistent…" | Geführte Inbetriebnahme in sechs Schritten |
| Tray → „Beenden" | Warnung, dann Abbruch oder `LogWarning` + Shutdown |
| Start mit `--silent` | kein Fenster, nur Tray-Icon + Re-Application |

Der Agent **muss** laufen, damit die Zuordnung erhalten bleibt — deshalb warnt „Beenden" mit
explizitem Wortlaut, der per Test zeichengenau festgenagelt ist
([`ExitWarningTests`](TouchMappingAgent.Tests/ClientTests/ExitWarningTests.cs)).

## Projektstruktur

```
TouchMappingAgent.Shared/          Modelle, Contracts, Hardware-Enumeration
  Models/                          MonitorInfo, HidDeviceInfo, TouchMapping
  Hardware/                        DisplayEnumerator (CCD), TouchDigitizerEnumerator,
                                   MappingResolver (reine Logik, voll testbar)
  Contracts/                       IPC-DTOs + Pipe-Client

TouchMappingAgent.Service/         Windows-Dienst (SYSTEM, Session 0)
  Services/                        MappingStore, ReapplyCoordinator, BackupService,
                                   AdvancedRepairService, DisplayRefreshService
  Hardware/                        ResilientHardwareWatcher (BackgroundService)
  IPC/                             NamedPipeServer + ACL-Factory
  Integration/                     ComplianceRequestHandler (zentraler Dispatcher)
  Validation/                      MappingValidator, SecureJsonDeserializer

TouchMappingAgent.WPF/             Tray-Agent (Benutzer, Session 1)
  Services/                        TrayIconManager, ReapplyAgent, RawTouchListener,
                                   DiagnosticsExporter, AgentWindowFactory
  Views/                           IdentifyWindow, LogWindow, EdidManagerWindow, HelpWindow,
                                   SetupWizardWindow
  ViewModels/                      MonitorMappingViewModel, EdidManagerViewModel,
                                   SetupWizardViewModel
  Resources/                       EvolvedDesignUI.xaml

Evolved.EdidManager/               EDID-Verwaltung (eigenständiges Modul)
  Edid/                            EdidBlock, EdidMutator, EdidCollisionDetector,
                                   EdidSynthesizer (rein, testbar)
  Templates/                       EdidTemplateStore, InfEdidParser
  Pnp/                             Instanz-ID-Konvertierung, CfgMgr32-Re-Enumeration
  Registry/                        EDID lesen, EDID_OVERRIDE schreiben/entfernen

TouchMappingAgent.Tests/           353 Tests
  HardwareTests/                   Identitätsmodell, Resolver
  ServiceTests/                    Store, Coordinator, Validierung
  ClientTests/                     Diagnose-Logging, Beenden-Wortlaut, Branding, Fensterbau
  EdidTests/                       EDID-Parsing, Mutation, Templates, INF, Synthese
  IntegrationTests/                DI-Composition-Root, Pipe-Routing

assets/                            Ausgelieferte Branding-Assets (generiert)
tools/IconGenerator/               Erzeugt assets/ aus Touchmappingagent.png
```

## EDID-Kollisionsauflösung (`Evolved.EdidManager`)

Der Touch-Mapping-Teil braucht das **nicht** — er ankert am PnP-Gerätepfad, der ohnehin
eindeutig ist. Dieses Modul richtet sich an alle **anderen** Verbraucher auf der Maschine, die
Displays über die EDID-Seriennummer identifizieren (Survey-Werkzeuge, HMI-Software,
Hersteller-Control-Panels) und die beiden DM7000 deshalb als ein einziges Display sehen.

Ablauf:

1. Liest die echte Hardware-EDID jedes Monitors aus
   `HKLM\SYSTEM\CurrentControlSet\Enum\<PnP-Instanz>\Device Parameters\EDID`.
   **Keine fest hinterlegten EDID-Bytes** — alles kommt zur Laufzeit vom Gerät.
2. Gruppiert nach voller Identität (Hersteller + Produktcode + numerische Serial + 0xFF-Text).
3. Klont kollidierende EDIDs im RAM mit eindeutigen Serials und rechnet Byte 127 neu:
   `(256 - (Summe(Bytes[0..126]) % 256)) % 256`.
4. Schreibt den Klon als Windows-EDID-Override und stößt eine PnP-Re-Enumeration an.

### Zwei Details, die über Funktionieren oder Nichtfunktionieren entscheiden

**`EDID_OVERRIDE` ist ein Unterschlüssel, kein Wert.** Windows liest den Override aus:

```
…\Device Parameters\EDID_OVERRIDE
    "0" = REG_BINARY   Block 0
    "1" = REG_BINARY   erster Extension-Block, falls vorhanden
```

Ein flacher Binärwert dieses Namens neben `EDID` wird von niemandem gelesen — der Schreibvorgang
gelänge, und der Monitor meldete weiter seine Werksseriennummer.

**Numerische Kollision allein reicht nicht als Kriterium.** `0x01010101` ist ein verbreiteter
„Seriennummer nicht programmiert"-Platzhalter. Auf dem Entwicklungsrechner melden zwei iiyama
PL2452 genau den, tragen aber unterschiedliche 0xFF-Texte und sind damit unterscheidbar. Das
Modul meldet solche Paare, **verändert sie aber nicht** — sonst würde man reihenweise EDIDs
von Panels umschreiben, die kein Problem haben. Umgeschrieben wird nur, wenn auch der Text
identisch (oder auf beiden Seiten abwesend) ist, wie beim DM7000.

### Extender-Fallback: Template-Store und Synthesizer

Reicht ein Extender gar keine EDID durch, gibt es nichts zu klonen. Dann greift diese Kette,
in dieser Reihenfolge:

1. **Vorlage einspielen** — `.bin`, `.hex` oder ein Windows-Monitortreiber `.inf`. Vorlagen
   liegen in `%ProgramData%\Evolved\EdidTemplates\`; `DM7000_Native.bin` wird beim ersten Start
   angelegt.
2. **Synthetische EDID** — `EdidSynthesizer` baut aus dem aktiven Displaymodus einen
   VESA-1.3-Block mit passendem Detailed Timing Descriptor.

Der `InfEdidParser` liest die `HKR,EDID_OVERRIDE`-AddReg-Zeilen eines Monitortreibers. Das ist
oft die einzige autoritative Quelle für ein Panel hinter einem Extender, weil Hersteller
korrigierte EDIDs genau so veröffentlichen. Mehrere Blöcke einer Sektion werden in
Index-Reihenfolge zu einer EDID zusammengesetzt.

> **Zur synthetischen EDID.** Sie ist ein Notbehelf und als solcher gekennzeichnet
> (Hersteller `EVD`, Produktcode `E01D`). Sie beschreibt den Modus, den Windows gerade fährt,
> enthält aber keine herstellerspezifischen Timings und keine am Panel gemessenen Farbwerte;
> die physische Größe ist aus dem Seitenverhältnis geschätzt. Farbwiedergabe und
> DPI-Skalierung bleiben Näherungen, weitere Modi des Panels sind für Windows unsichtbar.
> Wo eine echte EDID oder ein Herstellertreiber existiert, ist beides die bessere Quelle.

**Jedes Template wird beim Laden validiert** — Header, ganze 128-Byte-Blöcke, korrekte
Prüfsumme pro Block. Template-Dateien sind genau die Stelle, an der ein defekter Block ins
System gelangt: sie werden von Hand editiert, zwischen Maschinen kopiert oder von Werkzeugen
erzeugt, die niemand geprüft hat. Ein Grafiktreiber verwirft eine EDID mit falscher Prüfsumme,
und das übliche Symptom ist ein dunkler Anschluss.

`RepairChecksum` korrigiert eine Prüfsumme auf ausdrückliche Anforderung — bewusst nicht
automatisch beim Laden. Eine falsche Prüfsumme bedeutet meist, dass der Block hand-editiert
wurde; ihn stillschweigend zu „reparieren" würde alles andere Fehlerhafte mit einer Prüfsumme
versehen, die nun behauptet, es sei in Ordnung.

### Bedienung

**Tray-Menü → „EDID & Display-Manager…"** zeigt alle Anschlüsse mit PnP-ID, Seriennummer,
EDID-Status (`Gültig` / `Kollision` / `Extender (keine EDID)` / `Override aktiv`) und bietet
Drag & Drop für Template-Dateien, Kollisionsbehebung, Synthese und das Zurücksetzen von
Overrides.

Weitere Einträge: **„Hilfe & Handbuch…"** und **„Diagnose-Log exportieren"** (Protokolle plus
eine Momentaufnahme der erkannten Hardware in einer Datei).

Über die Named Pipe: `GetEdidStatus`, `GetEdidTemplates`, `ApplyEdidTemplate`,
`SynthesizeEdid`, `ResolveEdidCollisions` (mit `DryRun`) und `RestoreEdidDefaults`.
Schreibzugriffe erfordern Administratorrechte — das prüft das Modul vorab, statt erst beim
ersten Registry-Write zu scheitern.

`RestoreDefaults` entfernt den Override-Schlüssel; die Hardware-EDID bleibt unangetastet.

## Branding

Quellbild ist `Touchmappingagent.png` im Projektroot. Alles in `assets/` wird daraus erzeugt:

```bash
dotnet run --project tools/IconGenerator -- Touchmappingagent.png assets
```

| Asset | Verwendung |
|---|---|
| `touchmappingagent.ico` | EXE-Icons beider Programme, Titelleisten, Tray, Installer, Verknüpfungen |
| `logo.png` | Quelle für die Installer-Bitmaps |
| `installer-header.bmp` / `installer-welcome.bmp` | MUI2-Assistentenseiten |

**Das Icon ist nicht das ganze Bild.** Das Quellbild ist ein quadratisches Banner mit
Wortmarke und Feature-Leiste; auf 16 px skaliert bleibt davon nichts Lesbares übrig. Das Icon
ist der zentrale Bildschirm mit Touch-Ring und Cursor, ausgeschnitten aus dem Banner — nahezu
quadratisch, füllt die Icon-Fläche ohne Letterboxing und bleibt bis 16 px auf hellen wie
dunklen Taskleisten erkennbar. Details in [tools/IconGenerator/README.md](tools/IconGenerator/README.md).

Im Tray zeigt der Agent dieses Icon mit einem kleinen Statuspunkt (grün = Dienst antwortet,
bernstein = Dienst antwortet nicht). Zuvor tauschte der Tray-Manager das Produkt-Icon rund eine
Sekunde nach dem Start gegen ein generisches Windows-Systemsymbol aus — das Icon der Anwendung
war dadurch faktisch nie zu sehen.

## Persistenz-Layout

`HKLM\SOFTWARE\PadaLuma\TouchMappingAgent\Mappings\<escaped ParentInstanceId>`

Ein **Subkey pro Zuordnung**, benannt nach dem Touch-Anker mit `\` → `#` escaped (sonst würde
die Registry aus dem Instanz-Pfad stillschweigend einen dreistufigen Baum machen). Werte:
`MonitorHardwareKey`, `MonitorConnectorLabel`, `MonitorTargetId`, `MonitorBoundsKey`,
`LearnedUtc`, `LastAppliedUtc` und Diagnosefelder.

## Bauen und Testen

```bash
dotnet build TouchMappingAgent.sln -c Release
dotnet test  TouchMappingAgent.sln -c Release

# Installer (benötigt NSIS)
dotnet publish TouchMappingAgent.Service/TouchMappingAgent.Service.csproj -c Release -o publish
dotnet publish TouchMappingAgent.WPF/TouchMappingAgent.WPF.csproj         -c Release -o publish
"C:\Program Files (x86)\NSIS\makensis.exe" installer.nsi
```

Der Installer registriert den Dienst (`start= auto`), legt Startmenü- und Desktop-Verknüpfung
an und trägt den Autostart ein:

```
HKLM\Software\Microsoft\Windows\CurrentVersion\Run
  TouchMappingAgent = "<INSTDIR>\TouchMappingAgent.WPF.exe" --silent
```

## Logging

Beide Prozesse schreiben nach `%ProgramData%\PadaLuma\TouchMappingAgent\logs\`
(`service-<datum>.log`, `client-<datum>.log`, rollierend, 14 Tage) über
`Microsoft.Extensions.Logging` mit Serilog dahinter. Sicherheitsrelevante Aktionen gehen
zusätzlich als Audit-Einträge ins Windows-Event-Log (ISO 27001 A.8.15).

> Es gibt bewusst **keine** projekteigene `ILogger`-Abstraktion. Drei nahezu identische Kopien
> davon in verschiedenen Namespaces waren die Ursache eines Defekts, bei dem der
> DI-Container zur Laufzeit kollabierte, während alle Tests grün blieben — siehe
> [`ServiceCompositionRootTests`](TouchMappingAgent.Tests/IntegrationTests/ServiceCompositionRootTests.cs).

## Bekannte offene Punkte

* **`tabcal.exe`-Argumentsyntax nicht verifiziert.** Die Form
  `LinCal DisplayID=… DeviceKind=touch DevicePath="…" NoValidate` ist übernommen, aber nicht
  gegen `tabcal.exe /?` auf der Zielmaschine geprüft. Sie steht zentral in
  `MappingResolver.BuildTabcalArguments` — genau eine Stelle zum Korrigieren.
* **`ValidateHidDevice` (VID-Whitelist) ist nicht verdrahtet.** Die Methode existiert, wird aber
  auf keinem Produktivpfad aufgerufen. Die VID der Zielhardware (`0x14E1`) ist eingetragen,
  damit eine spätere Aktivierung die Anlage nicht aussperrt.
* **„Erweiterte Reparatur" ruft `tabcal ClearCal`** und löscht damit die Kalibrierung. Der
  `ReapplyCoordinator` stellt sie beim nächsten Poll wieder her, aber der Ablauf ist nicht als
  Absicht dokumentiert.
* **Kollidierende USB-Anker.** Meldet ein Digitizer als Parent eine Seriennummer statt eines
  Portpfads (real beobachtet: `USB\VID_0408&PID_3008\0000`), kollidieren zwei baugleiche
  Geräte. Der Resolver erkennt das und verweigert — die PM1715 der Zielanlage sind
  port-abgeleitet und damit unkritisch.

## Voraussetzungen

.NET 9 SDK · Windows 10/11 oder Server 2022 · NSIS (nur für den Installer)

## Lizenz

MIT


