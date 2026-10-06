# Compliance & Security Checklist

This checklist ensures your Touch-Mapping Agent meets IEC 62443-4-2, ISO 27001, NIS2, and MVO 2023/1230 requirements.

## 🔐 Input Validation & Deserialization (CRA / IEC 62443)

- [ ] **All JSON deserialization uses `SecureJsonDeserializer`**
  - Validate: Never use `dynamic` or `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true`
  - Check: `DeserializeSecure<T>` is used, not `Deserialize<T>`
  - Example:
    ```csharp
    // ✅ Correct
    var request = SecureJsonDeserializer.DeserializeSecure<MapTouchRequest>(json);
    
    // ❌ Wrong
    var request = JsonSerializer.Deserialize<MapTouchRequest>(json);
    ```

- [ ] **All input data is validated via `MappingValidator`**
  - Check: `MappingValidator.ValidateMapRequest()` called before processing
  - Check: `MappingValidator.ValidateHidDevice()` called for new devices
  - Check: `MappingValidator.ValidateMonitor()` called for monitor info

- [ ] **Whitelist of USB Vendor IDs is maintained**
  - Verify: Only approved manufacturers can be mapped
  - Check: `AllowedVendorIds` is updated when adding new devices
  - Audit: All whitelist changes are logged via `ComplianceAuditLogger`

---

## 🛡️ Access Control & IPC Security (IEC 62443-4-2 / CR-2.1)

- [ ] **Named Pipe uses `SecureNamedPipeFactory`**
  - Verify: Server pipe created with `CreateSecureServerPipe()`
  - Verify: ACLs restrict to SYSTEM and Administrators only
  - Verify: Everyone SID is explicitly denied

- [ ] **Client identity is verified on connection**
  - Check: `VerifyServiceIdentity()` is called
  - Verify: Service runs as SYSTEM (SID S-1-5-18)
  - Test: Unauthorized users cannot connect

- [ ] **Pipe uses message mode, not stream mode**
  - Verify: `PipeTransmissionMode.Message` is set
  - Verify: `PipeOptions.Asynchronous` is set (never blocks UI)

---

## 📊 Audit Logging (ISO 27001 A.8.15 / NIS2)

- [ ] **Windows Event Log is configured**
  - Task: Run `ComplianceAuditLogger.EnsureEventSourceExists()` during installation
  - Verify: Event source "TouchMappingAgent" exists in Windows Event Log
  - Verify: Application log contains audit entries

- [ ] **All critical actions are logged**
  - Check: `START_ADVANCED_REPAIR` logged when repair starts
  - Check: `SAVE_MAPPING` logged when configuration saved
  - Check: `DELETE_MAPPING` logged when configuration deleted
  - Check: `SERVICE_STARTUP` / `SERVICE_SHUTDOWN` logged
  - Check: `UNAUTHORIZED_ACCESS` logged on violations

- [ ] **Log entries contain required fields**
  - Verify: Timestamp (UTC) is included
  - Verify: User session is included (without PII)
  - Verify: Action name is specific
  - Verify: Status (SUCCESS/FAILURE) is clear
  - Verify: Device ID is included

- [ ] **No PII or sensitive data in logs**
  - Scan: No file paths (C:\...) in logs
  - Scan: No email addresses in logs
  - Scan: No SIDs or registry keys in logs
  - Scan: Error messages are sanitized via `SanitizeErrorMessage()`

---

## 🚨 Robustness & Fault Tolerance (MVO 2023/1230)

- [ ] **Hardware watcher never crashes the service**
  - Verify: `ResilientHardwareWatcher` is used
  - Test: Unplug USB device mid-operation → service continues
  - Test: Simulate EMI noise → service continues
  - Test: Device returns invalid data → service continues

- [ ] **Granular error handling in place**
  - Check: Each device has its own try-catch
  - Check: No catch block re-throws to service level
  - Check: Transient errors are retried (exponential backoff)
  - Check: Permanent errors are logged and skipped

- [ ] **Retry logic uses exponential backoff**
  - Verify: Initial backoff = 500ms
  - Verify: Max backoff = 30 seconds
  - Verify: Max retries = 5 attempts
  - Verify: Polling fallback after exhausting retries

- [ ] **Service stays responsive under adverse conditions**
  - Test: High USB device churn → service remains responsive
  - Test: Network latency → service doesn't block UI
  - Test: System resource constraints → service degrades gracefully

---

## 🔍 Code Review Checklist

- [ ] **No direct registry modifications in ViewModels**
  - Scan: No `RegistryKey.OpenSubKey()` in MVVM code
  - Verify: All registry access is in Service layer only

- [ ] **No Win32 P/Invoke in ViewModels**
  - Scan: No `[DllImport]` in MVVM code
  - Verify: Win32 APIs are in Service layer only

- [ ] **All ViewModels are `public partial class`**
  - Check: MVVM source generators work correctly
  - Verify: `@ObservableProperty` and `@RelayCommand` generate expected members

- [ ] **No stack traces in error messages to users**
  - Scan: No `ex.ToString()` exposed to UI
  - Verify: Error messages are generic and sanitized

---

## 📋 Documentation

- [ ] **Security Architecture Document**
  - Created: [SECURITY.md](./SECURITY.md)
  - Includes: Data flow diagrams, threat model, mitigation strategies

- [ ] **Compliance Mapping Document**
  - Created: [COMPLIANCE.md](./COMPLIANCE.md)
  - Maps: IEC 62443, ISO 27001, NIS2 controls to code locations

- [ ] **API Documentation**
  - Generated: XML comments for all public APIs
  - Verify: `<summary>`, `<param>`, `<returns>`, `<exception>` documented

---

## 🧪 Testing Requirements

- [ ] **Unit Tests for Validators**
  - Test: Invalid input rejected (path traversal, null bytes, etc.)
  - Test: Whitelist enforcement
  - Test: Boundary conditions (max lengths, ranges)

- [ ] **Integration Tests for IPC**
  - Test: Client connects successfully
  - Test: Unauthorized client rejected
  - Test: Concurrent clients handled
  - Test: Large payloads handled

- [ ] **Security Tests**
  - Test: Replay attack not possible
  - Test: Privilege escalation not possible
  - Test: Information disclosure via error messages prevented

- [ ] **Resilience Tests**
  - Test: USB device unplugged during operation
  - Test: Network latency doesn't crash service
  - Test: Malformed JSON doesn't crash service

---

## 🚀 Deployment Checklist

- [ ] **Service installed with correct permissions**
  - Verify: Service runs as SYSTEM
  - Verify: Binary directory has restricted ACLs

- [ ] **Event source created during installation**
  - Task: Run installer with admin privileges
  - Verify: "TouchMappingAgent" event source exists

- [ ] **Configuration files have restricted ACLs**
  - Verify: Only SYSTEM and Admins can read config
  - Verify: Config file stored in `%ProgramData%` or registry

- [ ] **Log rotation is configured**
  - Verify: Serilog output directory has cleanup policy
  - Verify: Event Log retention is set appropriately

- [ ] **Firewall rules (if any network access)**
  - Verify: Only necessary ports are open
  - Verify: Rules are documented and justified

---

## 📞 Compliance Contacts

- **IEC 62443 Architect:** [Name/Team]
- **ISO 27001 Auditor:** [Name/Team]
- **Security Review:** [Schedule]
- **Incident Response:** [Contact Info]

---

## 📅 Review Cycle

- **Quarterly:** Code review for compliance violations
- **Annually:** Full security audit
- **Ad-hoc:** When regulations change or incidents occur

---

Last Updated: 2024-05-21
