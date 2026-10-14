using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class WebhookCredentialVerificationTests
{
    [Fact]
    public void VerifiesCrudAndCleansUpWithoutPrintingCredentialsOrIdentifiers()
    {
        var store = new VerificationStore();
        using var output = new StringWriter();
        Assert.Equal(0, WebhookCredentialVerification.Run(store, output));
        Assert.Equal(["create", "read", "update", "read", "delete", "delete", "read", "delete"], store.Calls);
        Assert.Null(store.Value);
        Assert.Contains("passed", output.ToString());
        Assert.DoesNotContain(store.Id!, output.ToString());
        Assert.DoesNotContain("https://", output.ToString());
    }

    [Fact]
    public void FailureCleansUpAndNeverPrintsArbitraryExceptionDetails()
    {
        var store = new VerificationStore { FailRead = true };
        using var output = new StringWriter();
        Assert.Equal(1, WebhookCredentialVerification.Run(store, output));
        Assert.Equal(["create", "read", "delete"], store.Calls);
        Assert.Null(store.Value);
        Assert.Equal("Secure webhook credential verification failed." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void DetectsUnexpectedCredentialRemainingAfterDelete()
    {
        var store = new VerificationStore { IgnoreDelete = true };
        using var output = new StringWriter();
        Assert.Equal(1, WebhookCredentialVerification.Run(store, output));
        Assert.Contains("failed", output.ToString());
        Assert.Equal(3, store.Calls.Count(call => call == "delete"));
    }

    [Fact]
    public void CleanupFailureMakesVerificationFail()
    {
        var store = new VerificationStore { FailCleanup = true };
        using var output = new StringWriter();
        Assert.Equal(1, WebhookCredentialVerification.Run(store, output));
        Assert.Equal("Secure webhook credential verification failed." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void NativeMissingCredentialIsExpectedAfterDeletion()
    {
        var store = new VerificationStore { NativeMissing = true };
        using var output = new StringWriter();
        Assert.Equal(0, WebhookCredentialVerification.Run(store, output));
    }

    [Fact]
    public void NativeFailurePrintsOnlyOperationAndStatus()
    {
        var store = new VerificationStore { NativeFailure = true };
        using var output = new StringWriter();
        Assert.Equal(1, WebhookCredentialVerification.Run(store, output));
        Assert.Equal("Secure webhook credential verification failed: macOS Keychain Read, OSStatus -67030." + Environment.NewLine, output.ToString());
        Assert.Null(store.Value);
    }

    private sealed class VerificationStore : IWebhookSecretStore
    {
        public List<string> Calls { get; } = [];
        public string? Id { get; private set; }
        public string? Value { get; private set; }
        public bool FailRead { get; init; }
        public bool IgnoreDelete { get; init; }
        public bool FailCleanup { get; init; }
        public bool NativeMissing { get; init; }
        public bool NativeFailure { get; init; }

        public string Store(string url, string? existingId = null)
        {
            Calls.Add(Value is null ? "create" : "update");
            Assert.NotNull(existingId);
            if (Id is not null) Assert.Equal(Id, existingId);
            Id = existingId;
            Value = url;
            return Id;
        }

        public string Retrieve(string id)
        {
            Calls.Add("read");
            Assert.Equal(Id, id);
            if (FailRead) throw new Exception("https://discord.com/api/webhooks/private/token credential details");
            if (NativeFailure) throw new WebhookCredentialException(WebhookCredentialOperation.Read, -67030);
            if (Value is null && NativeMissing) throw new WebhookCredentialException(WebhookCredentialOperation.Read, -25300);
            return Value ?? throw new InvalidOperationException();
        }

        public void Delete(string id)
        {
            Calls.Add("delete");
            Assert.Equal(Id, id);
            if (FailCleanup && Calls.Count(call => call == "delete") == 3)
                throw new Exception("private-token");
            if (!IgnoreDelete) Value = null;
        }
    }
}
