# Architekturelles Audit-Report: TouchMappingAgent Solution

**Datum:** 2026-05-21 | **Auditor:** Principal .NET Architekt & IT-Security  
**Basis:** IEC 62443-4-2, ISO 27001 A.8.15, NIS2, MVO 2023/1230, OWASP Top 10

---

## 🚨 KRITISCH — Sofortiger Handlungsbedarf vor Deployment

---

### K-1 · `NamedPipeServer.cs` — `SecureNamedPipeFactory` wird NICHT verwendet

**Datei:** `TouchMappingAgent.Service/IPC/NamedPipeServer.cs`, Zeile 42–50

```csharp
// AKTUELL — unsicherer Default-Konstruktor:
var pipeServer = new NamedPipeServerStream(
    PipeName, PipeDirection.InOut, MaxServerInstances,
    PipeTransmissionMode.Message, PipeOptions.Asynchronous);
```

`SecureNamedPipeFactory` existiert mit vollständigen ACLs (SYSTEM FullControl, Admins ReadWrite, Everyone Deny), wird aber im produktiven `NamedPipeServer` **nie aufgerufen**. Die tatsächlich verwendete Pipe erbt Default-ACLs — jeder lokale Benutzer (inkl. Guest-Accounts) kann sich verbinden und beliebige JSON-Requests senden.

**Auswirkung:** Vollständige Umgehung des IEC 62443-4-2 CR-2.1 Access-Control-Modells. Privilege-Escalation-Vektor: Ein Low-Privilege-Prozess kann `AdvancedRepairRequest` senden und Registry-Schreiboperationen auslösen.

---

### K-2 · `SecureNamedPipeFactory.cs` — ACL-Anwendung logisch defekt

**Datei:** `TouchMappingAgent.Service/IPC/SecureNamedPipeFactory.cs`, Zeile 59–73

```csharp
var pipeSec = pipeServer.GetAccessControl();   // <- existierende, vererbte ACL
foreach (var rule in pipeSecurity.GetAccessRules(...))
    pipeSec.AddAccessRule(pipeRule);            // Regeln werden ADDIERT, nicht ersetzt
pipeServer.SetAccessControl(pipeSec);
```

`SetAccessRuleProtection(isProtected: true, preserveInheritance: false)` wurde auf dem lokalen `pipeSecurity`-Objekt gesetzt, **nicht** auf dem aus der Pipe ausgelesenen `pipeSec`. Die Vererbung bleibt aktiv. Die Deny-Everyone-Regel wird über bestehende Allow-Everyone-Regeln (aus der Default-ACL) gelegt — bei WinAPI-ACL-Evaluation gewinnt explizites Deny zwar, aber die saubere Isolation fehlt. Silencer-Block `catch { }` (Zeile 74) versteckt ACL-Fehler vollständig.

---

### K-3 · `NamedPipeServer.cs` — Request-Type-Detection logisch gebrochen (falsches Routing)

**Datei:** `TouchMappingAgent.Service/IPC/NamedPipeServer.cs`, Zeilen 168–195

```csharp
private static bool IsCreateBackupRequest(JsonElement root) =>
    root.TryGetProperty("BackupDescription", out _) ||
    (root.EnumerateObject().Count() == 0); // leeres Objekt matcht IMMER hier

private static bool IsAdvancedRepairRequest(JsonElement root) =>
    root.EnumerateObject().Count() == 0;  // wird nie erreicht für {}

private static bool IsGetTouchDevicesRequest(JsonElement root) =>
    root.EnumerateObject().Count() == 0;  // wird nie erreicht für {}
```

`IsCreateBackupRequest` matcht **jedes leere JSON-Objekt `{}`** zuerst. Da `AdvancedRepairRequest` und `GetTouchDevicesRequest` ebenfalls leere Records sind, werden sie **niemals korrekt geroutet**.

**Konkrete Folgen:**
- `AdvancedRepairRequest {}` → landet in `HandleCreateBackupAsync` → startet unbeabsichtigten Backup
- `GetTouchDevicesRequest {}` → landet in `HandleCreateBackupAsync` → `CheckServiceStatus()` im WPF triggert bei jedem Aufruf eine Backup-Operation auf dem Service-Host
- `AdvancedRepairRequest` kann programmatisch nie ausgelöst werden — die NIS2-Recovery-Funktion ist de facto tot

---

### K-4 · `BackupService.cs` — TOCTOU / Symlink-Attack beim Datei-Schreiben

**Datei:** `TouchMappingAgent.Service/Services/BackupService.cs`, Zeilen 86–88

```csharp
File.WriteAllText(backupFilePath, backupContent); // Datei liegt ms-lang mit offenen Rechten
RestrictFileAcl(backupFilePath);                  // <- Race Window: jeder kann lesen/überschreiben
```

Zwischen `WriteAllText` und `RestrictFileAcl` liegt ein Zeitfenster, in dem die Datei mit vererbten Directory-ACLs existiert. Auf einem Multi-User-Server (Windows Server 2022) kann ein lokaler Nicht-Admin-Prozess:
1. Den Dateiinhalt (Registry-Dump) lesen (Information Disclosure)
2. Die Datei durch einen Symlink ersetzen (Link-Attack → `RestrictFileAcl` setzt ACL auf das Ziel des Symlinks)

Ebenso: `EnsureBackupDirectoryExists()` erstellt erst das Verzeichnis, dann setzt es ACLs — selbes TOCTOU-Fenster auf Verzeichnisebene.

**Fix-Pattern:**
```csharp
// Statt WriteAllText: FileStream mit FileShare.None + vorab erstellter ACL
var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
```

---

### K-5 · `AdvancedRepairService.cs` — `IsWindowsServer2022OrLater()` identifiziert Server 2022 nie korrekt

**Datei:** `TouchMappingAgent.Service/Services/AdvancedRepairService.cs`, Zeile 279–284

```csharp
var osVersion = Environment.OSVersion.Version;
return osVersion.Major >= 11 ||                   // Windows hat kein Major 11
       (osVersion.Major == 10 && osVersion.Build >= 22000); // WS2022 Build = 20348, nicht 22000!
```

- Windows 11: `Major=10, Build=22000` → korrekt erkannt (zweiter Term)
- **Windows Server 2022: `Major=10, Build=20348`** → mit `Build >= 22000` **nicht erkannt** — tabcal wird nie ausgelöst
- Der erste Term `Major >= 11` ist totes Unreachable-Code
- `Environment.OSVersion` liefert unter .NET 9 ohne App Manifest ggf. `10.0.17763` (Compat-Shim) → Methode gibt false zurück, obwohl Server 2022

Korrekte Konstante: `Build >= 20348` für Windows Server 2022.

---

### K-6 · WPF — `INamedPipeClient` hat keine konkrete Implementierung im gesamten Repository

**Datei:** Alle `*.cs`-Dateien in `TouchMappingAgent.WPF/`

```bash
$ grep -r "INamedPipeClient" **/*.cs
# Nur Interface-Definition und ViewModel-Nutzung. Keine Implementierungsklasse.
```

`INamedPipeClient` ist definiert (`Shared/Contracts/INamedPipeClient.cs`), wird vom `MonitorMappingViewModel` injiziert, aber **es existiert keine einzige implementierende Klasse** im Repository. Konsequenzen:
1. `MainWindow.DataContext` wird nie auf ein `MonitorMappingViewModel` gesetzt (kein Wire-Up in XAML oder Code-Behind)
2. `App.xaml.cs:37` → `mainWindow.DataContext as MonitorMappingViewModel` gibt **immer null** zurück → `TrayIconManager` wird nie initialisiert, kein Tray-Icon erscheint
3. **Die WPF-Anwendung ist beim Start vollständig funktionslos** — keine einzige Command-Schaltfläche ist bindable

Die Application startet ohne Fehler (kompiliert), tut aber nichts.

---

### K-7 · `SecureNamedPipeFactory.cs` — `VerifyServiceIdentity()` prüft Client-Identität, nicht Server-Identität

**Datei:** `TouchMappingAgent.Service/IPC/SecureNamedPipeFactory.cs`, Zeilen 124–145

```csharp
private static void VerifyServiceIdentity(NamedPipeClientStream client)
{
    var identity = WindowsIdentity.GetCurrent(); // <- Identität des CLIENTS, nicht des Servers!
    if (identity?.User?.Value != systemSid.Value && !IsRunAsAdmin())
        throw new UnauthorizedAccessException(...);
}
```

`WindowsIdentity.GetCurrent()` gibt die Identität **des aktuellen Prozesses** (WPF-Client) zurück, nicht des verbundenen Pipe-Servers. Der Man-in-the-Middle-Schutz ist wirkungslos: ein Angreifer, der einen gefälschten Pipe-Server betreibt, wird nicht erkannt. Zusätzlich: Admins, die die WPF-App als normaler Benutzer (non-elevated) starten, werden fälschlicherweise mit `UnauthorizedAccessException` abgelehnt.

---

### K-8 · `NamedPipeServer.cs` — Kein Empfangslimit (DoS / OOM)

**Datei:** `TouchMappingAgent.Service/IPC/NamedPipeServer.cs`, Zeile 78

```csharp
while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
```

`ReadLineAsync()` ohne Größenbeschränkung. Ein angreifender Prozess (der die unsichere Pipe connecten kann — siehe K-1) kann eine Payload von 500 MB in einer einzelnen Zeile senden → `OutOfMemoryException` im Service → Service-Absturz. IEC 62443-4-2 CR 7.2 (Resource Management) verletzt.

---

## ⚠ WARNUNG — Architektur-Schwächen / Potenzielle Instabilität

---

### W-1 · `Program.cs` — Dual Fire-and-Forget, Fehler werden verschluckt

**Datei:** `TouchMappingAgent.Service/Program.cs`, Zeilen 83–88

```csharp
_ = host.RunAsync(cts.Token);              // Exception → silent
_ = namedPipeServer.StartAsync(cts.Token); // Exception → silent
await Task.Delay(Timeout.Infinite, cts.Token); // Service läuft "blind"
```

Beide Tasks sind discarded. Wenn `StartAsync` aufgrund eines Pipe-Erstellungsfehlers (Berechtigungsproblem, Name schon belegt) sofort wirft, läuft der Service weiter, akzeptiert aber keine Verbindungen — und niemand bemerkt es. Das verstößt gegen das Prinzip der Fail-Fast-Architecture.

---

### W-2 · `BackupService.cs` / `ComplianceRequestHandler.cs` — `SimpleLogger` schreibt nur `Debug.WriteLine`

**Datei:** `TouchMappingAgent.Service/Integration/ComplianceRequestHandler.cs`, Zeile 278

```csharp
public void LogError(Exception ex, string message, params object?[] args)
{
    System.Diagnostics.Debug.WriteLine($"[ERROR] {message} - {ex.Message}");
}
```

Als Windows Service (SYSTEM-Kontext, kein Debugger attached, keine Konsole) gehen **sämtliche strukturierten Log-Einträge verloren**. Nur der `ComplianceAuditLogger` schreibt in den Windows Event Log. Alle `_logger.LogError()`-Calls im Service-Layer sind im Produktionsbetrieb unsichtbar.

---

### W-3 · `ComplianceAuditLogger.cs` — `MaxLogLength = 256` vernichtet Audit-Daten

**Datei:** `TouchMappingAgent.Service/Logging/ComplianceAuditLogger.cs`, Zeile 17

Recovery-Details aus `AdvancedRepairService` (3 Phasen, `StringBuilder`, mehrzeilig) werden auf 256 Zeichen abgeschnitten. Das widerspricht ISO 27001 A.8.15 (vollständiger Audit-Trail). Kritisch für forensische Nachvollziehbarkeit nach einem Incident.

---

### W-4 · `AdvancedRepairService.cs` — `UnauthorizedAccessException` bricht den gesamten HID-Reset-Loop

**Datei:** `TouchMappingAgent.Service/Services/AdvancedRepairService.cs`, Zeilen 225–233

```csharp
catch (UnauthorizedAccessException ex)
{
    _logger.LogWarning(ex, "Cannot access registry key {DeviceId}", subKeyName);
    throw; // <- bricht die gesamte foreach-Schleife ab
}
```

Eine einzige gesperrte Registry-Subkey (z. B. durch einen aktiven Treiber) bricht Phase 2 vollständig ab und setzt `overallSuccess = false`. Das widerspricht dem expliziten MVO 2023/1230 „Partial Success"-Design. Stattdessen sollte die Exception den einzelnen Eintrag überspringen (wie bei anderen Exceptions im selben Block).

---

### W-5 · `TrayIconManager.cs` — `async void UpdateServiceStatusIcon()`

**Datei:** `TouchMappingAgent.WPF/Services/TrayIconManager.cs`, Zeile 143

```csharp
private async void UpdateServiceStatusIcon()
{
    await CheckServiceStatusAsync(); // Exception hier ist unhandled → AppDomain-Crash
}
```

`async void` als Fire-and-Forget: Exceptions werden nicht gefangen und führen zu einem unbehandelten `UnhandledExceptionEvent`. In einer WPF-Anwendung bedeutet das Application-Crash beim Start, wenn der Service nicht erreichbar ist — genau der häufigste Fall im Deploymentablauf.

---

### W-6 · `NamedPipeServer.cs` — Fire-and-Forget `HandleClientAsync` kann Pipe-Ressource leaken

**Datei:** `TouchMappingAgent.Service/IPC/NamedPipeServer.cs`, Zeile 51

```csharp
_ = HandleClientAsync(pipeServer, cancellationToken);
```

Wenn `HandleClientAsync` wirft, bevor der `using (pipeServer)`-Block die Kontrolle übernimmt, wird `pipeServer` nicht disposed. Unter Last (10 gleichzeitige Verbindungen, alle werfend) akkumuliert sich das.

---

### W-7 · `AdvancedRepairService.cs` — `proc.Kill()` auf möglicherweise bereits beendeten Prozess

**Datei:** `TouchMappingAgent.Service/Services/AdvancedRepairService.cs`, Zeile 173

```csharp
if (!proc.WaitForExit(5000))
    proc.Kill(); // InvalidOperationException wenn proc exited zwischen WaitForExit und Kill
```

Race Condition: In dem ms-langen Zeitfenster zwischen `WaitForExit` und `Kill` kann der Prozess sich selbst beendet haben. Die umgebende `catch` fängt es zwar ab, aber korrekt wäre `if (!proc.HasExited) proc.Kill()`.

---

### W-8 · `MappingValidator.cs` — `AllowedVendorIds` ist nicht thread-safe

**Datei:** `TouchMappingAgent.Service/Validation/MappingValidator.cs`, Zeile 18

```csharp
private static readonly HashSet<ushort> AllowedVendorIds = new() { ... };
```

`HashSet<T>` ist nicht thread-safe. Die public-Methode `AddAllowedVendorId()` kann von mehreren gleichzeitigen IPC-Handler-Tasks aufgerufen werden (da `NamedPipeServer` parallel handelt). Gleichzeitiges Lesen in `Contains()` und Schreiben in `Add()` ist UB.

---

### W-9 · `BackupService.cs` — Keine Backup-Rotation / Disk-Exhaustion-Risiko

**Datei:** `TouchMappingAgent.Service/Services/BackupService.cs`, Zeile 14

```csharp
private const string BackupDirectory = "C:\\TouchBackup";
```

Kein Cleanup alter Backups. Wiederholte Backup-Erstellungen (auch durch fehlgeleitete Requests — siehe K-3) füllen `C:\TouchBackup` ohne Begrenzung. Auf einem Produktionsserver kann dies den System-Drive füllen → DoS für den Gesamtserver.

---

### W-10 · `ComplianceRequestHandler.cs` — `ProcessMappingAsync()` ist ein nie-funktionierender Placeholder

**Datei:** `TouchMappingAgent.Service/Integration/ComplianceRequestHandler.cs`, Zeilen 104–112

```csharp
private async Task<MapTouchResponse> ProcessMappingAsync(MapTouchRequest request, ...)
{
    await Task.Delay(100, cancellationToken);
    return new MapTouchResponse(true, null); // Gibt immer Success zurück ohne zu tun
}
```

`MapTouchRequest` → `MapTouchResponse(true)` ohne jegliche Aktion. Die Whitelist-Validierung in `MappingValidator` wird korrekt aufgerufen, das eigentliche Mapping-Schreiben ist ein `TODO`. Deployment auf Produktionssystem würde dem Admin vortäuschen, Mappings wurden gesetzt.

---

### W-11 · `DisplayRefreshService.cs` — Nicht verwendete P/Invoke-Deklaration

**Datei:** `TouchMappingAgent.Service/Services/DisplayRefreshService.cs`, Zeile 20

```csharp
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
private static extern int SendMessage(int hWnd, int Msg, IntPtr wParam, string lParam);
```

`SendMessage` wird nie aufgerufen (nur `SendMessageTimeout` wird verwendet). Totes P/Invoke macht Code-Reviews schwieriger und ist ein Indicator für Copy-Paste-Schlampigkeit, die Security-Auditoren bei kritischer Infrastruktur sofort auffällt.

---

## 💡 OPTIMIERUNG — Clean-Code / .NET 9 Best Practices

---

### O-1 · `GlobalUsings.cs` (WPF) — `System.Windows.Forms` global importiert erzeugt strukturelle Ambiguität

**Datei:** `TouchMappingAgent.WPF/GlobalUsings.cs`, Zeile 7

```csharp
global using System.Windows.Forms;
```

Dies hat bereits den Build-Fehler `CS0104 'Application' ist mehrdeutig` produziert und musste mit `using Application = System.Windows.Application` gepatcht werden. WinForms-Namespaces sollten ausschließlich in `TrayIconManager.cs` lokal importiert werden, nicht global für das gesamte WPF-Projekt.

---

### O-2 · `BackupService.cs` — `JsonSerializerOptions` nicht gecacht

**Datei:** `TouchMappingAgent.Service/Services/BackupService.cs`, Zeilen 204–207

```csharp
return JsonSerializer.Serialize(backupObject, new JsonSerializerOptions { WriteIndented = true });
```

`new JsonSerializerOptions()` bei jedem Backup-Aufruf. Als `static readonly`-Field deklarieren.

---

### O-3 · `AdvancedRepairService.cs` — Veraltetes `Substring()` statt Range-Operator

**Datei:** `TouchMappingAgent.Service/Services/AdvancedRepairService.cs`, Zeile 316

```csharp
Guid.NewGuid().ToString("N").Substring(0, 8)  // C# 8+ Stil:
Guid.NewGuid().ToString("N")[..8]
```

---

### O-4 · `NamedPipeServer.cs` — `GetService()` statt `GetRequiredService<T>()`

**Datei:** `TouchMappingAgent.Service/IPC/NamedPipeServer.cs`, Zeilen 218, 254, 283, 310

```csharp
var handler = _serviceProvider.GetService(typeof(ComplianceRequestHandler)) as ComplianceRequestHandler;
if (handler == null) { ... } // 4× manuelle Null-Prüfung
```

`GetRequiredService<ComplianceRequestHandler>()` wirft direkt `InvalidOperationException` mit klarer Botschaft wenn nicht registriert. Spart 4× Null-Check-Boilerplate.

---

### O-5 · `BackupService.cs` — Backup enthält nur `"Class"`-Wert (inhaltlich unvollständig)

**Datei:** `TouchMappingAgent.Service/Services/BackupService.cs`, Zeilen 165–177

```csharp
var class_ = subKey.GetValue("Class", "")?.ToString() ?? "";
if (!string.IsNullOrEmpty(class_)) mappings[subKeyName] = class_;
```

Der Backup liest ausschließlich den `Class`-Wert. Touch-Mapping-Konfigurationen befinden sich in `DeviceParameters\WDF`, nicht in `Class`. Als Disaster-Recovery-Mechanismus (MVO 2023/1230) ist dieser Backup inhaltlich wertlos für das eigentliche Mapping-Restore.

---

### O-6 · `App.xaml.cs` — Keine konkrete `INamedPipeClient`-Instanz, kein DI-Container, kein ViewModel-Setup

**Datei:** `TouchMappingAgent.WPF/App.xaml.cs`

Die WPF-App hat keinen DI-Container und keine Factory für `MonitorMappingViewModel`. Das MainWindow setzt kein `DataContext`. Für das Deployment wird ein `Microsoft.Extensions.DependencyInjection`-Setup in `App.OnStartup()` oder ein `ViewModelLocator`-Pattern benötigt.

---

## Priorisierte Maßnahmen-Checkliste

| Priorität | Nr. | Datei | Maßnahme |
|-----------|-----|-------|----------|
| P0 — Deploy-Blocker | K-1 | `NamedPipeServer.cs` | Auf `SecureNamedPipeFactory.CreateSecureServerPipe()` umstellen |
| P0 — Deploy-Blocker | K-3 | `NamedPipeServer.cs` | Request-Type-Detection mit Discriminator-Property oder expliziter `$type`-Kennung neu bauen |
| P0 — Deploy-Blocker | K-6 | `WPF/` | `INamedPipeClient` konkret implementieren + DI-Wiring + DataContext in MainWindow setzen |
| P0 — Deploy-Blocker | K-5 | `AdvancedRepairService.cs` | Build-Konstante korrigieren: `Build >= 20348` statt `22000` |
| P1 — Sicherheit | K-2 | `SecureNamedPipeFactory.cs` | `SetAccessRuleProtection` auf `pipeSec` anwenden; leeren `catch`-Block entfernen |
| P1 — Sicherheit | K-4 | `BackupService.cs` | Atomares Datei-Schreiben mit `FileShare.None`-FileStream oder Pre-ACL-Ansatz |
| P1 — Sicherheit | K-7 | `SecureNamedPipeFactory.cs` | `VerifyServiceIdentity()` entfernen oder korrekt via Impersonation implementieren |
| P1 — Sicherheit | K-8 | `NamedPipeServer.cs` | `ReadLineAsync` durch length-limited Read ersetzen (max. 64 KB) |
| P2 — Stabilität | W-1 | `Program.cs` | `RunAsync` und `StartAsync` als awaitable Tasks mit Fehlerbehandlung |
| P2 — Stabilität | W-2 | `ComplianceRequestHandler.cs` | `SimpleLogger` durch Windows Event Log-Logger oder Serilog ersetzen |
| P2 — Stabilität | W-5 | `TrayIconManager.cs` | `async void UpdateServiceStatusIcon` zu `async Task` + Exception-Handling |
| P2 — Stabilität | W-4 | `AdvancedRepairService.cs` | `UnauthorizedAccessException` in HID-Loop: `continue` statt `throw` |
| P3 — Clean-Code | O-1 | `GlobalUsings.cs` | `System.Windows.Forms` aus GlobalUsings entfernen (nur lokal in TrayIconManager) |
| P3 — Clean-Code | O-5 | `BackupService.cs` | Backup-Inhalt korrigieren: relevante Registry-Werte für Touch-Mapping lesen |

---

## Gesamtbewertung

Das architektonische Fundament ist solide (MVVM, DI-Struktur, Phase-Isolation, OWASP-konforme Fehlermeldungen an den Client). Die kritischen Befunde **K-1, K-3 und K-6** verhindern jedoch, dass die Anwendung in ihrer aktuellen Form **überhaupt funktioniert**:

- **K-1:** IPC-Kanal ist für jeden lokalen Benutzer offen — IEC 62443-Sicherheitsmodell vollständig ausgehebelt
- **K-3:** Request-Routing ist durch Logikfehler gebrochen — NIS2-Recovery-Funktion de facto tot, jeder Health-Check löst einen Backup aus
- **K-6:** Die WPF-Anwendung ist vollständig unbindbar — kein funktionierender IPC-Client, kein DataContext, kein Tray-Icon

Diese drei Punkte sind **harte Deploy-Blocker** und müssen vor jedem NSIS-Installer-Build behoben sein.

---

## ✅ Fix-Protokoll

### Session 1 — Kritische & sicherheitsrelevante Befunde (K-1 bis K-8, W-4, W-5)

| Befund | Datei | Status | Maßnahme |
|--------|-------|--------|----------|
| K-1 | `NamedPipeServer.cs` | ✅ behoben | Auf `SecureNamedPipeFactory.CreateSecureServerPipe()` umgestellt |
| K-2 | `SecureNamedPipeFactory.cs` | ✅ behoben | `SetAccessRuleProtection` auf `pipeSec` angewendet; leeren `catch`-Block entfernt |
| K-3 | `NamedPipeServer.cs` | ✅ behoben | Request-Routing mit `$type`-Discriminator neu implementiert |
| K-4 | `BackupService.cs` | ✅ behoben | Atomares Datei-Schreiben mit `FileShare.None`-FileStream |
| K-5 | `AdvancedRepairService.cs` | ✅ behoben | `Build >= 20348` für Windows Server 2022 |
| K-6 | `WPF/` | ✅ behoben | `NamedPipeClientImpl` implementiert + DI-Wiring + DataContext gesetzt |
| K-7 | `SecureNamedPipeFactory.cs` | ✅ behoben | `VerifyServiceIdentity()` korrigiert (Impersonation-basiert) |
| K-8 | `NamedPipeServer.cs` | ✅ behoben | Max. 64 KB Empfangslimit eingeführt |
| W-4 | `AdvancedRepairService.cs` | ✅ behoben | `UnauthorizedAccessException` → `continue` statt `throw` |
| W-5 | `TrayIconManager.cs` | ✅ behoben | `async void` → `async Task` mit Exception-Handling |
| W-9 | `BackupService.cs` | ✅ behoben | Backup-Rotation (max. 10 Backups) eingeführt |
| O-2 | `BackupService.cs` | ✅ behoben | `JsonSerializerOptions` als `static readonly` gecacht |
| O-5 | `BackupService.cs` | ✅ behoben | Backup liest `DeviceParameters\WDF`-Registry-Werte |

### Session 1 — Nachträglich entdeckte Befunde

| Befund | Datei | Status | Maßnahme |
|--------|-------|--------|----------|
| N-1 · Pipe-Name-Mismatch | `NamedPipeClientImpl.cs` | ✅ behoben | `PipeName = "TouchMappingAgent"` (war `"TouchMappingPipe"`) |
| N-2 · Async-Deadlock | `NamedPipeClientImpl.cs` | ✅ behoben | Vollständig async, JsonOptions gecacht |

### Session 2 — Compiler-Warnungen

| Warnung | Datei | Status | Maßnahme |
|---------|-------|--------|----------|
| NETSDK1137 | `TouchMappingAgent.Service.csproj` | ✅ behoben | SDK von `Microsoft.NET.Sdk.WindowsDesktop` auf `Microsoft.NET.Sdk` geändert |
| NETSDK1106 | `TouchMappingAgent.Service.csproj` | ✅ behoben | Ungültige Property `UseWindowsFormsApp` entfernt; `OutputType` auf `Exe` korrigiert |
| CS1591 (Tests) | `TouchMappingAgent.Tests.csproj` | ✅ behoben | `<NoWarn>CS1591</NoWarn>` im Test-Projekt hinzugefügt |
| CS1591 (ILogger × 3) | `BackupService.cs`, `ComplianceRequestHandler.cs`, `ResilientHardwareWatcher.cs` | ✅ behoben | XML-Docs auf allen 3 `ILogger`-Interface-Kopien ergänzt |
| CS1591 (SimpleLogger) | `ComplianceRequestHandler.cs` | ✅ behoben | `/// <inheritdoc/>` auf allen 5 Methoden ergänzt |
| CS1591 (Konstruktoren) | `AdvancedRepairService`, `BackupService`, `DisplayRefreshService`, `ResilientHardwareWatcher`, `ComplianceRequestHandler`, `App`, `MainWindow`, `TrayIconManager` | ✅ behoben | XML-Docs auf allen public Konstruktoren ergänzt |
| CS1591 (Methoden) | `NamedPipeClientImpl.cs` | ✅ behoben | `/// <inheritdoc/>` auf `IsConnectedAsync()` und `SendAsync<TResponse>()` |

### Aktueller Build-Status

```
Build succeeded.
    0 Warnung(en)
    0 Fehler
```

**14/14 Unit-Tests grün.**

### Offene Befunde (nicht behoben)

| Befund | Priorität | Bemerkung |
|--------|-----------|-----------|
| W-1 · Fire-and-Forget in `Program.cs` | P2 | Stabilität im Fehlerfall |
| W-2 · `SimpleLogger` → `Debug.WriteLine` | P2 | Produktions-Logs unsichtbar |
| W-3 · `MaxLogLength = 256` in `ComplianceAuditLogger` | P2 | Audit-Trail unvollständig |
| W-6 · Pipe-Ressource-Leak bei `HandleClientAsync` | P2 | Unter Last akkumulierend |
| W-7 · Race bei `proc.Kill()` | P2 | `HasExited`-Check fehlt |
| W-8 · `AllowedVendorIds` nicht thread-safe | P2 | `HashSet` → `ConcurrentDictionary` oder `lock` |
| W-10 · `ProcessMappingAsync` ist Placeholder | P2 | Mapping-Schreiben nicht implementiert |
| W-11 · Totes P/Invoke `SendMessage` | P3 | Code-Hygiene |
| O-1 · `System.Windows.Forms` in GlobalUsings | P3 | Namespace-Ambiguität |
| O-3 · `Substring()` statt Range-Operator | P3 | C# 8+ Best Practice |
| O-4 · `GetService()` statt `GetRequiredService<T>()` | P3 | DI-Robustheit |
| O-6 · ViewModelLocator-Pattern | P3 | DI-Vervollständigung |
