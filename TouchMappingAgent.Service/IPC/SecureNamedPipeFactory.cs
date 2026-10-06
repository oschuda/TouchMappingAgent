using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace TouchMappingAgent.Service.IPC;

/// <summary>
/// Factory for creating secure Named Pipes with strict ACLs (IEC 62443-4-2 CR-2.1).
/// Restricts access to SYSTEM, Administrators, and any authenticated logged-on user (console,
/// RDP, or otherwise) — anonymous and Guest connections remain denied.
/// </summary>
public static class SecureNamedPipeFactory
{
    private const string PipeName = "TouchMappingAgent";
    private const int BufferSize = 4096;

    /// <summary>
    /// Creates a Named Pipe server with restricted ACLs.
    ///
    /// ARCHITECTURE NOTE: the original ACL here (SYSTEM + Administrators only, explicit
    /// Deny-Everyone) made the pipe completely unreachable for the WPF client's actual,
    /// intended usage: a normal tray app run by a regular logged-on user, not elevated. A
    /// non-elevated process — even one belonging to the Administrators group, since UAC
    /// filters that SID out of a non-elevated token — got UnauthorizedAccessException on
    /// every single IPC call (confirmed empirically: even opening a server-side pipe
    /// instance under a non-elevated identity was denied). This originally granted access to
    /// the well-known "Interactive" SID with ReadWrite only, so ordinary console desktop use
    /// works — that SID was later found to exclude RDP sessions specifically (see the RDP
    /// FOLLOW-UP note below) and was replaced with "Authenticated Users". Anonymous
    /// connections and Guest remain denied by omission — no explicit Deny-Everyone rule is
    /// used, since .NET's ObjectSecurity classes auto-canonicalize ACE order (explicit Deny
    /// before explicit Allow), which for a Deny-Everyone rule risks shadowing the
    /// SYSTEM/Administrators Allow rules that are supposed to take precedence; a protected
    /// DACL that simply omits a principal already denies it by default, no explicit Deny
    /// needed.
    ///
    /// TRADE-OFF FOR FOLLOW-UP: this now lets ANY authenticated logged-on user (not just
    /// admins) invoke every IPC command, including ForceConsoleSessionReset (logs the console
    /// off) and ApplyMappingsNow (restarts the digitizers) — acceptable to make the tool functional at
    /// all, but on a shared/multi-user Windows Server 2022 box you may want to replace the
    /// Authenticated Users SID below with a dedicated local/AD group instead of "every
    /// authenticated user".
    ///
    /// RDP FOLLOW-UP: the Interactive well-known SID (S-1-5-4) originally used here is only
    /// added to a token for a physical console logon (LogonType 2). An RDP session
    /// authenticates as LogonType 10 ("RemoteInteractive") and carries the separate "Remote
    /// Interactive Logon" SID (S-1-5-14) instead — never S-1-5-4. So the Interactive-only ACL
    /// silently denied every RDP-session client outright (UnauthorizedAccessException on
    /// connect), which is what surfaced as spurious "UNAUTHORIZED_ACCESS" pipe-server errors
    /// during RDP access. Rather than also enumerating S-1-5-14 (and every other logon-type
    /// SID Windows might use), this grants the "Authenticated Users" well-known SID
    /// (S-1-5-11) instead: present on every real logon token regardless of logon type
    /// (console, RDP, network, batch, service), while still excluding ANONYMOUS LOGON and
    /// Guest — which is exactly the "any authenticated local user, not literally everyone"
    /// boundary this pipe needs.
    /// </summary>
    /// <returns>A configured NamedPipeServerStream with security restrictions.</returns>
    public static NamedPipeServerStream CreateSecureServerPipe()
    {
        var pipeSecurity = new PipeSecurity();

        // Remove inheritance from parent - start with clean slate
        pipeSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        // Grant SYSTEM (service account) full control
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            systemSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        // Grant Administrators read/write only
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            adminSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // Grant any authenticated logged-on user (console OR RDP session, or any other real
        // logon type) read/write. This is what makes the non-elevated WPF tray client able to
        // talk to the service at all — see the method doc comment above for why the previous
        // SYSTEM/Administrators-only ACL blocked every normal (non-elevated) client connection
        // outright, and why "Authenticated Users" replaced the narrower "Interactive" SID that
        // silently excluded RDP sessions specifically.
        var authenticatedUsersSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            authenticatedUsersSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        // Apply the ACL atomically at creation (Win32 CreateNamedPipe's lpSecurityAttributes)
        // via NamedPipeServerStreamAcl.Create, instead of the previous two-step "create with
        // default ACL, then retrofit via GetAccessControl()/SetAccessControl()" approach.
        // .NET's NamedPipeServerStream has no constructor overload accepting PipeSecurity
        // directly (verified against .NET 9: only the ACL-less overloads exist) — ACL support
        // lives in this separate static factory instead. The retrofit approach also required
        // WRITE_DAC on the just-created handle, which is not guaranteed for the creator of a
        // pipe — confirmed empirically: SetAccessControl() threw UnauthorizedAccessException
        // here for a normal user identity even though that same identity had just created the
        // pipe. Whether the two-step approach happened to work depended on the creating
        // process's privilege level (SYSTEM has enough implicit privilege to bypass this; a
        // non-elevated process does not) — NamedPipeServerStreamAcl.Create removes that
        // dependency and the TOCTOU window where the pipe briefly existed with the OS default
        // ACL before being locked down.
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous,
            BufferSize,
            BufferSize,
            pipeSecurity);
    }

    /// <summary>
    /// Client connects to the Named Pipe and verifies service identity (impersonation protection).
    /// IEC 62443-4-2: Client must verify it's connecting to the legitimate service.
    /// </summary>
    /// <param name="timeout">Connection timeout in milliseconds.</param>
    /// <returns>A connected NamedPipeClientStream to the service.</returns>
    /// <exception cref="UnauthorizedAccessException">If service identity verification fails.</exception>
    /// <exception cref="TimeoutException">If connection times out.</exception>
    public static async Task<NamedPipeClientStream> CreateSecureClientPipeAsync(int timeout = 5000)
    {
        var client = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await client.ConnectAsync(timeout);

            // Verify service identity (impersonation protection - IEC 62443-4-2)
            VerifyServiceIdentity(client);

            return client;
        }
        catch (TimeoutException)
        {
            client.Dispose();
            throw new TimeoutException($"Failed to connect to '{PipeName}' service within {timeout}ms.");
        }
        catch (UnauthorizedAccessException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Failed to connect to service '{PipeName}'.", ex);
        }
    }

    /// <summary>
    /// Verifies that the Named Pipe server is running under the correct identity (SYSTEM or Admin).
    /// K-7 fix: Uses GetNamedPipeServerProcessId (P/Invoke) to retrieve the actual server process,
    /// then checks that process's owner — not WindowsIdentity.GetCurrent() which is always the client.
    /// Prevents man-in-the-middle attacks and spoofing attempts.
    /// </summary>
    private static void VerifyServiceIdentity(NamedPipeClientStream client)
    {
        try
        {
            // P/Invoke: get the process ID of the server side of the connected pipe
            if (!NativeMethods.GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid))
            {
                var err = Marshal.GetLastWin32Error();
                throw new UnauthorizedAccessException(
                    $"GetNamedPipeServerProcessId failed (Win32 error {err}). " +
                    "Cannot verify service identity.");
            }

            // Open the server process to query its token
            using var serverProcess = System.Diagnostics.Process.GetProcessById((int)serverPid);
            using var processHandle = serverProcess.Handle == IntPtr.Zero
                ? throw new UnauthorizedAccessException("Cannot open server process.")
                : (IDisposable?)null; // handle already accessed via serverProcess.Handle

            // Use OpenProcessToken + GetTokenInformation to get the process owner SID
            if (!NativeMethods.OpenProcessToken(serverProcess.Handle,
                    NativeMethods.TOKEN_QUERY, out var tokenHandle))
            {
                throw new UnauthorizedAccessException(
                    "OpenProcessToken failed. Cannot verify service identity.");
            }

            using (tokenHandle)
            {
                var identity = new WindowsIdentity(tokenHandle.DangerousGetHandle());

                var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                var adminSid  = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

                bool isAccepted =
                    identity.User?.Equals(systemSid) == true ||
                    identity.Groups?.Any(g => g.Value == adminSid.Value) == true;

                if (!isAccepted)
                {
                    throw new UnauthorizedAccessException(
                        $"Service identity verification failed. " +
                        $"Server process '{serverProcess.ProcessName}' (PID {serverPid}) " +
                        $"is not running as SYSTEM or Administrator.");
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                "Failed to verify service identity.", ex);
        }
    }

    /// <summary>
    /// P/Invoke declarations for Named Pipe server identity verification (K-7).
    /// </summary>
    private static class NativeMethods
    {
        internal const uint TOKEN_QUERY = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool GetNamedPipeServerProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle Pipe,
            out uint ServerProcessId);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(
            IntPtr ProcessHandle,
            uint DesiredAccess,
            out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle TokenHandle);
    }

    /// <summary>
    /// Checks if the current process is running with admin privileges.
    /// </summary>
    private static bool IsRunAsAdmin()
    {
        try
        {
            var identity = WindowsIdentity.GetCurrent();
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return identity?.Groups?.Any(g => g.Value == adminSid.Value) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the configured Named Pipe name.
    /// </summary>
    public static string GetPipeName() => PipeName;
}
