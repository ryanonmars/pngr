using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class WebhookSecretStoreTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("https://discord.com/api/webhooks/123/private-token")]
    [InlineData("com.other.app/credential")]
    public void InvalidIdentifiersNeverReachNativeStorageOrExposeInput(string id)
    {
        var store = WebhookSecretStore.Create();
        var read = Assert.Throws<ArgumentException>(() => store.Retrieve(id));
        var delete = Assert.Throws<ArgumentException>(() => store.Delete(id));
        var write = Assert.Throws<ArgumentException>(() => store.Store("https://example.test/private-token", id));
        Assert.Equal("Invalid webhook credential identifier.", read.Message);
        Assert.Equal(read.Message, delete.Message);
        Assert.Equal(read.Message, write.Message);
        Assert.Null(read.InnerException);
    }

    [Fact]
    public void EmptyAndOversizedCredentialsAreRejectedWithoutExposingSecrets()
    {
        var store = WebhookSecretStore.Create();
        Assert.Throws<ArgumentException>(() => store.Store(""));
        Assert.Throws<ArgumentException>(() => store.Store(" "));
        var error = Assert.Throws<ArgumentException>(() => store.Store("private-token-" + new string('x', 2560)));
        Assert.Equal("A valid webhook credential is required.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void UnsupportedPlatformsHaveSanitizedErrors()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) return;
        var store = WebhookSecretStore.Create();
        var error = Assert.Throws<PlatformNotSupportedException>(() => store.Store("https://example.test/private-token"));
        Assert.DoesNotContain("private-token", error.Message);
        Assert.Throws<PlatformNotSupportedException>(() => store.Retrieve(Guid.NewGuid().ToString("N")));
        Assert.Throws<PlatformNotSupportedException>(() => store.Delete(Guid.NewGuid().ToString("N")));
    }
    [NativeSecretStoreFact]
    public void NativeStorageCanCreateReadUpdateAndDeleteAnEphemeralCredential()
    {
        var store = WebhookSecretStore.Create();
        string? id = null;
        try
        {
            id = store.Store("https://example.invalid/pngr-native-storage-test");
            Assert.True(Guid.TryParseExact(id, "N", out _));
            Assert.Equal("https://example.invalid/pngr-native-storage-test", store.Retrieve(id));
            Assert.Equal(id, store.Store("https://example.invalid/pngr-native-storage-test-updated", id));
            Assert.Equal("https://example.invalid/pngr-native-storage-test-updated", store.Retrieve(id));
            store.Delete(id);
            store.Delete(id); // Idempotent cleanup.
            var error = Assert.ThrowsAny<InvalidOperationException>(() => store.Retrieve(id));
            if (OperatingSystem.IsMacOS())
            {
                var native = Assert.IsType<WebhookCredentialException>(error);
                Assert.Equal(-25300, native.OSStatus);
                Assert.Equal(WebhookCredentialOperation.Read, native.Operation);
            }
            else Assert.Equal("Unable to access the secure webhook credential store.", error.Message);
            Assert.Null(error.InnerException);
        }
        finally { if (id is not null) store.Delete(id); }
    }

    [Theory]
    [InlineData(WebhookCredentialOperation.Create, -67030, "create")]
    [InlineData(WebhookCredentialOperation.Read, -25300, "read")]
    [InlineData(WebhookCredentialOperation.Update, -25308, "update")]
    [InlineData(WebhookCredentialOperation.Delete, -25293, "delete")]
    public void KeychainDiagnosticsExposeOnlyOperationAndStatus(WebhookCredentialOperation operation, int status, string name)
    {
        var error = new WebhookCredentialException(operation, status);
        Assert.Equal(status, error.OSStatus);
        Assert.Equal(operation, error.Operation);
        Assert.Contains(name, error.Message);
        Assert.Contains($"OSStatus {status}", error.Message);
        Assert.DoesNotContain("https", error.ToString());
        Assert.Null(error.InnerException);
    }

    private sealed class NativeSecretStoreFactAttribute : FactAttribute
    {
        public NativeSecretStoreFactAttribute()
        {
            if ((!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows()) ||
                Environment.GetEnvironmentVariable("PNGR_NATIVE_SECRET_TESTS") != "1")
                Skip = "Set PNGR_NATIVE_SECRET_TESTS=1 to exercise native credential storage (macOS may request Keychain access).";
        }
    }
}
