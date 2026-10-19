using System.Net;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class TelegramCredentialVerificationTests
{
    private const string Token = "123456789:synthetic-token-do-not-log";
    private static WebhookAlertConfiguration Config() => new()
    {
        Enabled = true, Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = Guid.NewGuid().ToString("N"),
        TelegramChatId = "1234567890123"
    };
    private sealed class Secrets(int? error = null) : IWebhookSecretStore
    {
        public int Reads;
        public string Store(string value, string? existingId = null) => throw new Xunit.Sdk.XunitException("No writes permitted");
        public void Delete(string id) => throw new Xunit.Sdk.XunitException("No deletes permitted");
        public string Retrieve(string id)
        {
            Reads++;
            if (error is { } status) throw new WebhookCredentialException(WebhookCredentialOperation.Read, status);
            return Token;
        }
    }
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
    [Theory]
    [InlineData(-25293)]
    [InlineData(-25308)]
    [InlineData(-25300)]
    public async Task NativeReadFailureIsDistinguishedFromApiFailureWithoutSending(int status)
    {
        var store = new Secrets(status); var handler = new Handler(HttpStatusCode.OK, "{\"ok\":true}");
        using var output = new StringWriter();
        var config = Config();
        Assert.Equal(1, await TelegramCredentialVerification.RunAsync(config, store, output, handler));
        Assert.Contains("OSStatus " + status, output.ToString());
        Assert.Contains("No Telegram request was sent", output.ToString());
        Assert.Equal(1, store.Reads); Assert.Equal(0, handler.Requests);
        Assert.DoesNotContain(Token, output.ToString());
        Assert.DoesNotContain(config.TelegramBotTokenSecretId, output.ToString());
        Assert.DoesNotContain(config.TelegramChatId, output.ToString());
    }
    [Theory]
    [InlineData(401, "Invalid bot token")]
    [InlineData(403, "permission")]
    [InlineData(400, "Chat ID not found")]
    public async Task ApiFailureReportsSanitizedHttpAndApiStatus(int status, string explanation)
    {
        var store = new Secrets(); var handler = new Handler((HttpStatusCode)status,
            "{\"ok\":false,\"error_code\":" + status + ",\"description\":\"chat not found " + Token + "\"}");
        using var output = new StringWriter();
        Assert.Equal(1, await TelegramCredentialVerification.RunAsync(Config(), store, output, handler));
        Assert.Contains("HTTP " + status + ", API " + status, output.ToString());
        Assert.Contains(explanation, output.ToString());
        Assert.DoesNotContain("Keychain", output.ToString());
        Assert.DoesNotContain(Token, output.ToString());
        Assert.Equal(1, store.Reads); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task ApiLevelAuthenticationRejectionIsNotMistakenForKeychainFailure()
    {
        var store = new Secrets();
        var handler = new Handler(HttpStatusCode.OK, "{\"ok\":false,\"error_code\":401,\"description\":\"Unauthorized\"}");
        using var output = new StringWriter();
        Assert.Equal(1, await TelegramCredentialVerification.RunAsync(Config(), store, output, handler));
        Assert.Contains("HTTP 200, API 401", output.ToString());
        Assert.DoesNotContain("Keychain", output.ToString());
        Assert.Equal(1, store.Reads); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task ConfirmedApiSuccessUsesOnlyOneCredentialRead()
    {
        var store = new Secrets(); var handler = new Handler(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}");
        using var output = new StringWriter();
        Assert.Equal(0, await TelegramCredentialVerification.RunAsync(Config(), store, output, handler));
        Assert.Contains("API confirmed", output.ToString());
        Assert.Equal(1, store.Reads); Assert.Equal(1, handler.Requests);
    }
    [Fact]
    public async Task RawTokenAndOtherFormatsCannotBeUsedBySavedDiagnostic()
    {
        foreach (var config in new[] { Config() with { TelegramBotToken = Token }, Config() with { Format = WebhookFormat.Discord }, Config() with { Enabled = false } })
        {
            var store = new Secrets(); var handler = new Handler(HttpStatusCode.OK, "{\"ok\":true}");
            using var output = new StringWriter();
            Assert.Equal(1, await TelegramCredentialVerification.RunAsync(config, store, output, handler));
            Assert.Equal(0, store.Reads); Assert.Equal(0, handler.Requests);
        }
    }
}
