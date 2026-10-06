# Security Architecture

This document describes the security architecture of Touch-Mapping Agent, aligned with IEC 62443-4-2, ISO 27001, and NIS2 directives.

## 🏛️ Design Principles

### 1. Defense in Depth
- **Multiple validation layers:** Input validation (client) + Schema validation (service) + Business logic validation
- **Principle of Least Privilege:** Service runs as SYSTEM; clients run as standard user
- **Fail Secure:** Errors result in DENY, not ALLOW

### 2. Secure by Design
- **No implicit trust:** Every request verified, even from admin clients
- **Cryptographic integrity:** (Future) HMAC signing of IPC messages
- **Immutable records:** All models are C# records (cannot be mutated)

### 3. Defense in Depth - Error Handling
- **Granular exception handling:** Each operation has its own try-catch
- **No information leakage:** Stack traces never exposed to users
- **Audit trail:** All failures logged with context

---

## 🔐 Threat Model

### Threats Addressed

| Threat | Mitigation | Standard |
|--------|-----------|----------|
| Unauthorized device mapping | USB VID/PID whitelist, IPC ACL | IEC 62443-4-2 |
| Malformed input | `SecureJsonDeserializer`, `MappingValidator` | CRA / IEC 62443 |
| Privilege escalation | Named Pipe ACLs, identity verification | CR-2.1 |
| Denial of Service (USB crash) | `ResilientHardwareWatcher`, exponential backoff | MVO 2023/1230 |
| Information disclosure | Error sanitization, PII removal in logs | ISO 27001 A.8.15 |
| Replay attacks | (Future) Timestamp validation, nonce-based IPC | NIS2 |
| Man-in-the-middle (IPC) | Service identity verification, ACLs | CR-2.1 |

### Out of Scope

- Network-based attacks (application runs locally only)
- Kernel-mode exploits (Windows OS responsibility)
- Physical attacks on machine
- Supply chain compromise

---

## 📊 Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                  WPF Client Application                      │
│                   (Runs as Standard User)                   │
│                                                              │
│  MonitorMappingViewModel                                    │
│  ├─ No Win32 APIs                                           │
│  ├─ No Registry access                                      │
│  └─ IPC via INamedPipeClient                                │
└────────────────────────┬────────────────────────────────────┘
                         │
                  ╔══════════════╗
                  ║  Named Pipes ║
                  ║   (Message   ║
                  ║    Mode)     ║
                  ╚══════════════╝
                         │
┌────────────────────────▼────────────────────────────────────┐
│              Windows Service Backend                         │
│                (Runs as SYSTEM)                             │
│                                                              │
│  SecureNamedPipeFactory (ACL Protected)                     │
│  │                                                          │
│  ├─ ComplianceRequestHandler                               │
│  │  ├─ SecureJsonDeserializer (Step 1: Input)             │
│  │  ├─ MappingValidator (Step 2: Validate)                │
│  │  ├─ Business Logic (Step 3: Process)                    │
│  │  └─ ComplianceAuditLogger (Step 4: Audit)              │
│  │                                                          │
│  ├─ ResilientHardwareWatcher                               │
│  │  ├─ Granular error handling (per-device)               │
│  │  ├─ Exponential backoff (retry logic)                  │
│  │  └─ Polling fallback (resilience)                      │
│  │                                                          │
│  └─ Windows Event Log                                       │
│     └─ ComplianceAuditLogger entries                       │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔒 Specific Security Controls

### Input Validation (CRA / IEC 62443-4-2)

**Location:** `TouchMappingAgent.Service/Validation/`

**Pattern:**
```csharp
// 1. Deserialize safely
var request = SecureJsonDeserializer.DeserializeSecure<MapTouchRequest>(json);

// 2. Validate structure
MappingValidator.ValidateMapRequest(request);

// 3. Validate business rules
MappingValidator.ValidateHidDevice(device);

// 4. Process (now safe)
ProcessMapping(request);
```

**Validations:**
- ✅ No path traversal (`\` or `/` rejected)
- ✅ No null bytes (`\0` rejected)
- ✅ No control characters (ASCII < 32 rejected)
- ✅ Length constraints (max 256 chars)
- ✅ USB VID/PID whitelist enforcement
- ✅ Display dimensions range checks (0 to 7680x4320)
- ✅ Refresh rate range (30-240 Hz)

### IPC Security (IEC 62443-4-2 CR-2.1)

**Location:** `TouchMappingAgent.Service/IPC/SecureNamedPipeFactory.cs`

**Access Control List (ACL):**
```
✅ SYSTEM (S-1-5-18)        → Full Control
✅ Administrators             → Read/Write
❌ Everyone (S-1-1-0)        → Explicitly Denied
❌ Interactive Users         → Denied (implicit)
```

**Connection Flow:**
1. Client initiates connection
2. Server accepts connection
3. **Identity Verification:** `VerifyServiceIdentity()` checks:
   - Service runs as SYSTEM (SID S-1-5-18)
   - Prevents spoofing attacks
   - Prevents man-in-the-middle

### Audit Logging (ISO 27001 A.8.15 / NIS2)

**Location:** `TouchMappingAgent.Service/Logging/ComplianceAuditLogger.cs`

**Event Log Structure:**
```
[2024-05-21T14:32:10.123Z] User: [SERVICE] | Action: SAVE_MAPPING | 
DeviceId: \\?\hid#{...} | Status: SUCCESS
```

**Log Cleanup (PII Sanitization):**
- File paths: `C:\Path\To\File` → `[PATH]`
- Email addresses: `user@example.com` → `[EMAIL]`
- SIDs: `S-1-5-21-...` → `[SID]`
- Registry paths: `HKEY_LOCAL_MACHINE\...` → `[REGKEY]`

**Audit Actions Logged:**
- `SERVICE_STARTUP` / `SERVICE_SHUTDOWN`
- `SAVE_MAPPING` / `DELETE_MAPPING`
- `START_ADVANCED_REPAIR`
- `LOAD_CONFIGURATION`
- `UNAUTHORIZED_ACCESS`
- `WHITELIST_MODIFIED`

### Fault Tolerance (MVO 2023/1230)

**Location:** `TouchMappingAgent.Service/Hardware/ResilientHardwareWatcher.cs`

**Resilience Strategy:**
```
Watch USB Devices
    ↓ (Error)
    → Retry with Exponential Backoff
    → Max 5 retries (500ms → 30s max)
    ↓ (Still failing)
    → Switch to Polling Mode
    ↓ (Polling also fails)
    → Log error, continue (never crash)
```

**Per-Device Error Isolation:**
- Each device has its own try-catch
- One device failure ≠ all devices fail
- Transient errors (I/O, timeout): Retried
- Permanent errors (access denied): Logged and skipped

---

## 🛡️ Implementation Checklist

See [COMPLIANCE.md](./COMPLIANCE.md) for detailed checklist.

---

## 🔄 Security Review Process

1. **Code Review:** Every PR reviewed for compliance violations
2. **Automated Scanning:** CodeQL analysis on each commit
3. **Manual Testing:** Security-focused test cases
4. **Penetration Testing:** Annual external review
5. **Incident Response:** Documented process for security issues

---

## 📚 Standards & References

- **IEC 62443-4-2:** Implementation requirements for DevOps
- **ISO 27001 A.8.15:** Access logging and event logging
- **NIS2 (EU):** Cybersecurity resilience directive
- **MVO 2023/1230:** Cyber Resilience Act (EU)
- **CRA:** Additional requirements for secure deserialization
- **OWASP:** Desktop application security principles

---

## 🔐 Future Enhancements

- [ ] HMAC signing of IPC messages (replay attack prevention)
- [ ] Encrypted configuration files (data at rest)
- [ ] TLS for network communication (if enabled)
- [ ] Hardware key binding (TPM support)
- [ ] Attestation of service integrity

---

Last Updated: 2024-05-21
Reviewed By: [Security Team]
Next Review: 2024-08-21

