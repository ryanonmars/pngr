using TraceScope.Core;

namespace TraceScope.App;

/// <summary>Checks the packaged process's native credential identity without opening a workspace.</summary>
public static class WebhookCredentialVerification
{
    public static int Run(IWebhookSecretStore store, TextWriter output)
    {
        var id = Guid.NewGuid().ToString("N");
        const string original = "https://example.invalid/pngr-credential-verification/create";
        const string updated = "https://example.invalid/pngr-credential-verification/update";
        Exception? failure = null;
        try
        {
            if (store.Store(original, id) != id || store.Retrieve(id) != original)
                throw new InvalidOperationException();
            if (store.Store(updated, id) != id || store.Retrieve(id) != updated)
                throw new InvalidOperationException();
            store.Delete(id);
            store.Delete(id);
            var missing = false;
            try { store.Retrieve(id); }
            catch (WebhookCredentialException ex) when (ex.OSStatus == -25300) { missing = true; }
            // Windows Credential Manager uses the existing generic failure type.
            catch (InvalidOperationException ex) when (ex.GetType() == typeof(InvalidOperationException)) { missing = true; }
            if (!missing) throw new InvalidOperationException();
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            // Cleanup also runs if creation succeeded but a later operation failed.
            try { store.Delete(id); }
            catch (Exception ex) { failure ??= ex; }
        }
        if (failure is null)
        {
            output.WriteLine("Secure webhook credential verification passed (create, read, update, delete).");
            return 0;
        }
        if (failure is WebhookCredentialException native)
            output.WriteLine($"Secure webhook credential verification failed: macOS Keychain {native.Operation}, OSStatus {native.OSStatus}.");
        else
            output.WriteLine("Secure webhook credential verification failed.");
        return 1;
    }
}
