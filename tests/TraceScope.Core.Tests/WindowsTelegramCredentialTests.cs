using System.Net;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

/// <summary>Opt-in Windows Credential Manager checks using disposable synthetic credentials and mocked HTTP only.</summary>
public sealed class WindowsTelegramCredentialTests
{
    private const string Token = "123456789:windows-disposable-synthetic-token";
    private const string Replacement = "987654321:windows-disposable-replacement-token";
    private const string Discord = "https://discord.com/api/webhooks/123456789/windows-disposable-synthetic";
    private const string Chat = "4294967296";

    [WindowsCredentialFact]
    public async Task NativeCredentialsSurviveSettingsRestartReplacementAndFormatChanges()
    {
        using var credentials = new DisposableCredentials();
        using var workspace = new TemporaryWorkspace();
        var store = new WorkspaceStore(workspace.Path, credentials);
        var saved = await store.SaveAsync(new() { Actions = new() { Webhook = new()
        {
            Enabled = true, Format = WebhookFormat.Telegram, TelegramBotToken = Token,
            TelegramChatId = Chat, TelegramBotUsername = "PngrWindowsTestBot", Url = Discord
        } } });
        var originalTokenId = saved.Actions.Webhook.TelegramBotTokenSecretId;
        var discordId = saved.Actions.Webhook.UrlSecretId;
        Assert.NotEmpty(originalTokenId);
        Assert.NotEmpty(discordId);
        Assert.True(credentials.Retrieve(originalTokenId) == Token);
        Assert.True(credentials.Retrieve(discordId) == Discord);
        await AssertNoPlaintext(workspace.Path);

        // Recreate both objects, exercising native persistence rather than an in-memory cache.
        var restartedStore = new WorkspaceStore(workspace.Path, WebhookSecretStore.Create());
        var restored = (await restartedStore.LoadAsync())!;
        Assert.Equal(saved.Actions.Webhook, restored.Actions.Webhook);
        await VerifyDelivery(restored.Actions.Webhook, Token, WebhookSecretStore.Create());

        // Test an unsaved replacement, then discard it: saved references remain untouched.
        await VerifyDelivery(restored.Actions.Webhook with { TelegramBotToken = Replacement }, Replacement, credentials);
        Assert.Equal(originalTokenId, (await restartedStore.LoadAsync())!.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.True(credentials.Retrieve(originalTokenId) == Token);

        var replaced = await store.SaveAsync(restored with { Actions = restored.Actions with
        { Webhook = restored.Actions.Webhook with { TelegramBotToken = Replacement } } });
        Assert.NotEqual(originalTokenId, replaced.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(discordId, replaced.Actions.Webhook.UrlSecretId);
        Assert.Equal(Chat, replaced.Actions.Webhook.TelegramChatId);
        Assert.Equal("PngrWindowsTestBot", replaced.Actions.Webhook.TelegramBotUsername);
        Assert.True(credentials.Retrieve(originalTokenId) == Token); // Old credential remains preserved.
        Assert.True(credentials.Retrieve(discordId) == Discord);
        var afterRestart = (await new WorkspaceStore(workspace.Path, WebhookSecretStore.Create()).LoadAsync())!;
        await VerifyDelivery(afterRestart.Actions.Webhook, Replacement, WebhookSecretStore.Create());

        foreach (var format in new[] { WebhookFormat.Discord, WebhookFormat.GenericJson, WebhookFormat.Telegram })
        {
            replaced = await store.SaveAsync(replaced with { Actions = replaced.Actions with
            { Webhook = replaced.Actions.Webhook with { Format = format } } });
            Assert.Equal(discordId, replaced.Actions.Webhook.UrlSecretId);
            Assert.True(credentials.Retrieve(discordId) == Discord);
            Assert.True(credentials.Retrieve(replaced.Actions.Webhook.TelegramBotTokenSecretId) == Replacement);
        }
        var disconnected = await store.SaveAsync(replaced with { Actions = replaced.Actions with
        { Webhook = replaced.Actions.Webhook with { Enabled = false, TelegramChatId = "", TelegramBotUsername = "" } } });
        Assert.Equal(replaced.Actions.Webhook.TelegramBotTokenSecretId, disconnected.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.False((await restartedStore.LoadAsync())!.Actions.Webhook.Enabled);
        Assert.True(credentials.Retrieve(discordId) == Discord);
        await AssertNoPlaintext(workspace.Path);
    }

    [WindowsCredentialFact]
    public async Task PairingResolvesNativeTokenAndConfirmsActualPrivateChat()
    {
        using var credentials = new DisposableCredentials();
        var tokenId = credentials.Store(Token);
        string code = "";
        var calls = new List<string>();
        var handler = new MockHandler(async (request, ct) =>
        {
            Assert.StartsWith("/bot" + Token + "/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var method = request.RequestUri.AbsolutePath.Split('/').Last();
            calls.Add(method);
            return method switch
            {
                "getMe" => Reply("{\"ok\":true,\"result\":{\"id\":123456789,\"is_bot\":true,\"username\":\"PngrWindowsTestBot\"}}"),
                "getWebhookInfo" => Reply("{\"ok\":true,\"result\":{\"url\":\"\"}}"),
                "getUpdates" => Reply(JsonSerializer.Serialize(new { ok = true, result = new[] { new
                {
                    update_id = 7, message = new { message_id = 8, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    from = new { id = 123 }, chat = new { id = 4294967296L, type = "private" }, text = "/start " + code }
                } } })),
                "sendMessage" => await Confirm(request, ct),
                _ => throw new InvalidOperationException("Unexpected mock method")
            };
        });
        await using var pairing = new TelegramPairingClient(secretStore: WebhookSecretStore.Create(), httpHandler: handler);
        var result = await pairing.PairAsync(new() { Enabled = true, Format = WebhookFormat.Telegram,
            TelegramBotTokenSecretId = tokenId }, prompt =>
        {
            code = Uri.UnescapeDataString(new Uri(prompt.DeepLink).Query.Split("start=")[1]);
            Assert.DoesNotContain(Token, prompt.DeepLink);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(Chat, result.ChatId);
        Assert.Equal("PngrWindowsTestBot", result.BotUsername);
        Assert.Equal(new[] { "getMe", "getWebhookInfo", "getUpdates", "sendMessage" }, calls);
        Assert.True(credentials.Retrieve(tokenId) == Token);
    }

    private static async Task<HttpResponseMessage> Confirm(HttpRequestMessage request, CancellationToken ct)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Assert.Equal(Chat, body.RootElement.GetProperty("chat_id").GetString());
        return Reply("{\"ok\":true,\"result\":{\"message_id\":9}}");
    }

    private static async Task VerifyDelivery(WebhookAlertConfiguration webhook, string token, IWebhookSecretStore credentials)
    {
        var calls = 0;
        var handler = new MockHandler(async (request, ct) =>
        {
            calls++;
            Assert.True(request.RequestUri!.AbsoluteUri == "https://api.telegram.org/bot" + token + "/sendMessage");
            Assert.Equal(HttpMethod.Post, request.Method);
            return await Confirm(request, ct);
        });
        await using var dispatcher = new AlertActionDispatcher(new() { Webhook = webhook },
            httpHandler: handler, secretStore: credentials);
        var result = await dispatcher.SendTestWebhookAsync(webhook);
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, calls);
    }

    private static async Task AssertNoPlaintext(string path)
    {
        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(Replacement, json);
        Assert.DoesNotContain(Discord, json);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class MockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => reply(request, ct);
    }
    private sealed class TemporaryWorkspace : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pngr-windows-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "workspace.json");
        public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class DisposableCredentials : IWebhookSecretStore, IDisposable
    {
        private readonly IWebhookSecretStore native = WebhookSecretStore.Create();
        private readonly HashSet<string> owned = [];
        public string Store(string value, string? existingId = null)
        {
            var id = existingId ?? Guid.NewGuid().ToString("N");
            // Never access or clean up any pre-existing application credential.
            if (existingId is not null && !owned.Contains(id))
            {
                Assert.ThrowsAny<InvalidOperationException>(() => native.Retrieve(id));
            }
            owned.Add(id);
            return native.Store(value, id);
        }
        public string Retrieve(string id) => native.Retrieve(id);
        public void Delete(string id) { Assert.Contains(id, owned); native.Delete(id); }
        public void Dispose() { foreach (var id in owned) native.Delete(id); }
    }
    private sealed class WindowsCredentialFactAttribute : FactAttribute
    {
        public WindowsCredentialFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("PNGR_NATIVE_SECRET_TESTS") != "1")
                Skip = "Requires Windows and explicit PNGR_NATIVE_SECRET_TESTS=1; uses only disposable credentials and mocked Telegram HTTP.";
        }
    }
}
