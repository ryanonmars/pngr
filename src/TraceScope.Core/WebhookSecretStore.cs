using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TraceScope.Core;

/// <summary>Stores webhook credentials outside workspace configuration.</summary>
public interface IWebhookSecretStore
{
    string Store(string url, string? existingId = null);
    string Retrieve(string id);
    void Delete(string id);
}

/// <summary>Only fixed operation names and numeric OSStatus are exposed; never credential data.</summary>
public enum WebhookCredentialOperation { Find, Create, Read, Update, Delete }

public sealed class WebhookCredentialException : InvalidOperationException
{
    public WebhookCredentialOperation Operation { get; }
    public int OSStatus { get; }
    public WebhookCredentialException(WebhookCredentialOperation operation, int osStatus)
        : base($"macOS Keychain {SafeOperation(operation)} failed (OSStatus {osStatus}). " +
            (osStatus == -67030 ? "Application signing metadata is invalid. Install the latest Pngr release." :
             osStatus == -25300 ? "The credential was not found. Save the webhook URL again." :
             "Check Login Keychain access and retry."))
    {
        Operation = operation;
        OSStatus = osStatus;
    }

    private static string SafeOperation(WebhookCredentialOperation operation) => operation switch
    {
        WebhookCredentialOperation.Find => "lookup",
        WebhookCredentialOperation.Create => "create",
        WebhookCredentialOperation.Read => "read",
        WebhookCredentialOperation.Update => "update",
        WebhookCredentialOperation.Delete => "delete",
        _ => "operation"
    };
}

/// <summary>Uses the current user's macOS Keychain or Windows Credential Manager.</summary>
public sealed class WebhookSecretStore : IWebhookSecretStore
{
    private const string Service = "com.pngr.webhooks";
    private const string FailureMessage = "Unable to access the secure webhook credential store.";
    private const int NotFoundMac = -25300;
    private const int NotFoundWindows = 1168;
    public static IWebhookSecretStore Default { get; } = new WebhookSecretStore();
    public static IWebhookSecretStore Create() => new WebhookSecretStore();
    private static Func<string, string>? _macCredentialReader;

    /// <summary>
    /// Installs the desktop application's isolated, noninteractive macOS reader at
    /// startup. The helper process deliberately does not register this callback.
    /// No credentials are retained by the store or this registration.
    /// </summary>
    public static void RegisterMacCredentialReader(Func<string, string> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (Interlocked.CompareExchange(ref _macCredentialReader, reader, null) is not null)
            throw new InvalidOperationException("The macOS credential reader is already registered.");
    }

    public string Store(string url, string? existingId = null)
    {
        if (string.IsNullOrWhiteSpace(url) || Encoding.UTF8.GetByteCount(url) > 2560)
            throw new ArgumentException("A valid webhook credential is required.");
        var id = existingId ?? Guid.NewGuid().ToString("N");
        ValidateId(id);
        EnsureSupported();
        var bytes = Encoding.UTF8.GetBytes(url);
        try
        {
            if (OperatingSystem.IsMacOS()) StoreMac(id, bytes);
            else StoreWindows(id, bytes);
            return id;
        }
        catch (WebhookCredentialException) { throw; }
        catch { throw new InvalidOperationException(FailureMessage); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public string Retrieve(string id)
    {
        ValidateId(id);
        EnsureSupported();
        byte[]? bytes = null;
        try
        {
            if (OperatingSystem.IsMacOS() && Volatile.Read(ref _macCredentialReader) is { } reader)
            {
                var value = reader(id);
                if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > 2560)
                    throw new InvalidOperationException();
                return value;
            }
            bytes = OperatingSystem.IsMacOS() ? RetrieveMac(id) : RetrieveWindows(id);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (WebhookCredentialException) { throw; }
        catch { throw new InvalidOperationException(FailureMessage); }
        finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Delete(string id)
    {
        ValidateId(id);
        EnsureSupported();
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var status = FindMacItem(id, out var item);
                if (status == NotFoundMac) return;
                try
                {
                    CheckMac(status, WebhookCredentialOperation.Find);
                    CheckMac(SecKeychainItemDelete(item), WebhookCredentialOperation.Delete);
                }
                finally { if (item != 0) CFRelease(item); }
            }
            else if (!CredDeleteW(Target(id), 1, 0) && Marshal.GetLastWin32Error() != NotFoundWindows)
                throw new InvalidOperationException();
        }
        catch (WebhookCredentialException) { throw; }
        catch { throw new InvalidOperationException(FailureMessage); }
    }

    private static void ValidateId(string id)
    {
        if (id is null || !Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid webhook credential identifier.");
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Secure webhook credential storage requires macOS or Windows.");
    }

    private static string Target(string id) => $"{Service}/{id}";
    private static int FindMacItem(string id, out nint item)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(id);
        // Updates/deletes need only the item handle. Passing NULL for both data
        // outputs avoids decrypting its old secret just to locate that handle.
        return SecKeychainFindGenericPasswordItem(0, (uint)service.Length, service, (uint)account.Length, account, 0, 0, out item);
    }

    private static void StoreMac(string id, byte[] bytes)
    {
        var status = FindMacItem(id, out var item);
        try
        {
            if (status == 0)
            {
                CheckMac(SecKeychainItemModifyAttributesAndData(item, 0, (uint)bytes.Length, bytes), WebhookCredentialOperation.Update);
            }
            else if (status == NotFoundMac)
            {
                var service = Encoding.UTF8.GetBytes(Service);
                var account = Encoding.UTF8.GetBytes(id);
                var createStatus = SecKeychainAddGenericPassword(0, (uint)service.Length, service, (uint)account.Length, account, (uint)bytes.Length, bytes, out var created);
                try { CheckMac(createStatus, WebhookCredentialOperation.Create); }
                finally { if (created != 0) CFRelease(created); }
            }
            else CheckMac(status, WebhookCredentialOperation.Find);
        }
        finally { if (item != 0) CFRelease(item); }
    }

    private static byte[] RetrieveMac(string id)
    {
        var service = Encoding.UTF8.GetBytes(Service);
        var account = Encoding.UTF8.GetBytes(id);
        // Reading requires the data, but no separately retained item reference.
        var status = SecKeychainFindGenericPasswordData(0, (uint)service.Length, service, (uint)account.Length, account, out var length, out var data, 0);
        try
        {
            CheckMac(status, WebhookCredentialOperation.Read);
            if (length > 2560 || data == 0) throw new InvalidOperationException();
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            ClearMacData(data, length);
        }
    }

    private static void CheckMac(int status, WebhookCredentialOperation operation)
    {
        if (status != 0) throw new WebhookCredentialException(operation, status);
    }

    private static void ClearMacData(nint data, uint length)
    {
        if (data == 0) return;
        // Clear native credential memory before releasing it back to Security.framework.
        for (uint i = 0; i < length; i++) Marshal.WriteByte(data, (int)i, 0);
        SecKeychainItemFreeContent(0, data);
    }

    private static void StoreWindows(string id, byte[] bytes)
    {
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = Target(id), CredentialBlobSize = (uint)bytes.Length, CredentialBlob = blob, Persist = 2, UserName = "Pngr" };
            if (!CredWriteW(ref credential, 0)) throw new InvalidOperationException();
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static byte[] RetrieveWindows(string id)
    {
        if (!CredReadW(Target(id), 1, 0, out var pointer)) throw new InvalidOperationException();
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 2560 || credential.CredentialBlob == 0) throw new InvalidOperationException();
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(credential.CredentialBlob, i, 0);
            return bytes;
        }
        finally { CredFree(pointer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordItem(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, nint length, nint data, out nint item);
    [DllImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPasswordData(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint length, out nint data, nint item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint length, byte[] data, out nint item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(nint item, nint attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(nint attributes, nint data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(nint item);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint credential);
}
