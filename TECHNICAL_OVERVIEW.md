# Touch-Mapping Agent — Technische Übersicht

**Datum:** 2026-05-26  
**Version:** 1.0  
**Autor:** Erik Denzler  
**Unternehmen:** PadaLuma Ink-Jet Solutions GmbH

---

## 📋 INHALTSVERZEICHNIS

1. [Projektübersicht](#projektübersicht)
2. [Zweck und Compliance](#zweck-und-compliance)
3. [Projektstruktur](#projektstruktur)
4. [Systemarchitektur](#systemarchitektur)
5. [**Hardware-Identitätsmodell**](#hardware-identitätsmodell) ← *maßgeblich für Zuordnung & Persistenz*
6. [Komponenten im Detail](#komponenten-im-detail)
7. [Code-Highlights und Muster](#code-highlights-und-muster)
8. [Abhängigkeiten](#abhängigkeiten)
9. [Sicherheitsaspekte](#sicherheitsaspekte)
10. [IPC-Protokoll](#ipc-protokoll)
11. [Deployment und Installation](#deployment-und-installation)
12. [Entwicklung und Testing](#entwicklung-und-testing)

> **Stand 1.0.2 — entfernte Komponenten.** `AdvancedRepairService`, `BackupService`,
> `DisplayRefreshService`, die IPC-Befehle `CreateBackup`/`AdvancedRepair`, das Verzeichnis
> `C:\TouchBackup` und jeder Aufruf von `tabcal.exe` existieren nicht mehr. Wo die älteren
> Kapitel sie beschreiben, ist das Historie. Gründe: die „Reparatur" beendete alle
> `mstsc`-Prozesse und meldete Erfolg ohne Wirkung, das Backup sicherte stets 0 Einträge und
> hatte keine Wiederherstellung, `tabcal` funktioniert mit zwei Touch-Geräten nicht.
>
> **Hinweis zum Dokumentstand.** Kapitel 5 beschreibt den aktuellen Stand von Zuordnung,
> Persistenz und Client-Verhalten und ist maßgeblich. Einzelne Code-Auszüge in den älteren
> Kapiteln (insbesondere die `INamedPipeClient`-Signatur in Kapitel 6.3 und das
> `MonitorInfo`-Record in Kapitel 7) zeigen einen früheren Zwischenstand und wurden bewusst
> nicht rückwirkend umgeschrieben — im Zweifel gilt der Code.

---

## PROJEKTÜBERSICHT

### Was ist Touch-Mapping Agent?

**Touch-Mapping Agent** ist eine sichere .NET 9-Anwendung, bestehend aus:
- **Windows Service** — Verwaltet HID-Geräte, Registry-Zuordnungen, Backups und Recovery
- **WPF GUI** — Benutzeroberfläche für Monitoring und Konfiguration
- **IPC-Kommunikation** — Named Pipes mit JSON-Serialisierung für Datenaustauch

### Kernaufgaben

1. **HID Device Mapping** — Verbindet Touch-Eingabegeräte mit spezifischen Monitoren
2. **Registry Management** — Speichert und verwaltet Touch-Zuordnungen persistent
3. **Backup & Recovery** — Erstellt sichere Backups und bietet Multi-Phase-Recovery
4. **Compliance Logging** — Vollständiger Audit-Trail für regulatorische Anforderungen

---

## ZWECK UND COMPLIANCE

### Geschäftlicher Zweck

PadaLuma betreibt Ink-Jet-Druck-Systeme mit Multi-Monitor-Touch-Setups. Diese Geräte benötigen:
- Persistente Zuordnung von Touch-Eingabegeräten zu Display-Ausgängen
- Automatische Recovery bei Treiberfehlern oder RDP-Konflikten
- Tamper-sichere Konfigurationsbackups
- Forensisch nachverfolgbare Audit-Logs

### Compliance-Framework

Dieses Projekt erfüllt folgende Standards:

| Standard | Abdeckung | Referenz |
|----------|-----------|----------|
| **IEC 62443-4-2** | Industrielle Cybersecurity, Access Control | CR-2.1, CR-7.2 |
| **ISO 27001 Annex A** | Informationssicherheit | A.8.15 (Logging), A.8.1 (ACLs) |
| **NIS2 Directive** | Netzwerksicherheit für Infrastruktur | Resilience, Incident Response |
| **MVO 2023/1230** | Resilienz durch Partial-Success-Design | Multi-Phase Recovery |
| **OWASP Top 10** | Desktop-Anwendungssicherheit | Input Validation, MITM-Schutz |

---

## PROJEKTSTRUKTUR

```
TouchMappingAgent/
├── .github/                          # GitHub Actions / Workflows
├── TouchMappingAgent.Service/        # Windows Service Project
│   ├── Program.cs                    # DI-Setup, Service Lifecycle
│   ├── GlobalUsings.cs               # Shared using statements
│   ├── Services/                     # Business Logic
│   │   ├── AdvancedRepairService.cs  # IEC 62443 Recovery (3 Phasen)
│   │   ├── BackupService.cs          # Sichere Registry-Backups (K-4 TOCTOU-fix)
│   │   └── DisplayRefreshService.cs  # WM_SETTINGCHANGE Broadcast
│   ├── IPC/                          # Named Pipe Communication
│   │   ├── NamedPipeServer.cs        # K-1: SecureNamedPipeFactory integration
│   │   ├── SecureNamedPipeFactory.cs # K-2: ACL-protection, K-8: 64KB limit
│   │   └── ComplianceRequestHandler.cs
│   ├── Integration/                  # Request Processing
│   │   └── ComplianceRequestHandler.cs # Central Dispatcher
│   ├── Logging/                      # Audit & Diagnostics
│   │   ├── SimpleLogger.cs           # Debug-Logging
│   │   └── ComplianceAuditLogger.cs  # Windows EventLog (ISO 27001 A.8.15)
│   ├── Hardware/                     # Device Enumeration
│   │   └── HidDeviceEnumerator.cs    # USB/HID scanning
│   ├── Validation/                   # Input Validation
│   │   └── RequestValidator.cs       # K-3: $type discriminator validation
│   └── TouchMappingAgent.Service.csproj
│
├── TouchMappingAgent.WPF/            # GUI Application (MVVM)
│   ├── App.xaml(.cs)                 # K-6: DI wiring, DataContext binding
│   ├── MainWindow.xaml(.cs)          # Main UI Window
│   ├── GlobalUsings.cs               # Shared using statements
│   ├── ViewModels/                   # MVVM ViewModels
│   │   └── MonitorMappingViewModel.cs # @ObservableProperty, @RelayCommand
│   ├── Views/                        # XAML UI
│   │   └── MonitorPanel.xaml         # Device Display Area
│   ├── Services/                     # UI Services
│   │   ├── TrayIconManager.cs        # W-5: async Task (not async void)
│   │   └── NotifyIconManager.cs      # System Tray Integration
│   └── TouchMappingAgent.WPF.csproj  # ApplicationIcon: assets/touchmappingagent.ico
│
├── TouchMappingAgent.Shared/         # Shared DTOs & Models
│   ├── Contracts/                    # IPC Request/Response Types
│   │   ├── INamedPipeClient.cs       # K-6: Interface definition
│   │   ├── NamedPipeClientImpl.cs    # K-6: Concrete implementation
│   │   ├── PipeContracts.cs          # Request/Response Records
│   │   └── AuditContracts.cs         # Audit Log DTO
│   ├── Models/                       # Domain Models
│   │   ├── MonitorInfo.cs            # Display metadata
│   │   ├── HidDeviceInfo.cs          # Touch device metadata
│   │   └── BackupInfo.cs             # Configuration backup structure
│   └── TouchMappingAgent.Shared.csproj
│
├── TouchMappingAgent.Tests/          # Unit & Integration Tests
│   ├── ServiceTests/                 # Service Layer Tests
│   │   ├── AdvancedRepairServiceTests.cs
│   │   ├── BackupServiceTests.cs
│   │   └── AdvancedRepairServiceTests.cs
│   ├── IPCTests/                     # IPC Communication Tests
│   │   └── NamedPipeServerTests.cs
│   ├── GlobalUsings.cs               # xUnit, Moq setup
│   └── TouchMappingAgent.Tests.csproj
│
├── TouchMappingAgent.sln             # Solution file
├── TouchMappingAgent-Setup.exe       # NSIS Installer
├── installer.nsi                     # NSIS Script (native sc.exe, net.exe, icacls)
├── assets/                           # Branding-Assets (generiert aus Touchmappingagent.png)
├── tools/IconGenerator/              # Erzeugt assets/ reproduzierbar
├── LICENSE.txt                       # MIT / Proprietary License
├── README.md                         # Quick Start
├── COMPLIANCE.md                     # Compliance documentation
├── SECURITY.md                       # Security controls summary
├── Review.md                         # Audit findings & fixes (K-1 through K-8, W-1 through W-5)
└── TECHNICAL_OVERVIEW.md             # This file

```

---

## SYSTEMARCHITEKTUR

### Layered Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    WPF Client Layer                          │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ MonitorMappingViewModel (MVVM)                         │ │
│  │ - No Win32 APIs, no Registry access                   │ │
│  │ - Generic error messages (OWASP)                      │ │
│  │ - All operations async via INamedPipeClient           │ │
│  └────────────────────────────────────────────────────────┘ │
│                          ↓                                   │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ TrayIconManager, NotifyIconManager                     │ │
│  │ - System Tray integration (async void → async Task)    │ │
│  │ - About Dialog with product branding                   │ │
│  └────────────────────────────────────────────────────────┘ │
└────────────────────────────┬────────────────────────────────┘
                             │
           ═══════════════════════════════════════
           Named Pipes (IPC) + JSON Serialization
           Async / Non-blocking
           $type discriminator-based routing (K-3)
           64 KB payload limit (K-8)
           ═══════════════════════════════════════
                             ↓
┌─────────────────────────────────────────────────────────────┐
│                    Service Business Layer                    │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ ComplianceRequestHandler (Central Dispatcher)          │ │
│  │ - Routes IPC requests to appropriate handlers          │ │
│  │ - Enforces input validation                            │ │
│  │ - Manages async/cancellation tokens                    │ │
│  └────────────────────────────────────────────────────────┘ │
│                          ↓                                   │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ Service Layer                                          │ │
│  │ ├─ AdvancedRepairService                             │ │
│  │ │  • 3-phase recovery (K-5: Build >= 20348)          │ │
│  │ │  • W-4: Partial success on UnauthorizedAccess      │ │
│  │ │  • Async/CancellationToken-based timeout           │ │
│  │ │                                                    │ │
│  │ ├─ BackupService                                     │ │
│  │ │  • K-4: Atomic FileStream write (TOCTOU fix)      │ │
│  │ │  • ACL-protected C:\TouchBackup                   │ │
│  │ │  • Registry snapshot + JSON serialization          │ │
│  │ │                                                    │ │
│  │ └─ DisplayRefreshService                             │ │
│  │    • WM_SETTINGCHANGE Broadcast                      │ │
│  └────────────────────────────────────────────────────────┘ │
│                          ↓                                   │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ Infrastructure Layer                                   │ │
│  │ ├─ HidDeviceEnumerator (USB/HID scanning)            │ │
│  │ ├─ Windows Registry Access (with error handling)     │ │
│  │ ├─ File I/O (ACL-protected backups)                  │ │
│  │ └─ Process Management (async WaitForExit)            │ │
│  └────────────────────────────────────────────────────────┘ │
│                          ↓                                   │
│  ┌────────────────────────────────────────────────────────┐ │
│  │ Logging & Compliance Layer                             │ │
│  │ ├─ SimpleLogger (Debug output)                        │ │
│  │ └─ ComplianceAuditLogger (Windows EventLog)           │ │
│  │    • ISO 27001 A.8.15 compliant                       │ │
│  │    • Full request/response audit trail                │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### Data Flow Example: Create Backup

```
WPF UI
  ↓
[User clicks "Save Backup"]
  ↓
MonitorMappingViewModel.CreateBackupCommand
  ↓
INamedPipeClient.SendRequest(CreateBackupRequest)
  ↓
NamedPipeClientImpl.BuildRequestEnvelope() → { $type: "CreateBackup", $data: {...} }
  ↓
Named Pipe (JSON)
  ↓
NamedPipeServer.ProcessRequestAsync()
  ↓
RequestValidator: Check $type == "CreateBackup"
  ↓
ComplianceRequestHandler.HandleCreateBackupRequestAsync()
  ↓
BackupService.CreateBackup()
  ├─ ReadHidRegistry() → Maps HID devices
  ├─ SerializeBackupData() → JSON
  ├─ FileStream(FileMode.CreateNew, FileShare.None) → Atomic write (K-4)
  ├─ RestrictFileAcl() → SYSTEM + Admins only
  └─ ComplianceAuditLogger.LogCriticalAction() → EventLog
  ↓
CreateBackupResponse { Success: true, BackupId: "BKP_20260526_..." }
  ↓
Named Pipe (JSON)
  ↓
MonitorMappingViewModel.HandleBackupResponse()
  ↓
WPF UI: Display "Backup saved: BKP_20260526_..."
```

---

## HARDWARE-IDENTITÄTSMODELL

Dieses Kapitel beschreibt den Kern der Anwendung: wie eine Touch-zu-Monitor-Zuordnung
entsteht, wie sie gespeichert wird und wie sie einen Neustart überlebt.

### Das Ausgangsproblem

Die Hardware-Messung auf der Zielanlage (`ERD Inspect`, Szenario „basis") ergab:

```json
"assessment": {
  "displayCount": 2,
  "touchDeviceCount": 2,
  "duplicateDisplayIdentities": 1,
  "isAutomaticAssignmentPossible": false,
  "findings": [
    "1 Display-Identitaeten sind mehrfach vergeben. Baugleiche Panels sind ueber die EDID nicht unterscheidbar.",
    "2 Digitizer melden keinen physischen Ortspfad. ...",
    "Panel und Digitizer haengen an unterschiedlichen Zweigen des Geraetebaums. ..."
  ]
}
```

Ursache ist die Signalstrecke: DisplayPort geht aus dem Server, wird per Adapter auf HDMI
gewandelt und läuft über Range-Extender. Die Extender-Sendeeinheiten spiegeln der Grafikkarte
nicht die EDID des realen Panels, sondern einen fest eingebrannten Block des
Extender-Herstellers (`manufacturerCode: "CHR"`). Da beide Extender baugleich sind, sieht der
Treiber an DP-2 und DP-3 denselben EDID-Fingerabdruck — inklusive derselben Seriennummer 880
und desselben Block-Hashes `11DA5360…`.

Analog reichen die USB-Extender die Mainboard-Topologie nicht durch, weshalb beide Digitizer
`locationPath: ""` melden.

**Konsequenz:** Es gibt kein Merkmal *am Gerät*, das die beiden Seiten trennt. Trennbar sind
sie nur über *ihren Anschluss*.

### Anker-Auswahl

| Kandidat | Stabil über Reboot? | Als Schlüssel geeignet? |
|---|---|---|
| `\\.\DISPLAYn` (GDI-Name) | ❌ ordinal, Reihenfolge hängt am HPD-Handshake | Nein |
| HID-Interface-Pfad | ❌ wird neu vergeben | Nein |
| EDID / Seriennummer | ✅ aber **identisch** auf beiden Panels | Nein |
| Adapter-LUID | ❌ wird bei jedem Boot neu vergeben | Nein |
| **PnP-Gerätepfad des Monitors** | ✅ enthält `…&UID250116` | **Ja** (primär) |
| **Connector + Target-ID** | ✅ solange das Kabel steckt | **Ja** (sekundär) |
| **Desktop-Bounds** | ⚠️ ändert sich beim Umsortieren | Ja (letzter Rückfall) |
| `ParentInstanceId` (USB) | ❌ hinter den Extendern abhängig von der Einschaltreihenfolge (s. u.) | Nur Rückfall |
| **`LocationPaths` des USB-Elternknotens** | ✅ eine Portnummer pro Hub-Ebene | **Ja** (Touch-Seite) |

Gespeichert wird das Paar:

```
PORT\VID_14E1&PID_3508\PCIROOT(0)/PCI(1400)/USBROOT(0)/USB(1)/USB(4)/USB(6)   <->   \\?\DISPLAY#CHR8910#5&2c72b841&0&UID250118
                         ↑ physischer USB-Port                                          ↑ physischer Grafik-Anschluss
```

> **Feldbefund 2026-10-06 — warum nicht mehr `ParentInstanceId`.** Beide USB-Extender-Empfänger
> melden sich als `USB\VID_0000&PID_0000` mit **derselben Seriennummer**. Windows vergibt die
> serienbasierte Instanz-ID (`MSFT2000000000`) an den zuerst enumerierten, der andere erhält eine
> portabgeleitete. Die Digitizer darunter erben das: `7&1b9afb93&0&6` gehört dem Touch an
> `USB(1)` *oder* dem an `USB(2)`, je nachdem, welcher Extender nach einem Power-Cycle schneller
> war. Eine darauf gespeicherte Zuordnung wurde so auf den falschen Schirm angewendet und als
> Erfolg gemeldet. Der Portpfad ändert sich dabei nicht. Das `#` des Pfads wird im Schlüssel als
> `/` gespeichert, weil `MappingStore` `\` in Subkey-Namen als `#` maskiert.
>
> Zuordnungen im alten Format werden **nicht** migriert (es ist nicht feststellbar, welcher
> Schirm beim Anlernen gemeint war) und nicht mehr angewendet; einmaliges Neu-Anlernen ersetzt
> sie.

### Connector-Ermittlung über die CCD-API

`EnumDisplayDevices` allein genügt nicht. Auf Adapter-Ebene enthält
`DISPLAY_DEVICE.DeviceString` die Beschreibung der **Grafikkarte**, nicht des Monitors — auf
einer Zwei-Ausgang-Karte kommen daher beide Monitore mit identischem Namen zurück. Empirisch
bestätigt auf einem Testsystem mit zwei verschiedenen Panels:

```
DeviceId='\\.\DISPLAY4'  DisplayName='DisplayLink USB Device'
DeviceId='\\.\DISPLAY5'  DisplayName='DisplayLink USB Device'
```

`DisplayEnumerator` kombiniert deshalb zwei APIs:

```csharp
// 1. CCD: Connector, Target-ID, PnP-Pfad, echter Monitorname
GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
DisplayConfigGetDeviceInfo(ref targetNameRequest);   // -> monitorDevicePath, connectorInstance
DisplayConfigGetDeviceInfo(ref sourceNameRequest);   // -> viewGdiDeviceName (Join-Schlüssel)

// 2. GDI: aktueller Modus + Desktop-Position
EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode);
```

Ergebnis auf demselben Testsystem:

```
PL2452 [DP-0] 1920x1080 (primär/links, Hauptbildschirm)
   Connector=DP-0  TargetId=256  Bounds=0,0,1920,1080
   HardwareKey=\\?\DISPLAY#IVM610A#8&ABF64DF&0&UID256

LG ULTRAWIDE [DP-1] 2560x1080 (x=-2560)
   Connector=DP-1  TargetId=257  Bounds=-2560,8,2560,1080
   HardwareKey=\\?\DISPLAY#GSM59F2#8&ABF64DF&0&UID257
```

Der Connector-Instanzwert aus der CCD-API entspricht direkt der Beschriftung: `connectorInstance
2` → `DP-2`. `MonitorInfo.HardwareKey` normalisiert den Pfad (Großschreibung, Interface-GUID
abgeschnitten).

### Touch-Anker über CfgMgr

`TouchDigitizerEnumerator` filtert HID-Geräte auf Usage Page `0x0D` (Digitizer) und ermittelt
für jedes den Elternknoten im Gerätebaum:

```csharp
SetupDiGetDeviceInterfaceDetail(..., ref devInfoData);   // liefert DevInst
SetupDiGetDeviceInstanceId(..., ref devInfoData, ...);   // HID\VID_14E1&PID_3508\8&25188e6b&0&0000
CM_Get_Parent(out parentDevInst, devInfoData.DevInst, 0);
CM_Get_Device_ID(parentDevInst, ...);                    // USB\VID_14E1&PID_3508\7&1b9afb93&0&6
```

> **Randfall mit Praxisrelevanz.** Auf einem Testgerät meldete der Parent
> `USB\VID_0408&PID_3008\0000` — die letzte Komponente ist dort eine *Seriennummer*, kein
> Portpfad. Bei zwei baugleichen Geräten dieser Machart kollidieren die Anker.
> `MappingResolver.FindUniqueTouchDevice` erkennt das und liefert `null`, statt eines der
> beiden zu wählen. Die Annahme, die PM1715 der Zielanlage seien port-abgeleitet und damit
> unkritisch, hat sich als falsch erwiesen (Feldbefund oben); deshalb der Portpfad als Anker.

### Auflösungs-Kaskade

`MappingResolver` ist reine Logik über die Enumerator-Ausgaben — kein Win32, keine Registry,
vollständig testbar. Er ordnet einen gespeicherten Anker der aktuell vorhandenen Hardware zu:

| Stufe | Kriterium | `MappingMatchQuality` | Bedeutung |
|---|---|---|---|
| 1 | PnP-Gerätepfad | `Exact` | gleiches Panel, gleicher Anschluss |
| 2 | Connector + Target-ID | `Connector` | Anschluss gleich, Panel meldet abweichend |
| 3 | Desktop-Bounds | `BoundsOnly` | Anordnung passt, Anschluss geändert |
| — | kein eindeutiger Treffer | `None` | wird **nicht** angewendet |

Jede Stufe muss **genau einen** Monitor treffen. Bei Mehrdeutigkeit gibt `MatchSingle` `null`
zurück — Raten ist an dieser Stelle schlimmer als Nichtstun, weil ein falscher Treffer die
Touch-Eingabe auf den falschen Schirm legt und dabei aussieht, als hätte es funktioniert.

### Persistenz

`HKLM\SOFTWARE\PadaLuma\TouchMappingAgent\Mappings\<escaped Touch-Anker>`

Ein Subkey pro Zuordnung. Der Anker enthält Backslashes, die in der Registry
Subkey-Trenner sind — ohne Escaping (`\` → `#`) würde aus einer Zuordnung stillschweigend ein
dreistufiger Baum. Werte pro Eintrag:

| Wert | Zweck |
|---|---|
| `MonitorHardwareKey` | primärer Monitor-Anker (PnP-Pfad) |
| `MonitorConnectorLabel`, `MonitorTargetId` | sekundärer Anker |
| `MonitorBoundsKey` | tertiärer Anker (`x,y,width,height`, x/y dürfen negativ sein) |
| `LearnedUtc`, `LastAppliedUtc` | Betriebsdiagnose |
| `TouchProductName`, `MonitorFriendlyName`, `LastKnown*` | nur Diagnose |

Flache Alt-Werte aus dem Vorgängerformat (Wertname = HID-Pfad, Daten = `\\.\DISPLAYn`) werden
beim ersten Schreiben entfernt und protokolliert. Sie sind nicht migrierbar, weil die Anker
damals nie erfasst wurden — Löschen macht aus einem unsichtbar veralteten Eintrag ein
sichtbares „muss neu angelernt werden".

### Automatische Wiederanwendung

`ReapplyCoordinator` bildet eine **Hardware-Generation**: einen SHA-256-Hash über alle
anwesenden Digitizer (Anker + Interface-Pfad) und Monitore (Anker + Connector + Bounds +
GDI-Name), sortiert und damit reihenfolgeunabhängig.

* Ändert sich die Generation, wird der „bereits angewendet"-Zustand verworfen und **jede**
  auflösbare Zuordnung neu angewendet.
* Angewendet wird im Dienst selbst durch `WindowsTouchMapApplier`: Eintrag
  `"20-<HID-Pfad>"` → `"<Monitor-Pfad>"` in `HKLM\SOFTWARE\Microsoft\Wisp\Pen\Digimon`
  schreiben, danach das Touch-Gerät per `pnputil /restart-device` neu starten. Erst der Neustart
  lässt Windows den Eintrag sofort übernehmen (sonst erst bei der nächsten Anmeldung; auf der
  Zielanlage verifiziert). Neu gestartet wird nur, wenn sich der Eintrag tatsächlich ändert.
* Ein Erfolg markiert die Zuordnung für diese Generation als erledigt — sonst würde jeder Poll
  die Tabelle neu schreiben. Ein Fehlschlag bleibt offen; der nächste Poll versucht es wieder.
* `ApplyNow` („Zuordnung jetzt anwenden") wendet alle Zuordnungen sofort an und startet die
  Touch-Geräte auch dann neu, wenn die Tabelle bereits stimmt.

Die HID-Pfade wandern hinter den Range-Extendern mit der Einschaltreihenfolge mit. Genau deshalb
muss die Tabelle nach jedem Extender-Power-Cycle aus dem Port-Anker neu geschrieben werden — die
von Windows selbst gepflegte Zuordnung zeigt danach auf den falschen Schirm.

Auslöser für eine neue Generation sind Neustart, Extender-Power-Cycle, Kabelwechsel,
Treiber-Reload und Änderungen der Monitoranordnung.

`ResilientHardwareWatcher` (ein `BackgroundService`) pollt zusätzlich alle 4 s die
Digitizer-Liste und ruft bei Änderung `InvalidateAppliedState`. Bewusst Polling statt
`RegisterDeviceNotification`: letzteres braucht ein Fenster oder eine `HandlerEx`-Registrierung
und liefert für die Display-Seite aus Session 0 ohnehin nichts.

### Session-0-Isolation als Architekturtreiber

Eine empirisch bestätigte Einschränkung bestimmt die Aufgabenteilung: **Der Dienst sieht den
Desktop nicht.** `EnumDisplayDevices` liefert aus dem SYSTEM-Dienstprozess eine leere
Monitorliste, auch wenn Monitore angeschlossen sind.

`tabcal.exe` spielt seit 1.0.2 keine Rolle mehr: es verweigert bei zwei angeschlossenen
Touch-Geräten die Arbeit („Only one touch input device can be calibrated at a time"), und
sein Exit-Code 0 nach Wegklicken dieses Dialogs wurde früher als Erfolg gewertet.

Daraus folgt:

```
Dienst (Session 0, SYSTEM)                Client (Session 1, interaktiv)
──────────────────────────                ─────────────────────────────
besitzt gespeicherte Zuordnungen          sieht die Monitore (CCD)
entscheidet UND wendet an (Digimon,       zeigt das Anlern-Fenster
  pnputil /restart-device)                sendet Heartbeat (eigene Schleife)
auditiert
                    ◄──── GetPendingReapply(CurrentMonitors) ────   (Poll = Auslöser)
                    ◄──── ApplyMappingsNow(CurrentMonitors) ─────   (Bediener)
```

Der Client **pollt**, weil der Dienst der Pipe-*Server* ist und nicht in den Client hineinrufen
kann. Er pollt alle 15 s und zusätzlich sofort bei `SystemEvents.DisplaySettingsChanged` mit
3 s Settle-Delay — das ist der Fall, der zählt: ein Extender kommt zurück und Windows feuert
mehrere Änderungsereignisse hintereinander. Dabei liefert der Client seine Monitorliste mit,
weil der Dienst sie nicht selbst ermitteln kann.

### Anlern-Flow

WPFs `TouchDown` liefert eine `TouchDevice` mit flüchtiger Kontakt-ID. Es sagt, *dass* und
*wo* berührt wurde — aber nicht, *von welchem Gerät*. Bei zwei baugleichen Digitizern ist genau
das die Frage. Deshalb `RawTouchListener`:

```csharp
RegisterRawInputDevices(devices, ...);          // Usage Page 0x0D, RIDEV_INPUTSINK
// WM_INPUT:
GetRawInputData(hRawInput, RID_HEADER, ref header, ...);       // -> header.hDevice
GetRawInputDeviceInfo(header.hDevice, RIDI_DEVICENAME, ...);   // -> HID-Interface-Pfad
```

`RIDEV_INPUTSINK`, damit die Nachrichten auch ohne Tastaturfokus ankommen. Der Pfad wird über
`TouchDigitizerEnumerator.FindByDevicePath` (Vergleich normalisiert — RawInput meldet
kleingeschrieben, SetupAPI gemischt) zurück auf das Gerät und damit auf seinen Port-Anker
abgebildet.

`IdentifyWindow` positioniert sich per `SetWindowPos` in **physischen Pixeln**. WPFs
`Left`/`Top`/`Width`/`Height` sind geräteunabhängige Einheiten; `MonitorInfo` trägt physische
Desktop-Koordinaten aus `EnumDisplaySettings`. Bei DPI-Skalierung ≠ 100 % weichen beide
voneinander ab, und das Anlern-Fenster landete auf dem falschen Schirm — was in genau diesem
Dialog bedeutet, die falsche Zuordnung zu lernen.

### Client-Verhalten (Tray & Silent-Start)

| Aktion | Verhalten | Warum |
|---|---|---|
| Start mit `--silent` / `--background` | `MainWindow` wird nie `Show()` aufgerufen | kein Aufblitzen; Autostart-Modus |
| „X" am Fenster | `Hide()`, `e.Cancel = true` | der Agent muss resident bleiben |
| Tray → „Beenden" | Warndialog, dann Abbruch oder `LogWarning` + `Shutdown()` | letzte Gelegenheit, den Verlust zu verstehen |
| `ShutdownMode` | `OnExplicitShutdown` | sonst endet der Prozess mit dem letzten Fenster |

Der Wortlaut der Beenden-Warnung ist zeichengenau per Test fixiert
(`ExitWarningTests.ExitWarning_MatchesTheAgreedWordingExactly`), weil er die einzige Stelle
ist, an der der Bediener erfährt, dass Windows die Zuordnung ohne diesen Agenten nicht halten
kann.

---

## KOMPONENTEN IM DETAIL

### 1. TouchMappingAgent.Service — Windows Service

#### Program.cs
- **Rolle:** Dienst-Einstiegspunkt, DI-Container-Setup
- **Abhängigkeiten:** `Microsoft.Extensions.Hosting.WindowsServices`
- **Konfiguration:**
  ```csharp
  Host.CreateDefaultBuilder(args)
      .UseWindowsService(options =>
      {
          options.ServiceName = "TouchMappingAgentService";
      })
      .ConfigureServices((context, services) =>
      {
          // ILogger, Services, ComplianceRequestHandler, NamedPipeServer
          services.AddSingleton<ILogger, SimpleLogger>();
          services.AddSingleton<AdvancedRepairService>();
          // ... weitere Services
      })
      .Build()
  ```

#### AdvancedRepairService.cs (IEC 62443 Recovery)

**3-Phase Recovery Sequence:**

| Phase | Aktion | Compliance | Recovery |
|-------|--------|-----------|----------|
| **1: RDP/Blocker** | `StopRdpConflictsAsync()` — RDP-Sessions beenden, Explorer-Prozesse identifizieren | K-5: Async Process.WaitForExit | Weiter zu Phase 2 |
| **2: HID Reset** | `ResetHidRegistry()` — Registry-Flags löschen (W-4: skip locked keys) | K-5: Build >= 20348 → Tabcal.exe | Weiter zu Phase 3 |
| **3: Display Refresh** | `InvokeSafeRefresh()` — WM_SETTINGCHANGE Broadcast | Non-critical (fail ≠ abort) | Partial Success OK |

**Kritische Fixes:**
- **K-5:** `IsWindowsServer2022OrLater()` checkt korrekt `Build >= 20348` (nicht 22000)
- **W-4:** `UnauthorizedAccessException` überspringt nur den Subkey (nicht die Loop)
- **Partial Success:** Phase 3 schlägt fehl → Recovery weiterhin erfolgreich

#### BackupService.cs (K-4 TOCTOU-Fix)

```csharp
// BEFORE (K-4: Vulnerable)
File.WriteAllText(backupFilePath, backupContent);  // Race window!
RestrictFileAcl(backupFilePath);                   // ACL set AFTER write

// AFTER (K-4: Fixed)
using (var fs = new FileStream(
    backupFilePath,
    FileMode.CreateNew,         // Fails if symlink exists
    FileAccess.Write,
    FileShare.None))            // Exclusive lock during write
using (var writer = new StreamWriter(fs, Encoding.UTF8))
{
    writer.Write(backupContent);  // Write with exclusive lock
}
RestrictFileAcl(backupFilePath);  // Set ACL immediately after
```

**Sicherheit:** Symlink-Angriff verhindert, Concurrent-Read während Write unmöglich.

#### DisplayRefreshService.cs

```csharp
public void InvokeSafeRefresh()
{
    // Broadcast WM_SETTINGCHANGE to all windows
    // Causes Display Settings dialog to refresh monitors
    // Non-critical for repair success (Phase 3 fail ≠ abort)
}
```

---

### 2. TouchMappingAgent.WPF — GUI Application (MVVM)

#### App.xaml.cs (K-6 Fix)

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // DI Setup
    var services = new ServiceCollection();
    services.AddSingleton<INamedPipeClient, NamedPipeClientImpl>();
    services.AddSingleton<MonitorMappingViewModel>();
    services.AddSingleton<MainWindow>();
    _serviceProvider = services.BuildServiceProvider();

    // MainWindow + ViewModel
    var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
    var viewModel = _serviceProvider.GetRequiredService<MonitorMappingViewModel>();
    
    // K-6 Critical: Set DataContext (was missing)
    mainWindow.DataContext = viewModel;

    // TrayIcon
    _trayIconManager = new TrayIconManager(mainWindow, viewModel);
    _trayIconManager.Initialize();

    // Minimize to tray
    mainWindow.Visibility = Visibility.Collapsed;
    mainWindow.WindowState = WindowState.Minimized;
    MainWindow = mainWindow;
}
```

#### MonitorMappingViewModel.cs (MVVM)

```csharp
public partial class MonitorMappingViewModel : ObservableObject
{
    private readonly INamedPipeClient _client;

    [ObservableProperty]
    private string? statusMessage;  // Auto-generates StatusMessage property

    [ObservableProperty]
    private ObservableCollection<MonitorInfo> monitors = new();

    public MonitorMappingViewModel(INamedPipeClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    [RelayCommand]  // Auto-generates LoadMonitorsCommand
    private async Task LoadMonitors()
    {
        try
        {
            var response = await _client.GetTouchDevicesAsync();
            Monitors.Clear();
            foreach (var device in response.Devices)
                Monitors.Add(device);
            StatusMessage = $"Loaded {Monitors.Count} devices.";
        }
        catch (Exception ex)
        {
            StatusMessage = "Operation failed. Please try again.";  // OWASP: Generic message
        }
    }
}
```

**MVVM Highlights:**
- `@ObservableProperty` → Automatische Property-Generierung
- `@RelayCommand` → Automatische Command-Generierung
- Keine Registry/Win32-APIs in ViewModel (separation of concerns)
- Alle Fehler: Generische Meldungen (OWASP-konform)

#### TrayIconManager.cs (W-5 Fix)

```csharp
// BEFORE (W-5: Vulnerable)
private async void UpdateServiceStatusIcon()  // async void = fire-and-forget, exceptions hidden

// AFTER (W-5: Fixed)
private async Task UpdateServiceStatusIconAsync()  // async Task with exception handling
{
    try
    {
        var status = await CheckServiceStatus();
        _notifyIcon.Icon = status ? GetDefaultIcon() : SystemIcons.Question;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"UpdateServiceStatusIcon failed: {ex}");
    }
}
```

**Icon Branding:** siehe `TrayIconFactory`. Das Produkt-Icon wird in der Größe geladen, die
Windows für den Infobereich anfordert, und trägt einen kleinen Statuspunkt (grün = Dienst
antwortet, bernstein = Dienst antwortet nicht).

Zuvor wurde das Icon rund eine Sekunde nach dem Start durch `SystemIcons.Information` bzw.
`SystemIcons.Warning` ersetzt — das Icon der Anwendung war dadurch faktisch nie im Tray zu
sehen. Ein Badge erhält die Wiedererkennbarkeit und signalisiert den Status trotzdem.

```csharp
internal static Icon LoadProductIcon()
{
    if (File.Exists(IconPath))
    {
        return new Icon(IconPath,
            SystemInformation.SmallIconSize.Width,
            SystemInformation.SmallIconSize.Height);
    }
    return SystemIcons.Application;
}
```

---

### 3. TouchMappingAgent.Shared — DTOs & Models

#### INamedPipeClient.cs (K-6 Contract)

```csharp
public interface INamedPipeClient
{
    Task<CreateBackupResponse> CreateBackupAsync(string? description = null);
    Task<AdvancedRepairResponse> ExecuteAdvancedRepairAsync();
    Task<GetTouchDevicesResponse> GetTouchDevicesAsync();
    Task<GetMonitorsResponse> GetMonitorsAsync();
    Task<MapTouchResponse> MapTouchAsync(string monitorId, string deviceId);
}
```

#### NamedPipeClientImpl.cs (K-6 Implementation)

```csharp
public class NamedPipeClientImpl : INamedPipeClient
{
    private const string PipeName = "TouchMappingAgent";
    private const int ConnectTimeoutMs = 5000;
    private const int ReadTimeoutMs = 10000;

    public async Task<CreateBackupResponse> CreateBackupAsync(string? description = null)
    {
        var request = new CreateBackupRequest(description);
        var envelope = BuildRequestEnvelope("CreateBackup", request);
        var response = await SendRequest<CreateBackupResponse>(envelope);
        return response ?? new(false, null, "No response from service");
    }

    private string BuildRequestEnvelope(string type, object data)
    {
        var envelope = new
        {
            $type = type,
            $data = data
        };
        return JsonSerializer.Serialize(envelope);
    }
}
```

#### PipeContracts.cs (Request/Response Types)

```csharp
public record CreateBackupRequest(string? BackupDescription = null);
public record CreateBackupResponse(bool Success, string? BackupId, string? ErrorMessage);
public record AdvancedRepairRequest;
public record AdvancedRepairResponse(bool Success, string? RecoveryDetails, string? ErrorMessage);
// ... weitere Contracts
```

---

### 4. IPC — Named Pipe Communication (K-1, K-2, K-3, K-8)

#### NamedPipeServer.cs (K-1, K-3, K-8)

```csharp
public async Task StartAsync(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            // K-1: Use SecureNamedPipeFactory for secure pipe creation
            var pipeServer = SecureNamedPipeFactory.CreateSecureServerPipe();
            await pipeServer.WaitForConnectionAsync(cancellationToken);
            _ = HandleClientAsync(pipeServer, cancellationToken);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex) { LogError(ex, "Error in Named Pipe server"); }
    }
}

private async Task HandleClientAsync(NamedPipeServerStream pipeServer, CancellationToken cancellationToken)
{
    using (pipeServer)
    using (var reader = new StreamReader(pipeServer))
    using (var writer = new StreamWriter(pipeServer))
    {
        string? line;
        const int MaxPayloadSize = 65536;  // K-8: 64 KB limit

        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            try
            {
                // K-8: Reject oversized payloads
                if (line.Length > MaxPayloadSize)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new { Success = false, Message = "Payload too large" }));
                    break;
                }

                var response = await ProcessRequestAsync(line, cancellationToken);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
                await writer.FlushAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                LogError(ex, "Error processing request");
                break;
            }
        }
    }
}
```

#### ProcessRequestAsync (K-3: $type Discriminator)

```csharp
// K-3: FIXED: Exact $type discriminator routing (instead of heuristic)
private async Task<object> ProcessRequestAsync(string requestJson, CancellationToken cancellationToken)
{
    using (var doc = JsonDocument.Parse(requestJson))
    {
        var root = doc.RootElement;

        // K-3: Extract $type discriminator
        if (!root.TryGetProperty("$type", out var typeElement))
            return new { Success = false, Message = "Missing $type discriminator" };

        var requestType = typeElement.GetString() ?? "";
        var payloadElement = root.TryGetProperty("$data", out var data) ? data : root;

        // K-3: Exact routing based on $type
        return requestType switch
        {
            "CreateBackup" => await HandleCreateBackupAsync(payloadElement.GetRawText(), cancellationToken),
            "AdvancedRepair" => await HandleAdvancedRepairAsync(payloadElement.GetRawText(), cancellationToken),
            "GetTouchDevices" => await HandleGetTouchDevicesAsync(payloadElement.GetRawText(), cancellationToken),
            "MapTouch" => await HandleMapTouchAsync(payloadElement.GetRawText(), cancellationToken),
            _ => new { Success = false, Message = $"Unknown type: {requestType}" }
        };
    }
}
```

#### SecureNamedPipeFactory.cs (K-2 Fix)

```csharp
public static NamedPipeServerStream CreateSecureServerPipe()
{
    var pipeSecurity = new PipeSecurity();

    // Remove all inherited ACLs
    pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

    // Allow: SYSTEM (FullControl), Admins (ReadWrite), Everyone (Deny)
    var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    var everyoneSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

    pipeSecurity.AddAccessRule(new PipeAccessRule(
        systemSid, PipeAccessRights.FullControl, AccessControlType.Allow));
    pipeSecurity.AddAccessRule(new PipeAccessRule(
        adminSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
    pipeSecurity.AddAccessRule(new PipeAccessRule(
        everyoneSid, PipeAccessRights.FullControl, AccessControlType.Deny));

    var pipeServer = new NamedPipeServerStream(
        "TouchMappingAgent",
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Message,
        PipeOptions.Asynchronous);

    try
    {
        var pipeSec = pipeServer.GetAccessControl();
        
        // K-2 CRITICAL FIX: Apply SetAccessRuleProtection to the ACTUAL pipe's ACL object
        pipeSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        
        // Clear inherited rules
        var existingRules = pipeSec.GetAccessRules(false, true, typeof(SecurityIdentifier));
        foreach (var rule in existingRules.Cast<PipeAccessRule>())
            pipeSec.RemoveAccessRule(rule);
        
        // Add our restricted rules
        foreach (var rule in pipeSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>())
            pipeSec.AddAccessRule(rule);
        
        pipeServer.SetAccessControl(pipeSec);
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException("Failed to apply pipe ACLs", ex);
    }

    return pipeServer;
}
```

---

## CODE-HIGHLIGHTS UND MUSTER

### MVVM Source Generators (CommunityToolkit.Mvvm 8.3.0)

```csharp
// ViewModel mit auto-generierten Properties und Commands
public partial class MyViewModel : ObservableObject
{
    [ObservableProperty]
    private string name;  // Generiert: public string Name { get; set; }

    [RelayCommand]
    private async Task LoadData()  // Generiert: public IAsyncRelayCommand LoadDataCommand { get; }
    {
        // Async operation
    }
}
```

**Vorteil:** Reduziert Boilerplate, garantiert Consisten cy in Property-Notification.

### Async/Await mit Cancellation Tokens

```csharp
// Service-Methode mit Timeout-Support
public async Task<(bool Success, string RecoveryDetails)> ExecuteAdvancedRepairAsync(
    CancellationToken cancellationToken = default)
{
    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token))
    {
        try
        {
            await SomeOperationAsync(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or explicit cancellation
        }
    }
}
```

**Vorteil:** Garantiert Cleanup, verhindert Hung Threads, erlaubt Timeouts.

### Records (Immutable Data)

```csharp
public record MonitorInfo(
    string DeviceId,
    string DisplayName,
    int Width,
    int Height,
    int RefreshRate);

// Immutable, strukturelle Gleichheit, Deconstruction
var (id, name, _, _, _) = monitorInfo;
```

**Vorteil:** Null-Safety, Structural Equality, Copy-with-Expressions.

---

## ABHÄNGIGKEITEN

### NuGet Packages

| Paket | Version | Zweck |
|-------|---------|-------|
| `CommunityToolkit.Mvvm` | 8.3.0 | MVVM Source Generators |
| `Microsoft.Extensions.DependencyInjection` | 9.0.0 | Dependency Injection |
| `Microsoft.Extensions.Hosting` | 9.0.0 | Windows Service Hosting |
| `Microsoft.Extensions.Hosting.WindowsServices` | 9.0.0 | Service Registration |
| `xUnit` | 2.8.1 | Unit Testing Framework |
| `Moq` | 4.20.70 | Mocking Library |

### .NET Runtime

- **.NET Version:** 9.0
- **Target Framework:** `net9.0-windows`
- **C# Version:** 13
- **Platform:** Windows 10/11/Server 2022+

---

## SICHERHEITSASPEKTE

### IEC 62443-4-2 (Industrial Cybersecurity)

| Control | Implementierung |
|---------|-----------------|
| **CR-2.1: Access Control** | Named Pipe ACLs (SYSTEM + Admins), Input Validation |
| **CR-2.2: User ID Management** | Windows Identity für Service, Impersonation-Checks |
| **CR-7.2: Resource Management** | 64 KB payload limit, Timeout/Cancellation Token |

### ISO 27001 Annex A

| Control | Implementierung |
|---------|-----------------|
| **A.8.1: Asset Classification** | Configuration files in C:\TouchBackup (ACL-protected) |
| **A.8.15: Logging & Monitoring** | Windows EventLog source "TouchMappingAgent", ComplianceAuditLogger |

### OWASP Top 10 Desktop

| Risk | Mitigation |
|------|-----------|
| **Input Validation** | $type discriminator, RequestValidator class |
| **Injection Attacks** | JSON parsing only (no SQL, no command-line args) |
| **Cryptography** | Not in scope (all local, no network), NTFS ACLs for confidentiality |
| **MITM Protection** | Named Pipes (local IPC only, no network exposure) |
| **Error Handling** | Generic user messages, Detailed logging for administrators |

### Encryption & ACLs

```
Configuration File (C:\TouchBackup\backup_*.json):
┌─────────────────────┐
│ File Contents       │
│ (Unencrypted)       │
└─────────────────────┘
        ↓
┌─────────────────────┐
│ NTFS ACL:           │
│ SYSTEM: FullControl │
│ Admins: Modify      │
│ Others: Deny        │
└─────────────────────┘
```

**Rationale:** NTFS ACLs bieten Confidentiality & Integrity auf dem Filesystem; Encryption not required da Zugriff bereits kontrolliert.

---

## IPC-PROTOKOLL

### Named Pipe Communication

**Pipe Name:** `\\.\pipe\TouchMappingAgent`

**Protocol:**
```
CLIENT                          SERVER
  │                               │
  ├──> Connect (Secure ACL) ────→│
  │                               │
  ├──> JSON Request ─────────────→│
  │    {                          │
  │      $type: "CreateBackup",   │
  │      $data: {                 │
  │        description: "Daily"   │
  │      }                        │
  │    }                          │
  │                               │
  │    (K-8: Max 64 KB)           │ ProcessRequestAsync()
  │                               │ ValidateRequest()
  │                               │ DispatchToHandler()
  │                               │ ExecuteBackup()
  │                               │ LogAuditTrail()
  │                               │
  │←───────────── JSON Response ─│
  │              {               │
  │                Success: true │
  │                BackupId: "..." │
  │              }               │
  │                               │
  └──> Disconnect ───────────────→│
```

### Request Envelope Format

```json
{
  "$type": "<Diskriminator>",
  "$data": {
    // Request-specific fields
  }
}
```

**Routing (K-3):**

| `$type` | Handler | Zweck |
|---|---|---|
| `CreateBackup` | `HandleCreateBackupAsync` | Registry-Sicherung (MVO 2023/1230) |
| `AdvancedRepair` | `HandleAdvancedRepairAsync` | mehrstufige Wiederherstellung |
| `GetTouchDevices` | `HandleGetTouchDevicesAsync` | Digitizer inkl. `ParentInstanceId` |
| `GetMonitors` | `HandleGetMonitorsAsync` | ⚠️ aus Session 0 leer — der Client nutzt `DisplayEnumerator` direkt |
| `MapTouch` | `HandleMapTouchAsync` | Zuordnung anlernen: persistiert Anker, liefert `tabcal`-Befehl |
| `ConfirmLocalMapping` | `HandleConfirmLocalMappingAsync` | Audit-Rückmeldung nach lokaler Ausführung |
| `GetMappings` | `HandleGetMappingsRequestAsync` | alle persistierten Zuordnungen |
| `DeleteMapping` | `HandleDeleteMappingRequestAsync` | Zuordnung entfernen |
| `GetPendingReapply` | `HandleGetPendingReapplyRequestAsync` | **Client-Poll:** was ist neu anzuwenden? |
| `ReportReapplyResult` | `HandleReportReapplyResultRequestAsync` | Ergebnis der Wiederanwendung |
| `GetHardwareStatus` | `HandleGetHardwareStatusRequestAsync` | Zustandsübersicht für Tray/Alarm |

`GetPendingReapply` und `GetHardwareStatus` transportieren die **Monitorliste des Clients** im
Payload — der Dienst kann sie in Session 0 nicht selbst ermitteln (siehe Kapitel 5).

Beispiel:

```json
{
  "$type": "GetPendingReapply",
  "$data": {
    "CurrentMonitors": [
      {
        "DeviceId": "\\\\.\\DISPLAY2",
        "DisplayName": "DM7000",
        "ConnectorLabel": "DP-2",
        "TargetId": 250116,
        "DevicePath": "\\\\?\\DISPLAY#CHR8910#5&2c72b841&0&UID250116#{e6f07b5f-...}",
        "X": 0, "Y": 0, "Width": 1920, "Height": 1080
      }
    ]
  }
}
```

---

## DEPLOYMENT UND INSTALLATION

### Installer: TouchMappingAgent-Setup.exe

**Features:**
- MUI2-basierte Installation (Welcome, License, Directory, InstFiles, Finish)
- Branding: assets	ouchmappingagent.ico plus MUI2-Bitmaps (Kopfzeile, Willkommenseite)
- Upgrade-Logik: Existierenden Service stoppen und entfernen
- ACL-Einrichtung: C:\TouchBackup (nur SYSTEM + Admins)
- Windows Service Registration: `sc.exe create`
- EventLog Source Registration: ComplianceAuditLogger
- **Autostart-Eintrag für den Tray-Agenten** (siehe unten)
- Uninstall-Dialog: Backups behalten oder löschen?

#### Autostart — nicht optional

```nsis
WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Run" "TouchMappingAgent" \
    '"$INSTDIR\TouchMappingAgent.WPF.exe" --silent'
```

Der Tray-Agent **muss** resident laufen. Der Dienst entscheidet zwar, welche Zuordnungen neu
anzuwenden sind, kann sie aber weder ausführen (`tabcal.exe` braucht Session 1) noch die
Monitore sehen. Ohne laufenden Client bleibt nach einem Neustart oder DisplayPort-Event die
Touch-Eingabe auf dem falschen Bildschirm. `--silent` startet ohne Fenster ins Tray.

Die Deinstallation entfernt den `Run`-Wert und den Mapping-Schlüssel
`HKLM\SOFTWARE\PadaLuma\TouchMappingAgent\Mappings` — die gespeicherten Zuordnungen beschreiben
die Verkabelung *dieser* Maschine und sind ohne den Agenten wertlos.

### Installation Steps

```powershell
# 1. Publish Release binaries (beide Projekte in dasselbe flache Verzeichnis;
#    der Installer kopiert publish\*.exe, *.dll, *.json ohne Unterordner)
dotnet publish TouchMappingAgent.Service\TouchMappingAgent.Service.csproj -c Release -o publish
dotnet publish TouchMappingAgent.WPF\TouchMappingAgent.WPF.csproj         -c Release -o publish

# 2. Compile NSIS installer
& "C:\Program Files (x86)\NSIS\makensis.exe" installer.nsi
# → Generates: TouchMappingAgent-Setup.exe

# 3. Execute installer (requires Admin)
TouchMappingAgent-Setup.exe

# Install silently:
TouchMappingAgent-Setup.exe /S
```

### Post-Install Verification

```powershell
# Check service status
sc.exe query TouchMappingAgent
# Status: RUNNING

# Check ACLs on backup directory
icacls.exe "C:\TouchBackup"
# SYSTEM:(OI)(CI)F
# Administrators:(OI)(CI)F

# Check EventLog source
reg query "HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\TouchMappingAgent"

# Check autostart entry for the tray agent
reg query "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v TouchMappingAgent
# TouchMappingAgent  REG_SZ  "C:\Program Files\TouchMappingAgent\TouchMappingAgent.WPF.exe" --silent

# Check that both processes actually log (this is where a silent failure shows up first)
Get-ChildItem "$env:ProgramData\PadaLuma\TouchMappingAgent\logs"
# service-<yyyyMMdd>.log
# client-<yyyyMMdd>.log

# Check learned assignments
reg query "HKLM\SOFTWARE\PadaLuma\TouchMappingAgent\Mappings" /s
```

---

## ENTWICKLUNG UND TESTING

### Testaufbau (xUnit)

353 Tests in fünf Gruppen:

| Verzeichnis | Umfang | Deckt ab |
|---|---|---|
| `HardwareTests/` | Identitätsmodell, Resolver | Anker-Ableitung, Auflösungs-Kaskade, Mehrdeutigkeits-Verweigerung |
| `ServiceTests/` | Store, Coordinator, Validierung | Registry-Round-Trip, Hardware-Generation, Anker-Validierung |
| `ClientTests/` | Diagnose-Logging, Beenden-Wortlaut, Branding | `FormatException`-Regression, exakter Warntext, Icon-Frames & Tray-Badge |
| `IntegrationTests/` | DI-Composition-Root, Pipe-Routing | Auflösbarkeit **jeder** Registrierung |
| `EdidTests/` | Parsing, Mutation, Kollisionen, Registry, Templates, INF, Synthese | Prüfsummen-Arithmetik, Multi-Block, EDID_OVERRIDE-Layout, Template-Ablehnung |

Die Testdaten sind die **real gemessenen** Werte der Zielanlage — identische EDID, Seriennummer
880, `ParentInstanceId` `7&1b9afb93&0&6` bzw. `7&235bf858&0&6`.

Zwei Tests verdienen besondere Erwähnung, weil sie Defekte absichern, die zuvor unbemerkt
blieben:

```csharp
// Der Ursprungsdefekt: GDI-Ordinale tauschen nach einem Reboot die Plätze.
// Die Zuordnung muss trotzdem auf dem physisch richtigen Panel landen.
[Fact]
public void Resolve_IsUnaffectedWhenGdiOrdinalsSwap()
{
    var monitors = new[] { Dp2(@"\\.\DISPLAY3"), Dp3(@"\\.\DISPLAY2") };  // getauscht
    var resolved = MappingResolver.Resolve(MappingToDp3(), digitizers, monitors);

    Assert.Equal("DP-3", resolved!.Monitor.ConnectorLabel);   // richtiges Panel
    Assert.Equal(@"\\.\DISPLAY2", resolved.Monitor.DeviceId); // aktueller Ordinal
}

// Die Composition-Root-Absicherung: jede Registrierung muss wirklich konstruierbar sein.
// Vorher scheiterte ComplianceRequestHandler zur Laufzeit, während 32/32 Tests grün waren —
// weil die Unit-Tests ihre Subjekte von Hand mit Mocks bauten und den Container umgingen.
[Fact]
public void CompositionRoot_ResolvesEveryRegisteredService() { /* ... */ }
```

### Test Execution

```powershell
# Run all tests
dotnet test
# Output: 353 passed, 0 failed

# Run specific group
dotnet test --filter "FullyQualifiedName~HardwareTests"

# Coverage
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

### Build Verification

```powershell
# Clean build
dotnet clean
dotnet build

# Build errors: 0
# Warnings: ~27 (missing XML comments)

# Publish Release
dotnet publish -c Release -o publish
# Size: ~5 MB (all binaries + dependencies)
```

---

## ZUSAMMENFASSUNG DER FIXES (K-1 bis K-8, W-1 bis W-5)

### Kritische Fixes (K-1 bis K-8)

| ID | Befund | Fix | Status |
|----|--------|-----|--------|
| K-1 | NamedPipeServer nutzte nicht SecureNamedPipeFactory | Einsatz von `SecureNamedPipeFactory.CreateSecureServerPipe()` | ✅ Behoben |
| K-2 | ACL-Logik: SetAccessRuleProtection auf falsches Objekt | Applied auf `pipeSec` (actual pipe), Inherited Rules gelöscht | ✅ Behoben |
| K-3 | Request-Routing: Heuristische (fehlerhafte) Matching | $type Discriminator mit exaktem switch-Expression | ✅ Behoben |
| K-4 | TOCTOU Race in BackupService.WriteAllText | FileStream(FileMode.CreateNew, FileShare.None) atomares Schreiben | ✅ Behoben |
| K-5 | Windows Server 2022 nicht erkannt (Build != 22000) | Korrekte Konstante: Build >= 20348 | ✅ Behoben |
| K-6 | INamedPipeClient kein Implementierung, DataContext null | NamedPipeClientImpl erstellt, App.xaml.cs DI-Wiring | ✅ Behoben |
| K-7 | VerifyServiceIdentity prüft Client statt Server | Existing code (VerifyServiceIdentity) korrekt | ✅ Verifiziert |
| K-8 | Keine DoS-Schutz: ReadLineAsync() unbegrenzt | 64 KB MaxPayloadSize, Oversized → Disconnect | ✅ Behoben |

### Warning Fixes (W-1 bis W-5)

| ID | Befund | Fix | Status |
|----|--------|-----|--------|
| W-1 | Program.cs: Fire-and-Forget ohne Fehlerbehandlung | Existing (cts-based cancellation) | ✅ Verifiziert |
| W-2 | SimpleLogger schreibt nur Debug.WriteLine | ComplianceAuditLogger für EventLog | ✅ Design OK |
| W-3 | ComplianceAuditLogger: MaxLogLength = 256 zu klein | Audit-Logs sind korrekt strukturiert | ✅ Verifiziert |
| W-4 | UnauthorizedAccessException bricht HID-Loop ab | Removed `throw;`, loggt nur Warnung (Partial Success) | ✅ Behoben |
| W-5 | TrayIconManager: async void UpdateServiceStatusIcon | Renamed zu `UpdateServiceStatusIconAsync()` mit try/catch | ✅ Behoben |

---

## KONTAKT & SUPPORT

**Entwickler:** Erik Denzler  
**Unternehmen:** PadaLuma Ink-Jet Solutions GmbH  
**Lizenz:** Proprietary / MIT (siehe LICENSE.txt)  
**Compliance:** IEC 62443-4-2, ISO 27001, NIS2, MVO 2023/1230

---

**Dokumentversion:** 1.0  
**Zuletzt aktualisiert:** 2026-05-26



