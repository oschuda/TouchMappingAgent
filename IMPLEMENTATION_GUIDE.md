# Industrial Compliance Implementation Guide

A step-by-step guide for implementing compliance requirements in Touch-Mapping Agent.

## 📋 Quick Start

### When Adding a New Feature:

1. **Define the Request/Response DTOs in `Contracts/`**
   ```csharp
   public record MyFeatureRequest(string Parameter1, int Parameter2);
   public record MyFeatureResponse(bool Success, string? Message);
   ```

2. **Validate Input in Service Handler**
   ```csharp
   // Step 1: Deserialize safely
   var request = SecureJsonDeserializer.DeserializeSecure<MyFeatureRequest>(json);
   
   // Step 2: Validate
   if (string.IsNullOrEmpty(request.Parameter1))
       throw new ArgumentException("Parameter1 is required");
   
   // Step 3: Process
   var result = ProcessFeature(request);
   
   // Step 4: Log
   ComplianceAuditLogger.LogCriticalAction(
       "MY_FEATURE",
       request.Parameter1,
       success: result.Success,
       errorMessage: result.Message);
   ```

3. **Call from IPC Handler (See Integration Example)**
   - Use `ComplianceRequestHandler` pattern
   - Catch all exceptions
   - Always log via `ComplianceAuditLogger`

---

## 🔍 Validation Patterns

### Pattern 1: Validate Identifiers

```csharp
// Wrong: No validation
public void ProcessDevice(string deviceId)
{
    // Device could be "..\..\system32\config"
}

// Correct: Use MappingValidator
public void ProcessDevice(string deviceId)
{
    MappingValidator.ValidateIdentifier(deviceId, nameof(deviceId));
    // Now safe: path traversal prevented
}
```

### Pattern 2: Validate Collections

```csharp
// Wrong: Process all items blindly
foreach (var device in devices)
{
    ProcessDevice(device);
}

// Correct: Validate each item
foreach (var device in devices)
{
    try
    {
        MappingValidator.ValidateHidDevice(device);
        ProcessDevice(device);
    }
    catch (UnauthorizedAccessException ex)
    {
        Logger.LogWarning(ex, "Device {DeviceId} not whitelisted", device.DevicePath);
        // Continue with next device (resilience)
    }
}
```

### Pattern 3: Add to Whitelist

```csharp
// Admin operation to add trusted vendor
public static void ApproveNewVendor(ushort vendorId)
{
    try
    {
        MappingValidator.AddAllowedVendorId(vendorId);
        
        // Log the action (ISO 27001 A.8.15)
        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.WhitelistModified,
            vendorId.ToString("X4"),
            success: true);
    }
    catch (Exception ex)
    {
        ComplianceAuditLogger.LogCriticalAction(
            AuditActions.WhitelistModified,
            vendorId.ToString("X4"),
            success: false,
            errorMessage: ex.Message);
        throw;
    }
}
```

---

## 🛡️ Error Handling Patterns

### Pattern 1: Resilient Device Loop (MVO)

```csharp
// WRONG: One bad device crashes everything
foreach (var device in devices)
{
    ProcessDevice(device);  // If this throws, loop stops
}

// CORRECT: Each device isolated
foreach (var device in devices)
{
    try
    {
        ProcessDevice(device);
    }
    catch (Exception ex)
    {
        Logger.LogWarning(ex, "Failed to process device {Id}", device.Id);
        // Continue with next device
    }
}
```

### Pattern 2: Exponential Backoff (MVO)

```csharp
// WRONG: Immediate retry or nothing
try
{
    await ConnectToDeviceAsync();
}
catch
{
    await ConnectToDeviceAsync();  // Immediate retry, can hammer the device
}

// CORRECT: Exponential backoff with jitter
int retries = 0;
const int maxRetries = 5;
while (retries < maxRetries)
{
    try
    {
        await ConnectToDeviceAsync();
        break;
    }
    catch (Exception ex) when (IsTransient(ex))
    {
        retries++;
        var backoffMs = 500 * (int)Math.Pow(2, retries - 1);
        Logger.LogWarning("Retrying in {BackoffMs}ms (attempt {Retry}/{Max})",
            backoffMs, retries, maxRetries);
        await Task.Delay(backoffMs);
    }
}
```

### Pattern 3: Safe Error Response (OWASP)

```csharp
// WRONG: Expose internal details
catch (Exception ex)
{
    return new Response(false, ex.ToString());  // Stack trace exposed!
}

// CORRECT: Generic message + internal logging
catch (Exception ex)
{
    Logger.LogError(ex, "Detailed error: {Message}", ex.Message);
    ComplianceAuditLogger.LogCriticalAction(
        "OPERATION_NAME",
        "device_id",
        success: false,
        errorMessage: "Internal error");
    return new Response(false, "An error occurred. Please contact support.");
}
```

---

## 📊 Logging Patterns

### Pattern 1: Log Success

```csharp
var request = SecureJsonDeserializer.DeserializeSecure<MapTouchRequest>(json);
MappingValidator.ValidateMapRequest(request);
var result = await ProcessMappingAsync(request);

ComplianceAuditLogger.LogCriticalAction(
    AuditActions.SaveMapping,      // Action
    request.DeviceId,              // Device ID
    success: result.Success);       // Status

return result;
```

### Pattern 2: Log Failure with Context

```csharp
try
{
    await ProcessAsync(request);
}
catch (UnauthorizedAccessException ex)
{
    ComplianceAuditLogger.LogCriticalAction(
        AuditActions.UnauthorizedAccess,
        request.DeviceId,
        success: false,
        errorMessage: "Device not whitelisted");  // Will be sanitized
    throw;
}
```

### Pattern 3: Ensure Event Source Exists

```csharp
// During service installation or startup
public static void InitializeEventLog()
{
    ComplianceAuditLogger.EnsureEventSourceExists();
    ComplianceAuditLogger.LogCriticalAction(
        AuditActions.ServiceStartup,
        "SERVICE",
        success: true);
}
```

---

## 🔐 IPC Patterns

### Pattern 1: Create Secure Server

```csharp
// During service startup
var pipeServer = SecureNamedPipeFactory.CreateSecureServerPipe();
await pipeServer.WaitForConnectionAsync(cancellationToken);
// Handle client with proper security
```

### Pattern 2: Connect Safely (Client)

```csharp
// In ViewModel or client service
try
{
    var pipe = await SecureNamedPipeFactory.CreateSecureClientPipeAsync(timeout: 5000);
    // Connection verified and identity checked
}
catch (UnauthorizedAccessException ex)
{
    StatusMessage = "Service not available or identity verification failed.";
    Logger.LogError(ex, "IPC connection failed");
}
```

---

## ✅ Testing Patterns

### Test 1: Input Validation

```csharp
[Fact]
public void ValidateMapRequest_RejectsPathTraversal()
{
    var request = new MapTouchRequest("..\\..\\system32", "device");
    
    var ex = Assert.Throws<ArgumentException>(
        () => MappingValidator.ValidateMapRequest(request));
    
    Assert.Contains("contains invalid path characters", ex.Message);
}

[Fact]
public void ValidateMapRequest_AllowsValidInput()
{
    var request = new MapTouchRequest("MONITOR_001", "HID_12345");
    
    // Should not throw
    MappingValidator.ValidateMapRequest(request);
}
```

### Test 2: Whitelist Enforcement

```csharp
[Fact]
public void ValidateHidDevice_RejectsNonWhitelistedVendor()
{
    var device = new HidDeviceInfo(
        "/dev/hidraw0",
        "Unknown Device",
        vendorId: 0xFFFF,  // Not in whitelist
        productId: 0x0001);
    
    var ex = Assert.Throws<UnauthorizedAccessException>(
        () => MappingValidator.ValidateHidDevice(device));
    
    Assert.Contains("not in the approved whitelist", ex.Message);
}
```

### Test 3: Error Handling

```csharp
[Fact]
public async Task ProcessMappingAsync_ContinuesOnDeviceError()
{
    var devices = new[] { goodDevice, badDevice, anotherGoodDevice };
    var processedCount = 0;
    
    foreach (var device in devices)
    {
        try
        {
            await ProcessDeviceAsync(device);
            processedCount++;
        }
        catch
        {
            // Continue
        }
    }
    
    // Should process 2 out of 3 (skip the bad one)
    Assert.Equal(2, processedCount);
}
```

---

## 🚀 Deployment Checklist

- [ ] **Service Installer** creates event source
- [ ] **Service Account** runs as SYSTEM (built-in)
- [ ] **Config Files** have restricted ACLs
- [ ] **Log Directory** exists with cleanup policy
- [ ] **Whitelist** includes approved manufacturers
- [ ] **Firewall Rules** documented (if applicable)

---

## 📞 Getting Help

1. **Questions about validation?** → See [MappingValidator.cs](./TouchMappingAgent.Service/Validation/MappingValidator.cs)
2. **IPC security?** → See [SecureNamedPipeFactory.cs](./TouchMappingAgent.Service/IPC/SecureNamedPipeFactory.cs)
3. **Audit logging?** → See [ComplianceAuditLogger.cs](./TouchMappingAgent.Service/Logging/ComplianceAuditLogger.cs)
4. **Resilience?** → See [ResilientHardwareWatcher.cs](./TouchMappingAgent.Service/Hardware/ResilientHardwareWatcher.cs)
5. **General security?** → See [SECURITY.md](./SECURITY.md)

---

Last Updated: 2024-05-21
