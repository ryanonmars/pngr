using System.Net;
using System.Collections.Concurrent;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

/// <summary>Portable access-count checks; these do not exercise native authorization dialogs.</summary>
public sealed class CredentialAccessLifecycleTests
{
    private const string UrlId = "11111111111111111111111111111111";
    private const string TokenId = "22222222222222222222222222222222";
    private const string Token = "123456:portable-test-secret";
    private const string Url = "https://discord.com/api/webhooks/123456789/portable-test-secret";

    private sealed class Secrets : IWebhookSecretStore
    {
        public Dictionary<string, string> Values { get; } = new() { [UrlId] = Url, [TokenId] = Token };
        public List<string> Reads { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Deletes { get; } = [];
        public Action<string>? OnRead { get; set; }
        public bool FailRead { get; init; }
        public bool FailWrite { get; set; }
        public bool WrongReadBack { get; set; }
        public bool FailAfterWrite { get; set; }
        public string Retrieve(string id)
        {
            Reads.Add(id);
            OnRead?.Invoke(id);
            if (FailRead || !Values.TryGetValue(id, out var value)) throw new IOException("Secret-store failure: " + Token);
            return WrongReadBack ? "incorrect readback" : value;
        }
        public string Store(string value, string? existingId = null)
        {
            if (FailWrite) throw new IOException("Secret-store failure: " + value);
            var id = existingId ?? Guid.NewGuid().ToString("N");
            Writes.Add(id);
            Values[id] = value;
            if (FailAfterWrite) throw new IOException("Secret-store partial failure: " + value);
            return id;
        }
        public void Delete(string id) { Deletes.Add(id); Values.Remove(id); }
        public void AssertIdle() { Assert.Empty(Reads); Assert.Empty(Writes); Assert.Empty(Deletes); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "pngr-credential-lifecycle-" + Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(_directory, "workspace.json");
        public WorkspaceStore Store(Secrets secrets) => new(FilePath, secrets);
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private static WorkspaceConfiguration Configuration(WebhookFormat format) => new()
    {
        Actions = new() { MinimumActionSpacingSeconds = 0, Webhook = new()
        {
            Enabled = true, Format = format, UrlSecretId = UrlId,
            TelegramBotTokenSecretId = TokenId, TelegramChatId = "4294967296", TelegramBotUsername = "PngrTestBot"
        } }
    };

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task UnchangedApplyAndRestartPreserveReferencesWithoutAccessingSecrets(WebhookFormat format)
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var expected = Configuration(format);
        var saved = await workspace.Store(secrets).SaveAsync(expected);
        saved = await workspace.Store(secrets).SaveAsync(saved);
        var restored = await workspace.Store(secrets).LoadAsync();
        Assert.Equal(expected.Actions.Webhook, saved.Actions.Webhook);
        Assert.Equal(saved.Actions.Webhook, restored!.Actions.Webhook);
        secrets.AssertIdle();
    }

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task CreateEditAndSaveOrdinaryAlertDoesNotAccessWebhookCredentials(WebhookFormat format)
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var original = Configuration(format);
        var rule = new AlertRule { Name = "Ordinary latency rule", Condition = AlertCondition.Latency, Threshold = 200 };
        var created = await workspace.Store(secrets).SaveAsync(original with { Alerts = [rule] });
        var edited = await workspace.Store(secrets).SaveAsync(created with { Alerts = [rule with { Threshold = 250 }] });
        Assert.Equal(250, Assert.Single(edited.Alerts).Threshold);
        Assert.Equal(original.Actions.Webhook, edited.Actions.Webhook);
        secrets.AssertIdle();
    }

    [Fact]
    public async Task SwitchingFormatsAndDisconnectingPreservesBothCredentialReferencesWithoutAccess()
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var configuration = Configuration(WebhookFormat.Telegram);
        foreach (var format in new[] { WebhookFormat.Discord, WebhookFormat.GenericJson, WebhookFormat.Telegram })
            configuration = await workspace.Store(secrets).SaveAsync(configuration with
            { Actions = configuration.Actions with { Webhook = configuration.Actions.Webhook with { Format = format } } });
        configuration = await workspace.Store(secrets).SaveAsync(configuration with
        { Actions = configuration.Actions with { Webhook = configuration.Actions.Webhook with { Enabled = false, TelegramChatId = "" } } });
        Assert.Equal(UrlId, configuration.Actions.Webhook.UrlSecretId);
        Assert.Equal(TokenId, configuration.Actions.Webhook.TelegramBotTokenSecretId);
        secrets.AssertIdle();
    }

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task MissingOrUnavailableCredentialDoesNotPreventUnrelatedSave(WebhookFormat format)
    {
        using var workspace = new Workspace();
        var secrets = new Secrets { FailRead = true };
        secrets.Values.Clear();
        var configuration = Configuration(format) with { Alerts = [new AlertRule()] };
        await workspace.Store(secrets).SaveAsync(configuration);
        var restored = await workspace.Store(secrets).LoadAsync();
        Assert.Single(restored!.Alerts);
        Assert.Equal(configuration.Actions.Webhook, restored.Actions.Webhook);
        secrets.AssertIdle();
    }

    [Fact]
    public async Task TokenReplacementVerifiesOnlyNewEntryAndRetainsOldCredentials()
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var configuration = await workspace.Store(secrets).SaveAsync(Configuration(WebhookFormat.Telegram));
        var before = await File.ReadAllTextAsync(workspace.FilePath);
        secrets.OnRead = id =>
        {
            Assert.NotEqual(TokenId, id);
            Assert.Equal(before, File.ReadAllText(workspace.FilePath));
            Assert.Equal(Token, secrets.Values[TokenId]);
        };
        var replacement = await workspace.Store(secrets).SaveAsync(configuration with
        { Actions = configuration.Actions with { Webhook = configuration.Actions.Webhook with { TelegramBotToken = "987654:replacement-secret" } } });
        var newId = Assert.Single(secrets.Writes);
        Assert.Equal(newId, replacement.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Empty(secrets.Deletes);
        Assert.Equal(newId, Assert.Single(secrets.Reads));
        Assert.Equal(Token, secrets.Values[TokenId]);
        Assert.Equal(configuration.Actions.Webhook.TelegramChatId, replacement.Actions.Webhook.TelegramChatId);
        Assert.Equal(configuration.Actions.Webhook.TelegramBotUsername, replacement.Actions.Webhook.TelegramBotUsername);
        Assert.Equal(Url, secrets.Values[UrlId]);
        Assert.Empty(replacement.Actions.Webhook.TelegramBotToken);
        Assert.DoesNotContain("replacement-secret", await File.ReadAllTextAsync(workspace.FilePath));
        await workspace.Store(secrets).SaveAsync(replacement);
        var reopened = (await workspace.Store(secrets).LoadAsync())!;
        Assert.Equal(replacement.Actions.Webhook, reopened.Actions.Webhook);
        Assert.Single(secrets.Writes);
        Assert.Empty(secrets.Deletes);
        Assert.Single(secrets.Reads);
    }

    private sealed class DesktopContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;
        public DesktopContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var action in _queue.GetConsumingEnumerable()) action();
            }) { IsBackground = true };
            _thread.Start();
        }
        public override void Post(SendOrPostCallback callback, object? state) => _queue.Add(() => callback(state));
        public void Dispose()
        {
            _queue.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "Desktop test thread did not exit.");
            _queue.Dispose();
        }
    }

    [Fact]
    public async Task ReplacementApplyReturnsToDesktopWhileNewCredentialReadbackIsPending()
    {
        using var workspace = new Workspace();
        using var desktop = new DesktopContext();
        using var release = new ManualResetEventSlim();
        var enteredRead = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<Task<WorkspaceConfiguration>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = new Secrets();
        var store = workspace.Store(secrets);
        var saved = await store.SaveAsync(Configuration(WebhookFormat.Telegram));
        secrets.OnRead = id =>
        {
            if (id == TokenId) throw new WebhookCredentialException(WebhookCredentialOperation.Read, -25293);
            enteredRead.TrySetResult(Environment.CurrentManagedThreadId);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var replacement = saved with { Actions = saved.Actions with { Webhook = saved.Actions.Webhook with
            { TelegramBotToken = "987654:replacement-secret" } } };
        desktop.Post(_ =>
        {
            started.TrySetResult(store.SaveAsync(replacement));
            desktop.Post(_ => heartbeat.TrySetResult(), null);
        }, null);
        Task<WorkspaceConfiguration>? pending = null;
        Task<WorkspaceConfiguration?>? waitingReader = null;
        try
        {
            var workerThread = await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(desktop.ThreadId, workerThread);
            Assert.False(pending.IsCompleted);
            waitingReader = store.LoadAsync();
            Assert.False(waitingReader.IsCompleted);
            await using var stream = File.OpenRead(workspace.FilePath);
            Assert.Equal(TokenId, (await WorkspaceStore.ImportAsync(stream)).Actions.Webhook.TelegramBotTokenSecretId);
        }
        finally { release.Set(); }
        var secured = await pending!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(secured.Actions.Webhook, (await waitingReader!.WaitAsync(TimeSpan.FromSeconds(5)))!.Actions.Webhook);
        var reopened = (await workspace.Store(secrets).LoadAsync())!;
        Assert.Equal(secured.Actions.Webhook, reopened.Actions.Webhook);
        Assert.Equal("987654:replacement-secret", secrets.Values[secured.Actions.Webhook.TelegramBotTokenSecretId]);
        Assert.DoesNotContain(TokenId, secrets.Reads);
        Assert.Empty(secrets.Deletes);
    }

    [Fact]
    public async Task CancelDuringNewCredentialReadbackWaitsForWorkerThenRollsBackOnlyNewEntry()
    {
        using var workspace = new Workspace();
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secrets = new Secrets();
        var store = workspace.Store(secrets);
        var saved = await store.SaveAsync(Configuration(WebhookFormat.Telegram));
        var before = await File.ReadAllTextAsync(workspace.FilePath);
        secrets.OnRead = _ =>
        {
            enteredRead.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var pending = store.SaveAsync(saved with { Actions = saved.Actions with { Webhook = saved.Actions.Webhook with
            { TelegramBotToken = "987654:replacement-secret" } } }, cancel.Token);
        try
        {
            await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.False(pending.IsCompleted);
            Assert.Empty(secrets.Deletes);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.FilePath));
        Assert.Equal(Assert.Single(secrets.Writes), Assert.Single(secrets.Deletes));
        Assert.DoesNotContain(TokenId, secrets.Reads);
        Assert.Equal(Token, secrets.Values[TokenId]);
        Assert.Equal(Url, secrets.Values[UrlId]);
    }

    [Fact]
    public async Task FailedReplacementPreservesSavedConfigurationAndCredentialsWithoutLeakingSecret()
    {
        using var workspace = new Workspace();
        var secrets = new Secrets { FailWrite = true };
        var configuration = await workspace.Store(secrets).SaveAsync(Configuration(WebhookFormat.Telegram));
        var before = await File.ReadAllTextAsync(workspace.FilePath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Store(secrets).SaveAsync(configuration with
        { Actions = configuration.Actions with { Webhook = configuration.Actions.Webhook with { TelegramBotToken = "987654:replacement-secret" } } }));
        Assert.DoesNotContain("replacement-secret", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.FilePath));
        Assert.Equal(Token, secrets.Values[TokenId]);
        Assert.Empty(secrets.Reads);
        Assert.Empty(secrets.Writes);
        Assert.NotEqual(TokenId, Assert.Single(secrets.Deletes));
        Assert.NotEqual(UrlId, Assert.Single(secrets.Deletes));
    }

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task NewCredentialIsCreatedOnceAndExportDoesNotReadOrDiscloseIt(WebhookFormat format)
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var configuration = Configuration(format);
        var draft = configuration.Actions.Webhook with { UrlSecretId = "", TelegramBotTokenSecretId = "" };
        draft = format == WebhookFormat.Telegram ? draft with { TelegramBotToken = Token } : draft with { Url = Url };
        var saved = await workspace.Store(secrets).SaveAsync(configuration with { Actions = configuration.Actions with { Webhook = draft } });
        var id = Assert.Single(secrets.Writes);
        Assert.Equal(id, format == WebhookFormat.Telegram ? saved.Actions.Webhook.TelegramBotTokenSecretId : saved.Actions.Webhook.UrlSecretId);
        await using var exported = new MemoryStream();
        await WorkspaceStore.ExportAsync(exported, saved);
        var json = System.Text.Encoding.UTF8.GetString(exported.ToArray());
        Assert.DoesNotContain("portable-test-secret", json);
        Assert.DoesNotContain(id, json);
        Assert.DoesNotContain("portable-test-secret", await File.ReadAllTextAsync(workspace.FilePath));
        if (format == WebhookFormat.Telegram) Assert.Equal(id, Assert.Single(secrets.Reads));
        else Assert.Empty(secrets.Reads);
        Assert.Empty(secrets.Deletes);
        Assert.Single(secrets.Writes);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("mismatch")]
    [InlineData("partial-write")]
    [InlineData("workspace-write")]
    public async Task ReplacementFailureRollsBackOnlyOwnedNewEntryAndKeepsOriginalSettings(string failure)
    {
        using var workspace = new Workspace();
        var secrets = new Secrets();
        var saved = await workspace.Store(secrets).SaveAsync(Configuration(WebhookFormat.Telegram));
        var before = await File.ReadAllTextAsync(workspace.FilePath);
        var replacing = saved with { Actions = saved.Actions with { Webhook = saved.Actions.Webhook with
            { TelegramBotToken = "987654:replacement-secret" } } };
        if (failure == "workspace-write") Directory.CreateDirectory(workspace.FilePath + ".tmp");
        secrets = new Secrets { FailRead = failure == "read", WrongReadBack = failure == "mismatch",
            FailAfterWrite = failure == "partial-write" };
        var error = await Assert.ThrowsAnyAsync<Exception>(() => workspace.Store(secrets).SaveAsync(replacing));
        Assert.DoesNotContain("replacement-secret", error.ToString());
        Assert.Equal(before, await File.ReadAllTextAsync(workspace.FilePath));
        var ownedId = Assert.Single(secrets.Writes);
        Assert.Equal(ownedId, Assert.Single(secrets.Deletes));
        Assert.DoesNotContain(ownedId, secrets.Values.Keys);
        Assert.Equal(Token, secrets.Values[TokenId]);
        Assert.Equal(Url, secrets.Values[UrlId]);
        Assert.DoesNotContain(TokenId, secrets.Reads);
        Assert.DoesNotContain(UrlId, secrets.Reads);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Requests++; return Task.FromResult(response(request)); }
    }
    private static HttpResponseMessage Ok(string body = "{\"ok\":true,\"result\":{\"message_id\":1}}") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task ExplicitTestsReadOnlyActiveCredentialOncePerOperationWithoutCaching(WebhookFormat format)
    {
        var secrets = new Secrets();
        var handler = new Handler(_ => Ok());
        var configuration = Configuration(format).Actions;
        await using var dispatcher = new AlertActionDispatcher(configuration, secretStore: secrets, httpHandler: handler);
        secrets.AssertIdle();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            Assert.True((await dispatcher.SendTestWebhookAsync(configuration.Webhook)).Success);
            Assert.Equal(attempt, secrets.Reads.Count);
            Assert.All(secrets.Reads, id => Assert.Equal(format == WebhookFormat.Telegram ? TokenId : UrlId, id));
        }
        Assert.Equal(2, handler.Requests);
        Assert.Empty(secrets.Writes);
        Assert.Empty(secrets.Deletes);
    }

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task ActualSuccessfulDeliveryReadsOnlyActiveCredentialOnce(WebhookFormat format)
    {
        var secrets = new Secrets();
        var handler = new Handler(_ => Ok());
        await using var dispatcher = new AlertActionDispatcher(Configuration(format).Actions, secretStore: secrets, httpHandler: handler);
        secrets.AssertIdle();
        var completion = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => completion.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(new(Guid.NewGuid(), Guid.Empty, DateTimeOffset.UtcNow, "Alert", "ordinary latency alert")));
        Assert.True((await completion.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(format == WebhookFormat.Telegram ? TokenId : UrlId, Assert.Single(secrets.Reads));
        Assert.Equal(1, handler.Requests);
        Assert.Empty(secrets.Writes);
        Assert.Empty(secrets.Deletes);
    }

    [Theory]
    [InlineData(WebhookFormat.Telegram)]
    [InlineData(WebhookFormat.Discord)]
    [InlineData(WebhookFormat.GenericJson)]
    public async Task ActualDeliveryReadsOnceAndMissingCredentialFailsWithoutSecretLeak(WebhookFormat format)
    {
        var secrets = new Secrets { FailRead = true };
        var handler = new Handler(_ => Ok());
        await using var dispatcher = new AlertActionDispatcher(Configuration(format).Actions, secretStore: secrets, httpHandler: handler);
        var completion = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => completion.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(new(Guid.NewGuid(), Guid.Empty, DateTimeOffset.UtcNow, "Alert", "ordinary latency alert")));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Success);
        Assert.Single(secrets.Reads);
        Assert.DoesNotContain(Token, result.Message);
        Assert.Equal(0, handler.Requests);
        Assert.Empty(secrets.Writes);
        Assert.Empty(secrets.Deletes);
    }
}
