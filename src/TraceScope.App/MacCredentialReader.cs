using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TraceScope.Core;

namespace TraceScope.App;

/// <summary>One bounded credential read in an authenticated child, with authorization UI disabled only there.</summary>
public static class MacCredentialReader
{
    private const int MaxBytes = 2560;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Read(string id)
    {
        if (!OperatingSystem.IsMacOS() || !Guid.TryParseExact(id, "N", out _)) throw Failure();
        using var request = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var response = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw Failure()) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // Child startup must not load inherited managed hooks/profilers that could
        // inspect credential-bearing pipe data. This changes only the child env.
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.Equals("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DOTNET_ADDITIONAL_DEPS", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("DOTNET_SHARED_STORE", StringComparison.OrdinalIgnoreCase) ||
                (key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) ||
                 key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase)) &&
                (key.Contains("PROFIL", StringComparison.OrdinalIgnoreCase) || key.Contains("DIAGNOSTIC", StringComparison.OrdinalIgnoreCase)))
                start.Environment.Remove(key);
        start.ArgumentList.Add("--read-webhook-secret");
        start.ArgumentList.Add(request.GetClientHandleAsString());
        start.ArgumentList.Add(response.GetClientHandleAsString());
        using var child = Process.Start(start) ?? throw Failure();
        request.DisposeLocalCopyOfClientHandle(); response.DisposeLocalCopyOfClientHandle();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var bytes = new byte[MaxBytes];
        try
        {
            var requestBytes = Encoding.ASCII.GetBytes(id);
            try { request.WriteAsync(requestBytes, deadline.Token).AsTask().GetAwaiter().GetResult(); }
            finally { CryptographicOperations.ZeroMemory(requestBytes); }
            request.Dispose();
            return ReadFrameAsync(response, bytes, deadline.Token).GetAwaiter().GetResult();
        }
        catch (WebhookCredentialException) { throw; }
        catch { throw Failure(); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try
            {
                if (!child.HasExited) child.Kill();
                child.WaitForExit(1000);
            }
            catch { }
        }
    }

    public static int RunChild(string requestHandle, string responseHandle)
    {
        // Do not touch any credential until the running parent is authenticated.
        if (!OperatingSystem.IsMacOS() || !IsAuthenticatedParent()) return 1;
        if (SecKeychainGetUserInteractionAllowed(out var original) != 0) return 1;
        if (SecKeychainSetUserInteractionAllowed(false) != 0) return 1;
        var secretBytes = new byte[MaxBytes];
        try
        {
            using var request = new AnonymousPipeClientStream(PipeDirection.In, requestHandle);
            using var response = new AnonymousPipeClientStream(PipeDirection.Out, responseHandle);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var idBytes = new byte[32];
            try
            {
                request.ReadExactlyAsync(idBytes, deadline.Token).AsTask().GetAwaiter().GetResult();
                var id = Encoding.ASCII.GetString(idBytes);
                if (!Guid.TryParseExact(id, "N", out _)) return 1;
                try
                {
                    // Child is routed before callback registration, so this invokes
                    // the native store directly without recursively spawning children.
                    var value = WebhookSecretStore.Create().Retrieve(id);
                    var count = Utf8.GetByteCount(value);
                    if (count is < 1 or > MaxBytes) return WriteFailure(response, -50);
                    Utf8.GetBytes(value.AsSpan(), secretBytes);
                    var header = new byte[9]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), count);
                    response.Write(header); response.Write(secretBytes, 0, count); response.Flush();
                    return 0;
                }
                catch (WebhookCredentialException error) { return WriteFailure(response, error.OSStatus); }
                catch { return WriteFailure(response, -25293); }
            }
            finally { CryptographicOperations.ZeroMemory(idBytes); }
        }
        catch { return 1; }
        finally { CryptographicOperations.ZeroMemory(secretBytes); SecKeychainSetUserInteractionAllowed(original); }
    }

    public static async Task<string> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        if (buffer.Length != MaxBytes) throw Failure();
        var header = new byte[9];
        // Read() synchronously waits on this task from the settings UI thread.
        // Pipe completions must never require that blocked synchronization context.
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = header[0];
        var osStatus = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5));
        if (status == 1 && length == 0) throw new WebhookCredentialException(WebhookCredentialOperation.Read, osStatus);
        if (status != 0 || osStatus != 0 || length is < 1 or > MaxBytes) throw Failure();
        await stream.ReadExactlyAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
        return Utf8.GetString(buffer, 0, length);
    }

    private static int WriteFailure(Stream stream, int status)
    {
        var frame = new byte[9]; frame[0] = 1; BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), status);
        stream.Write(frame); stream.Flush(); return 1;
    }
    private static WebhookCredentialException Failure() => new(WebhookCredentialOperation.Read, -25293);

    private static bool IsAuthenticatedParent()
    {
        nint self = 0, requirement = 0, attributes = 0, pidNumber = 0, parent = 0;
        try
        {
            var pid = getppid(); if (pid <= 1) return false;
            var pathBytes = new byte[4096];
            var count = proc_pidpath(pid, pathBytes, (uint)pathBytes.Length);
            if (count <= 0) return false;
            var end = Array.IndexOf(pathBytes, (byte)0); if (end < 0) return false;
            if (!string.Equals(Utf8.GetString(pathBytes, 0, end), Environment.ProcessPath, StringComparison.Ordinal)) return false;
            if (SecCodeCopySelf(0, out self) != 0 || SecCodeCheckValidity(self, 0, 0) != 0 ||
                SecCodeCopyDesignatedRequirement(self, 0, out requirement) != 0) return false;
            var security = NativeLibrary.Load(Security);
            var coreFoundation = NativeLibrary.Load(CoreFoundation);
            try
            {
                attributes = CFDictionaryCreateMutable(0, 0,
                    NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryKeyCallBacks"),
                    NativeLibrary.GetExport(coreFoundation, "kCFTypeDictionaryValueCallBacks"));
                pidNumber = CFNumberCreate(0, 3, ref pid);
                if (attributes == 0 || pidNumber == 0) return false;
                CFDictionarySetValue(attributes, Marshal.ReadIntPtr(NativeLibrary.GetExport(security, "kSecGuestAttributePid")), pidNumber);
                return SecCodeCopyGuestWithAttributes(0, attributes, 0, out parent) == 0 &&
                    SecCodeCheckValidity(parent, 0, requirement) == 0;
            }
            finally { NativeLibrary.Free(security); NativeLibrary.Free(coreFoundation); }
        }
        catch { return false; }
        finally { foreach (var value in new[] { parent, pidNumber, attributes, requirement, self }) if (value != 0) CFRelease(value); }
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int getppid();
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_pidpath(int pid, byte[] buffer, uint size);
    [DllImport(Security)] private static extern int SecCodeCopySelf(uint flags, out nint self);
    [DllImport(Security)] private static extern int SecCodeCopyDesignatedRequirement(nint code, uint flags, out nint requirement);
    [DllImport(Security)] private static extern int SecCodeCopyGuestWithAttributes(nint host, nint attributes, uint flags, out nint code);
    [DllImport(Security)] private static extern int SecCodeCheckValidity(nint code, uint flags, nint requirement);
    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] out bool allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] bool allowed);
    [DllImport(CoreFoundation)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keys, nint values);
    [DllImport(CoreFoundation)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(CoreFoundation)] private static extern nint CFNumberCreate(nint allocator, int type, ref int value);
    [DllImport(CoreFoundation)] private static extern void CFRelease(nint value);
}
