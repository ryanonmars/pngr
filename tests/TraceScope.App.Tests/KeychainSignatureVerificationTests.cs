using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class KeychainSignatureVerificationTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pngr-signature-probe-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "probe.json");
        public Fixture() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, true);
    }
    private sealed class ProbeStore : IWebhookSecretStore
    {
        public readonly Dictionary<string, string> Values = [];
        public readonly List<string> Calls = [];
        public bool DenyReads;
        public string Store(string value, string? existingId = null)
        {
            Calls.Add("create"); Assert.NotNull(existingId); Assert.False(Values.ContainsKey(existingId));
            Values.Add(existingId, value); return existingId;
        }
        public string Retrieve(string id)
        {
            Calls.Add("read");
            if (DenyReads) throw new WebhookCredentialException(WebhookCredentialOperation.Read, -25308);
            return Values[id];
        }
        public void Delete(string id) { Calls.Add("delete"); Values.Remove(id); }
    }

    [Fact]
    public void CreatesOnlyOneDisposableItemSupportsRepeatedReadAndVerifiedCleanup()
    {
        using var fixture = new Fixture(); var store = new ProbeStore(); using var output = new StringWriter();
        store.Values.Add("existing-discord", "existing credential must remain untouched");
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "create", fixture.Path, output));
        var test = store.Values.Single(pair => pair.Key != "existing-discord");
        Assert.DoesNotContain(test.Value, File.ReadAllText(fixture.Path));
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "read", fixture.Path, output));
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "read", fixture.Path, output));
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "read", fixture.Path, output));
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "cleanup", fixture.Path, output));
        Assert.Single(store.Values);
        Assert.True(store.Values.ContainsKey("existing-discord"));
        Assert.Equal(new[] { "create", "read", "read", "read", "read", "delete" }, store.Calls);
        Assert.DoesNotContain(test.Key, output.ToString());
        Assert.DoesNotContain(test.Value, output.ToString());
        Assert.Equal(1, KeychainSignatureVerification.Run(store, "read", fixture.Path, output));
        Assert.Equal(6, store.Calls.Count);
    }

    [Fact]
    public void ExistingStateCannotBeOverwrittenOrCreateAnotherCredential()
    {
        using var fixture = new Fixture(); var store = new ProbeStore(); using var output = new StringWriter();
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "create", fixture.Path, output));
        Assert.Equal(1, KeychainSignatureVerification.Run(store, "create", fixture.Path, output));
        Assert.Single(store.Calls);
        Assert.Single(store.Values);
    }

    [Fact]
    public void AuthorizationFailurePreservesDisposableItemAndPrintsOnlyOperationAndStatus()
    {
        using var fixture = new Fixture(); var store = new ProbeStore(); using var output = new StringWriter();
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "create", fixture.Path, output));
        var state = File.ReadAllText(fixture.Path); var test = Assert.Single(store.Values);
        store.DenyReads = true;
        Assert.Equal(1, KeychainSignatureVerification.Run(store, "read", fixture.Path, output));
        Assert.Equal(state, File.ReadAllText(fixture.Path));
        Assert.True(store.Values.ContainsKey(test.Key));
        Assert.DoesNotContain("delete", store.Calls);
        Assert.Contains("Read, OSStatus -25308", output.ToString());
        Assert.DoesNotContain(test.Value, output.ToString());
    }

    [Fact]
    public void CleanupRequiresMatchingOwnedValueAndRejectsMalformedStateBeforeStoreAccess()
    {
        using var fixture = new Fixture(); var store = new ProbeStore(); using var output = new StringWriter();
        Assert.Equal(0, KeychainSignatureVerification.Run(store, "create", fixture.Path, output));
        var test = Assert.Single(store.Values);
        store.Values[test.Key] = "different value";
        Assert.Equal(1, KeychainSignatureVerification.Run(store, "cleanup", fixture.Path, output));
        Assert.DoesNotContain("delete", store.Calls);
        var count = store.Calls.Count;
        File.WriteAllText(fixture.Path, JsonSerializer.Serialize(new { Purpose = "not an owned probe", Id = test.Key, Digest = new string('A', 64) }));
        Assert.Equal(1, KeychainSignatureVerification.Run(store, "cleanup", fixture.Path, output));
        Assert.Equal(count, store.Calls.Count);
    }
}
