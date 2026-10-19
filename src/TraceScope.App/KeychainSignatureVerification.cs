using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TraceScope.Core;

namespace TraceScope.App;

/// <summary>Explicit cross-build diagnostic, restricted to a disposable credential recorded in its own state file.</summary>
public static class KeychainSignatureVerification
{
    private const string Marker = "Pngr disposable signature probe v1";
    private sealed record State(string Purpose, string Id, string Digest, bool Cleaned = false);

    public static int RunNative(string phase, string path, TextWriter output)
    {
        if (!OperatingSystem.IsMacOS())
        {
            output.WriteLine("Signature probe requires macOS.");
            return 1;
        }
        // Test-only, per-process policy: return native authorization failures instead
        // of presenting a dialog. This neither grants access nor changes Keychain ACLs.
        var status = SecKeychainGetUserInteractionAllowed(out var original);
        if (status != 0) { output.WriteLine($"Interaction-policy query failed (OSStatus {status})."); return 1; }
        status = SecKeychainSetUserInteractionAllowed(false);
        if (status != 0) { output.WriteLine($"Interaction-policy setup failed (OSStatus {status})."); return 1; }
        try
        {
            output.WriteLine("Controlled Keychain signature probe: native authorization UI disabled in this process only.");
            var keychainStatus = SecKeychainCopyDefault(out var keychain);
            try
            {
                uint flags = 0;
                if (keychainStatus == 0) keychainStatus = SecKeychainGetStatus(keychain, out flags);
                output.WriteLine(keychainStatus == 0
                    ? $"Default Keychain: {((flags & 1) != 0 ? "unlocked" : "locked")}."
                    : $"Default Keychain status unavailable (OSStatus {keychainStatus}).");
            }
            finally { if (keychain != 0) CFRelease(keychain); }
            return Run(WebhookSecretStore.Create(), phase, path, output);
        }
        finally { SecKeychainSetUserInteractionAllowed(original); }
    }

    public static int Run(IWebhookSecretStore store, string phase, string path, TextWriter output)
    {
        try
        {
            if (phase == "create")
            {
                // Never accept a caller-supplied credential reference or reuse a state
                // file. Only a fresh random entry is created by this diagnostic.
                var id = Guid.NewGuid().ToString("N");
                var random = RandomNumberGenerator.GetBytes(32);
                var value = Convert.ToBase64String(random);
                CryptographicOperations.ZeroMemory(random);
                var state = new State(Marker, id, Digest(value));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    JsonSerializer.Serialize(file, state);
                    file.Flush(true);
                }
                if (store.Store(value, id) != id) throw new InvalidOperationException();
                output.WriteLine("Disposable signature-probe credential created.");
                return 0;
            }
            if (phase is not ("read" or "cleanup")) throw new InvalidDataException();
            var existing = JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? throw new InvalidDataException();
            if (existing.Purpose != Marker || existing.Cleaned || !Guid.TryParseExact(existing.Id, "N", out _) ||
                existing.Digest.Length != 64 || existing.Digest.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException();
            // Verify the recorded random value before any deletion. The diagnostic
            // never enumerates items or accepts an arbitrary account to delete.
            var actual = Digest(store.Retrieve(existing.Id));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(existing.Digest)))
                throw new InvalidDataException();
            if (phase == "cleanup")
            {
                store.Delete(existing.Id);
                File.WriteAllText(path, JsonSerializer.Serialize(existing with { Cleaned = true }));
                output.WriteLine("Disposable signature-probe credential removed.");
            }
            else output.WriteLine("Disposable signature-probe credential read verified.");
            return 0;
        }
        catch (WebhookCredentialException error)
        {
            output.WriteLine($"Signature probe failed: {error.Operation}, OSStatus {error.OSStatus}.");
            return 1;
        }
        catch
        {
            output.WriteLine("Signature probe failed; no credential values or exception details are displayed.");
            return 1;
        }
    }

    private static string Digest(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] out bool allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] bool allowed);
    [DllImport(Security)] private static extern int SecKeychainCopyDefault(out nint keychain);
    [DllImport(Security)] private static extern int SecKeychainGetStatus(nint keychain, out uint status);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);
}
