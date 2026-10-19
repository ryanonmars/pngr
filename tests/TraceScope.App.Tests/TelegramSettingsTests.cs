using System.Net;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class TelegramSettingsTests
{
    [AvaloniaFact]
    public void SelectingTelegramShowsOnlyRelevantInputsAndCancelDiscardsDraft()
    {
        var writes = 0;
        var window = Create(new(), _ => { writes++; return Task.CompletedTask; });
        window.Show(); window.SelectPage("Alerts & notifications");
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        var token = Find<TextBox>(window, "SettingsTelegramBotToken");
        Assert.True(token.IsEffectivelyVisible);
        Assert.False(Find<TextBox>(window, "SettingsWebhookUrl").IsEffectivelyVisible);
        Assert.Equal('●', token.PasswordChar);
        token.Text = "12345:replacement_secret";
        Find<TextBox>(window, "SettingsTelegramChatId").Text = "-1001234567890";
        Click(Find<Button>(window, "SettingsCancel"));
        Assert.Equal(0, writes);
        Assert.Equal("", token.Text);
    }

    [AvaloniaFact]
    public void SavedTokenIsOpaqueAndExplicitReplacementPreservesOtherFormats()
    {
        var webhook = new WebhookAlertConfiguration { Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = "11111111111111111111111111111111",
            TelegramChatId = "@public_channel", UrlSecretId = "22222222222222222222222222222222", BearerTokenEnvironmentVariable = "GENERIC_TOKEN" };
        SettingsChange? applied = null;
        var window = Create(new() { Actions = new() { Webhook = webhook } }, change => { applied = change; return Task.CompletedTask; });
        window.Show(); window.SelectPage("Alerts & notifications");
        var token = Find<TextBox>(window, "SettingsTelegramBotToken");
        Assert.True(string.IsNullOrEmpty(token.Text));
        Assert.False(token.IsEnabled);
        Click(Find<Button>(window, "SettingsReplaceTelegramToken"));
        Assert.True(token.IsEnabled);
        token.Text = "123456:replacement_secret";
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 0;
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 1;
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        Click(Find<Button>(window, "SettingsApply"));
        Assert.NotNull(applied);
        var saved = applied.Configuration.Actions.Webhook;
        Assert.Equal("123456:replacement_secret", saved.TelegramBotToken);
        Assert.Equal(webhook.TelegramBotTokenSecretId, saved.TelegramBotTokenSecretId);
        Assert.Equal("22222222222222222222222222222222", saved.UrlSecretId);
        Assert.Equal("GENERIC_TOKEN", saved.BearerTokenEnvironmentVariable);
        Assert.Equal(webhook.TelegramChatId, saved.TelegramChatId);
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyReplacementFieldsPreserveSavedCredentialsAcrossFormatChanges()
    {
        var webhook = new WebhookAlertConfiguration { Format = WebhookFormat.Telegram,
            TelegramBotTokenSecretId = "11111111111111111111111111111111", TelegramChatId = "@channel",
            UrlSecretId = "22222222222222222222222222222222" };
        SettingsChange? applied = null;
        var window = Create(new() { Actions = new() { Webhook = webhook } }, change => { applied = change; return Task.CompletedTask; });
        window.Show(); window.SelectPage("Alerts & notifications");
        Click(Find<Button>(window, "SettingsReplaceTelegramToken"));
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 0;
        Click(Find<Button>(window, "SettingsReplaceWebhookUrl"));
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        Click(Find<Button>(window, "SettingsApply"));
        Assert.NotNull(applied);
        Assert.Equal(webhook.TelegramBotTokenSecretId, applied.Configuration.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(webhook.UrlSecretId, applied.Configuration.Actions.Webhook.UrlSecretId);
        window.Close();
    }

    [AvaloniaFact]
    public void TelegramTestUsesEnabledUnsavedFieldsWithoutApplying()
    {
        WebhookAlertConfiguration? tested = null;
        var writes = 0;
        var window = Create(new(), _ => { writes++; return Task.CompletedTask; }, config =>
        { tested = config; return Task.FromResult(new NotificationResult(true, "Telegram confirmed delivery.")); });
        window.Show(); window.SelectPage("Alerts & notifications");
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        Find<TextBox>(window, "SettingsTelegramBotToken").Text = "123456:unsaved_secret";
        Find<TextBox>(window, "SettingsTelegramChatId").Text = "-1001234567890";
        Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
        Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked = true;
        Assert.True(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
        Click(Find<Button>(window, "SettingsTestWebhook"));
        Assert.NotNull(tested);
        Assert.Equal("123456:unsaved_secret", tested.TelegramBotToken);
        Assert.Equal("-1001234567890", tested.TelegramChatId);
        Assert.True(tested.Enabled);
        Assert.Equal(0, writes);
        Assert.Equal("Telegram confirmed delivery.", Find<TextBlock>(window, "SettingsWebhookTestStatus").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void RepeatedApplyUsesSavedReferenceWithoutRestoringToken()
    {
        WebhookAlertConfiguration saved = new();
        var drafts = new List<WebhookAlertConfiguration>();
        var window = Create(new(), change =>
        {
            drafts.Add(change.Configuration.Actions.Webhook);
            saved = change.Configuration.Actions.Webhook with { TelegramBotToken = "", TelegramBotTokenSecretId = "33333333333333333333333333333333" };
            return Task.CompletedTask;
        }, savedWebhook: () => saved);
        window.Show(); window.SelectPage("Alerts & notifications");
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        var token = Find<TextBox>(window, "SettingsTelegramBotToken");
        token.Text = "123456:replacement_secret";
        Find<TextBox>(window, "SettingsTelegramChatId").Text = "1234567890123";
        Click(Find<Button>(window, "SettingsApply"));
        Assert.Equal("", token.Text);
        Assert.False(token.IsEnabled);
        Click(Find<Button>(window, "SettingsApply"));
        Assert.Equal(2, drafts.Count);
        Assert.Equal("", drafts[1].TelegramBotToken);
        Assert.Equal("33333333333333333333333333333333", drafts[1].TelegramBotTokenSecretId);
        window.Close();
    }

    [AvaloniaFact]
    public void EnabledTelegramValidatesRequiredTokenAndChatIdBeforeApply()
    {
        var writes = 0;
        var window = Create(new(), _ => { writes++; return Task.CompletedTask; });
        window.Show(); window.SelectPage("Alerts & notifications");
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 2;
        Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked = true;
        Click(Find<Button>(window, "SettingsApply"));
        Assert.Equal(0, writes);
        Assert.Contains("Chat ID", Find<TextBlock>(window, "SettingsStatus").Text);
        Find<TextBox>(window, "SettingsTelegramChatId").Text = "-1001234567890";
        Click(Find<Button>(window, "SettingsApply"));
        Assert.Equal(0, writes);
        Assert.Contains("Bot Token", Find<TextBlock>(window, "SettingsStatus").Text);
        Find<TextBox>(window, "SettingsTelegramBotToken").Text = "123456:unsaved_secret";
        Find<TextBox>(window, "SettingsTelegramChatId").Text = "-1001234567890";
        Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked = true;
        Click(Find<Button>(window, "SettingsApply"));
        Assert.Equal(1, writes);
        window.Close();
    }

    [AvaloniaFact]
    public void PairingStagesDestinationAndMetadataUntilApply()
    {
        SettingsChange? applied = null;
        WebhookAlertConfiguration? pairingDraft = null;
        var config = new WorkspaceConfiguration { Actions = new() { Webhook = new() {
            Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = "11111111111111111111111111111111" } } };
        var window = new SettingsWindow(config, null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
            change => { applied = change; return Task.CompletedTask; }, () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
            pairTelegram: (draft, _, _) => { pairingDraft = draft; return Task.FromResult(new TelegramPairingResult("9876543210987", "PngrAlertsBot")); });
        window.Show(); window.SelectTelegramSettings();
        Assert.False(Find<Expander>(window, "SettingsTelegramAdvanced").IsExpanded);
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.Null(applied);
        Assert.NotNull(pairingDraft);
        Assert.Equal(config.Actions.Webhook.TelegramBotTokenSecretId, pairingDraft.TelegramBotTokenSecretId);
        Assert.Contains("Personal Chat", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Assert.Contains("Apply", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Assert.DoesNotContain("9876543210987", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Click(Find<Button>(window, "SettingsApply"));
        Assert.NotNull(applied);
        Assert.Equal("9876543210987", applied.Configuration.Actions.Webhook.TelegramChatId);
        Assert.Equal("PngrAlertsBot", applied.Configuration.Actions.Webhook.TelegramBotUsername);
        window.Close();
    }

    [AvaloniaFact]
    public void FailedPairingKeepsPreviousDestinationAndCancelDoesNotSave()
    {
        var writes = 0;
        var config = new WorkspaceConfiguration { Actions = new() { Webhook = new() { Format = WebhookFormat.Telegram,
            TelegramBotTokenSecretId = "11111111111111111111111111111111", TelegramChatId = "1234" } } };
        var window = new SettingsWindow(config, null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
            _ => { writes++; return Task.CompletedTask; }, () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
            pairTelegram: (_, _, _) => throw new TelegramPairingException("Invalid bot token."));
        window.Show(); window.SelectTelegramSettings();
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.Contains("Invalid bot token", Find<TextBlock>(window, "SettingsWebhookTestStatus").Text);
        Find<Expander>(window, "SettingsTelegramAdvanced").IsExpanded = true;
        Assert.Equal("1234", Find<TextBox>(window, "SettingsTelegramChatId").Text);
        Click(Find<Button>(window, "SettingsCancel"));
        Assert.Equal(0, writes);
    }

    [AvaloniaFact]
    public async Task ClosingSettingsCancelsPairingAndRejectsLateResult()
    {
        var pending = new TaskCompletionSource<TelegramPairingResult>();
        CancellationToken token = default;
        var writes = 0;
        var window = new SettingsWindow(new(), null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
            _ => { writes++; return Task.CompletedTask; }, () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
            pairTelegram: (_, _, ct) => { token = ct; return pending.Task; });
        window.Show(); window.SelectTelegramSettings();
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.False(Find<Button>(window, "SettingsApply").IsEnabled);
        Assert.True(Find<Button>(window, "SettingsCancel").IsEnabled);
        Click(Find<Button>(window, "SettingsCancel"));
        Assert.True(token.IsCancellationRequested);
        pending.SetResult(new("999", "OtherBot"));
        await Task.Yield();
        Assert.Equal(0, writes);
    }

    [AvaloniaFact]
    public void DisconnectStagesDisabledDeliveryAndPreservesCredentialAndOtherFormats()
    {
        SettingsChange? applied = null;
        var initial = new WebhookAlertConfiguration { Enabled = true, Format = WebhookFormat.Telegram,
            TelegramBotTokenSecretId = "11111111111111111111111111111111", TelegramChatId = "1234", TelegramBotUsername = "PngrAlertsBot",
            UrlSecretId = "22222222222222222222222222222222" };
        var window = Create(new() { Actions = new() { Webhook = initial } }, change => { applied = change; return Task.CompletedTask; });
        Click(Find<Button>(window, "SettingsDisconnectTelegram"));
        Assert.Null(applied);
        Assert.Equal("", Find<TextBox>(window, "SettingsTelegramChatId").Text);
        Click(Find<Button>(window, "SettingsApply"));
        Assert.NotNull(applied);
        Assert.False(applied.Configuration.Actions.Webhook.Enabled);
        Assert.Equal("", applied.Configuration.Actions.Webhook.TelegramBotUsername);
        Assert.Equal(initial.TelegramBotTokenSecretId, applied.Configuration.Actions.Webhook.TelegramBotTokenSecretId);
        Assert.Equal(initial.UrlSecretId, applied.Configuration.Actions.Webhook.UrlSecretId);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisconnectDuringReconnectCancelsPollingAndRejectsLateDestination()
    {
        var pending = new TaskCompletionSource<TelegramPairingResult>();
        CancellationToken cancellation = default;
        var initial = new WebhookAlertConfiguration { Enabled = true, Format = WebhookFormat.Telegram,
            TelegramBotTokenSecretId = "11111111111111111111111111111111", TelegramChatId = "1234", TelegramBotUsername = "PngrAlertsBot" };
        var window = new SettingsWindow(new() { Actions = new() { Webhook = initial } }, null, null,
            [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings", _ => Task.CompletedTask,
            () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
            pairTelegram: (_, _, ct) => { cancellation = ct; return pending.Task; });
        window.Show(); window.SelectTelegramSettings();
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.True(Find<Button>(window, "SettingsDisconnectTelegram").IsEnabled);
        Click(Find<Button>(window, "SettingsDisconnectTelegram"));
        Assert.True(cancellation.IsCancellationRequested);
        pending.SetResult(new("2222", "StaleBot"));
        await Task.Yield();
        Assert.Empty(Find<TextBox>(window, "SettingsTelegramChatId").Text!);
        Assert.False(Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked);
        Assert.Contains("Not connected", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task CancelRetainsGuardUntilPollingStopsAndReconnectRejectsStaleResult()
    {
        var pending = new TaskCompletionSource<TelegramPairingResult>();
        CancellationToken cancellation = default;
        var attempts = 0;
        var window = new SettingsWindow(new(), null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
            _ => Task.CompletedTask, () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
            pairTelegram: (_, _, ct) => { attempts++; cancellation = ct; return attempts == 1 ? pending.Task : Task.FromResult(new TelegramPairingResult("222", "ConnectedBot")); });
        window.Show(); window.SelectTelegramSettings();
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Click(Find<Button>(window, "SettingsCancelTelegram"));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(Find<Button>(window, "SettingsConnectTelegram").IsEnabled);
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.Equal(1, attempts);
        pending.SetResult(new("111", "StaleBot"));
        await Task.Yield();
        Assert.True(Find<Button>(window, "SettingsConnectTelegram").IsEnabled);
        Assert.Contains("Not connected", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Click(Find<Button>(window, "SettingsConnectTelegram"));
        Assert.Contains("ConnectedBot", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Assert.DoesNotContain("StaleBot", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TokenAndFormatChangesCancelPairingWithoutSaving()
    {
        foreach (var changeFormat in new[] { false, true })
        {
            var pending = new TaskCompletionSource<TelegramPairingResult>();
            CancellationToken cancellation = default;
            var window = new SettingsWindow(new(), null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
                _ => throw new InvalidOperationException("Must not save"), () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
                pairTelegram: (_, _, ct) => { cancellation = ct; return pending.Task; });
            window.Show(); window.SelectTelegramSettings();
            Click(Find<Button>(window, "SettingsConnectTelegram"));
            if (changeFormat) Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = 1;
            else Find<TextBox>(window, "SettingsTelegramBotToken").Text = "123:new_secret";
            Assert.True(cancellation.IsCancellationRequested);
            pending.SetResult(new("111", "StaleBot"));
            await Task.Yield();
            Assert.DoesNotContain("StaleBot", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RealPairConfirmApplyLifecycleRefreshesControlsFromPersistedCredential()
    {
        const string token = "123456:synthetic-test-secret";
        var directory = Path.Combine(Path.GetTempPath(), "pngr-controls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            var secrets = new MemorySecrets();
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"), secrets);
            var saved = await store.SaveAsync(new() { Actions = new() { Webhook = new() {
                Format = WebhookFormat.Telegram, Url = "https://discord.com/api/webhooks/123/existing", BearerTokenEnvironmentVariable = "EXISTING_GENERIC" } } });
            var originalUrlReference = saved.Actions.Webhook.UrlSecretId;
            var applyGate = new TaskCompletionSource();
            var applying = new TaskCompletionSource();
            string code = "";
            var calls = new List<string>();
            using var handler = new PairingHandler(request =>
            {
                var method = request.RequestUri!.AbsolutePath.Split('/').Last();
                calls.Add(method);
                return method switch
                {
                    "getMe" => "{\"ok\":true,\"result\":{\"id\":123456,\"is_bot\":true,\"username\":\"PngrControlsBot\"}}",
                    "getWebhookInfo" => "{\"ok\":true,\"result\":{\"url\":\"\"}}",
                    "getUpdates" => JsonSerializer.Serialize(new { ok = true, result = new[] { new { update_id = 1,
                        message = new { message_id = 2, date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            from = new { id = 42 }, chat = new { id = 9876543210987L, type = "private" }, text = "/start " + code } } } }),
                    "sendMessage" => "{\"ok\":true,\"result\":{\"message_id\":3}}",
                    _ => throw new InvalidOperationException("Unexpected Telegram method")
                };
            });
            await using var pairing = new TelegramPairingClient(secrets, handler);
            WebhookAlertConfiguration? tested = null;
            window = new SettingsWindow(saved, null, null, [new ProbeSourceChoice("Automatic", null, null)], directory,
                async change => { applying.SetResult(); await applyGate.Task; saved = await store.SaveAsync(change.Configuration); },
                () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { },
                savedWebhook: () => saved.Actions.Webhook,
                testWebhook: draft => { tested = draft; return Task.FromResult(new NotificationResult(true, "Telegram confirmed delivery.")); },
                pairTelegram: (draft, ready, ct) => pairing.PairAsync(draft, async prompt =>
                {
                    code = Uri.UnescapeDataString(new Uri(prompt.DeepLink).Query.Split("start=")[1]);
                    await ready(prompt);
                }, ct), openTelegram: _ => { });
            window.Show(); window.SelectTelegramSettings();
            Find<TextBox>(window, "SettingsTelegramBotToken").Text = token;
            Click(Find<Button>(window, "SettingsConnectTelegram"));
            await WaitUntil(() => Find<TextBlock>(window, "SettingsWebhookTestStatus").Text?.Contains("confirmed the connection") == true);
            Assert.Equal(new[] { "getMe", "getWebhookInfo", "getUpdates", "sendMessage" }, calls);
            Assert.Empty(saved.Actions.Webhook.TelegramChatId);
            Assert.True(Find<Button>(window, "SettingsTestWebhook").IsEffectivelyEnabled);
            Click(Find<Button>(window, "SettingsApply"));
            await applying.Task;
            Assert.False(Find<Button>(window, "SettingsApply").IsEnabled);
            applyGate.SetResult();
            await WaitUntil(() => Find<TextBlock>(window, "SettingsStatus").Text?.StartsWith("Settings saved.") == true);
            AssertConnectedControls(window);
            Assert.Empty(Find<TextBox>(window, "SettingsTelegramBotToken").Text!);
            Assert.False(Find<TextBox>(window, "SettingsTelegramBotToken").IsEnabled);
            Assert.Empty(saved.Actions.Webhook.TelegramBotToken);
            Assert.Matches("^[0-9a-f]{32}$", saved.Actions.Webhook.TelegramBotTokenSecretId);
            Assert.Equal(token, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(Path.Combine(directory, "workspace.json")));
            Assert.Equal("9876543210987", saved.Actions.Webhook.TelegramChatId);
            Assert.Equal(originalUrlReference, saved.Actions.Webhook.UrlSecretId);
            Assert.Equal("EXISTING_GENERIC", saved.Actions.Webhook.BearerTokenEnvironmentVariable);
            Click(Find<Button>(window, "SettingsTestWebhook"));
            Assert.NotNull(tested);
            Assert.Empty(tested.TelegramBotToken);
            Assert.Equal(saved.Actions.Webhook.TelegramBotTokenSecretId, tested.TelegramBotTokenSecretId);
            Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked = false;
            Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEffectivelyEnabled);
            Assert.True(Find<Button>(window, "SettingsConnectTelegram").IsEffectivelyEnabled);
            Assert.True(Find<Button>(window, "SettingsDisconnectTelegram").IsEffectivelyEnabled);
            Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked = true;
            AssertConnectedControls(window);
            Click(Find<Button>(window, "SettingsDisconnectTelegram"));
            Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
            Assert.Empty(Find<TextBox>(window, "SettingsTelegramChatId").Text!);
            Click(Find<Button>(window, "SettingsCancel"));
            saved = (await store.LoadAsync())!;
            window = Create(saved, _ => Task.CompletedTask, _ => Task.FromResult(new NotificationResult(true, "Confirmed")), () => saved.Actions.Webhook);
            AssertConnectedControls(window);
            Click(Find<Button>(window, "SettingsReplaceTelegramToken"));
            Find<TextBox>(window, "SettingsTelegramBotToken").Text = "123456:replacement-secret";
            Assert.True(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
            Assert.Equal("9876543210987", Find<TextBox>(window, "SettingsTelegramChatId").Text);
            Click(Find<Button>(window, "SettingsCancel"));
            Assert.Equal(token, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
            window = Create((await store.LoadAsync())!, async change => saved = await store.SaveAsync(change.Configuration),
                _ => Task.FromResult(new NotificationResult(true, "Confirmed")), () => saved.Actions.Webhook);
            AssertConnectedControls(window);
            Click(Find<Button>(window, "SettingsDisconnectTelegram"));
            Click(Find<Button>(window, "SettingsApply"));
            await WaitUntil(() => Find<TextBlock>(window, "SettingsStatus").Text?.StartsWith("Settings saved.") == true);
            Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
            Assert.True(Find<Button>(window, "SettingsConnectTelegram").IsEffectivelyEnabled);
            Assert.False(Find<Button>(window, "SettingsDisconnectTelegram").IsVisible);
            Assert.Equal(token, secrets.Retrieve(saved.Actions.Webhook.TelegramBotTokenSecretId));
            Assert.False(saved.Actions.Webhook.Enabled);
            window.Close();
            window = Create((await store.LoadAsync())!, _ => Task.CompletedTask, _ => Task.FromResult(new NotificationResult(true, "Confirmed")));
            Assert.Empty(Find<TextBox>(window, "SettingsTelegramChatId").Text!);
            Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
            Assert.True(Find<Button>(window, "SettingsConnectTelegram").IsEffectivelyEnabled);
        }
        finally { window?.Close(); Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task FailedApplyRestoresConnectedControlsAndRetainsDraft()
    {
        var failure = new TaskCompletionSource();
        var config = new WorkspaceConfiguration { Actions = new() { Webhook = new() { Enabled = true,
            Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = "11111111111111111111111111111111",
            TelegramChatId = "1234", TelegramBotUsername = "PngrControlsBot" } } };
        var window = Create(config, async _ => { await failure.Task; throw new IOException("Settings write failed."); },
            _ => Task.FromResult(new NotificationResult(true, "Confirmed")));
        Click(Find<Button>(window, "SettingsApply"));
        failure.SetResult();
        await WaitUntil(() => Find<TextBlock>(window, "SettingsStatus").Text?.Contains("were not applied") == true);
        AssertConnectedControls(window);
        Assert.Equal("1234", Find<TextBox>(window, "SettingsTelegramChatId").Text);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExistingWebhookFormatsCanTestUnsavedValuesWithDeliveryDisabled(int format)
    {
        WebhookAlertConfiguration? tested = null;
        var window = Create(new(), _ => Task.CompletedTask, draft =>
        { tested = draft; return Task.FromResult(new NotificationResult(true, "Confirmed")); });
        Find<ComboBox>(window, "SettingsWebhookFormat").SelectedIndex = format;
        Find<TextBox>(window, "SettingsWebhookUrl").Text = "https://discord.com/api/webhooks/123/existing";
        Assert.False(Find<CheckBox>(window, "SettingsWebhookEnabled").IsChecked);
        Assert.True(Find<Button>(window, "SettingsTestWebhook").IsEffectivelyEnabled);
        Click(Find<Button>(window, "SettingsTestWebhook"));
        Assert.NotNull(tested);
        Assert.Equal((WebhookFormat)format, tested.Format);
        Assert.True(tested.Enabled);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("", "1234")]
    [InlineData("not-a-secure-reference", "1234")]
    [InlineData("11111111111111111111111111111111", "")]
    [InlineData("11111111111111111111111111111111", "not-a-chat")]
    public void TelegramTestRequiresUsableCredentialAndDestination(string secretId, string chatId)
    {
        var config = new WorkspaceConfiguration { Actions = new() { Webhook = new() { Enabled = true,
            Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = secretId, TelegramChatId = chatId } } };
        var window = Create(config, _ => Task.CompletedTask, _ => Task.FromResult(new NotificationResult(true, "Confirmed")));
        Assert.False(Find<Button>(window, "SettingsTestWebhook").IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReplacementPreservesDestinationVerifiesNewEntryAndTestsAfterReopeningWithoutOldRead()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pngr-token-replacement-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        SettingsWindow? window = null;
        try
        {
            const string oldId = "11111111111111111111111111111111";
            var secrets = new MemorySecrets();
            var store = new WorkspaceStore(Path.Combine(directory, "workspace.json"), secrets);
            var saved = await store.SaveAsync(new() { Actions = new() { Webhook = new() { Enabled = true,
                Format = WebhookFormat.Telegram, TelegramBotTokenSecretId = oldId,
                TelegramChatId = "-1001234567890", TelegramBotUsername = "PngrAlertsBot",
                UrlSecretId = "22222222222222222222222222222222" } } });
            var requests = 0;
            await using var dispatcher = new AlertActionDispatcher(secretStore: secrets,
                httpHandler: new PairingHandler(_ => { requests++; return "{\"ok\":true,\"result\":{}}"; }));
            window = Create(saved, async change => saved = await store.SaveAsync(change.Configuration),
                draft => dispatcher.SendTestWebhookAsync(draft), () => saved.Actions.Webhook);
            Click(Find<Button>(window, "SettingsReplaceTelegramToken"));
            Find<TextBox>(window, "SettingsTelegramBotToken").Text = "123456:replacement-secret";
            AssertConnectedControls(window);
            Assert.Equal("-1001234567890", Find<TextBox>(window, "SettingsTelegramChatId").Text);
            Click(Find<Button>(window, "SettingsApply"));
            await WaitUntil(() => Find<TextBlock>(window, "SettingsStatus").Text?.StartsWith("Settings saved.") == true);
            Assert.NotEqual(oldId, saved.Actions.Webhook.TelegramBotTokenSecretId);
            Assert.Equal("PngrAlertsBot", saved.Actions.Webhook.TelegramBotUsername);
            Assert.Equal("22222222222222222222222222222222", saved.Actions.Webhook.UrlSecretId);
            Assert.Single(secrets.Reads); // New entry readback before settings commit.
            Assert.DoesNotContain(oldId, secrets.Reads);
            Assert.Empty(secrets.Deletes);
            window.Close();
            saved = (await store.LoadAsync())!;
            window = Create(saved, _ => Task.CompletedTask, draft => dispatcher.SendTestWebhookAsync(draft));
            AssertConnectedControls(window);
            Click(Find<Button>(window, "SettingsTestWebhook"));
            await WaitUntil(() => Find<TextBlock>(window, "SettingsWebhookTestStatus").Text?.Contains("sent successfully") == true);
            Assert.Equal(1, requests);
            Assert.Equal(2, secrets.Reads.Count);
            Assert.DoesNotContain(oldId, secrets.Reads);
            Assert.DoesNotContain("replacement-secret", await File.ReadAllTextAsync(store.Path));
        }
        finally { window?.Close(); Directory.Delete(directory, true); }
    }

    private static void AssertConnectedControls(Window window)
    {
        Assert.Contains("Connected", Find<TextBlock>(window, "SettingsTelegramConnection").Text);
        Assert.True(Find<Button>(window, "SettingsTestWebhook").IsEffectivelyEnabled, "Send Test must be enabled immediately after Apply.");
        Assert.True(Find<Button>(window, "SettingsConnectTelegram").IsEffectivelyEnabled, "Reconnect must be enabled immediately after Apply.");
        Assert.True(Find<Button>(window, "SettingsDisconnectTelegram").IsEffectivelyEnabled, "Disconnect must be enabled immediately after Apply.");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10);
        Assert.True(condition(), "Settings operation did not complete.");
    }
    private sealed class PairingHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request)) });
    }
    private sealed class MemorySecrets : IWebhookSecretStore
    {
        private readonly Dictionary<string, string> _values = [];
        public readonly List<string> Reads = [];
        public readonly List<string> Deletes = [];
        public string Store(string value, string? existingId = null)
        {
            var id = existingId ?? Guid.NewGuid().ToString("N");
            _values[id] = value;
            return id;
        }
        public string Retrieve(string id) { Reads.Add(id); return _values[id]; }
        public void Delete(string id) { Deletes.Add(id); _values.Remove(id); }
    }

    private static SettingsWindow Create(WorkspaceConfiguration configuration, Func<SettingsChange, Task> apply,
        Func<WebhookAlertConfiguration, Task<NotificationResult>>? test = null, Func<WebhookAlertConfiguration>? savedWebhook = null)
    {
        var window = new SettingsWindow(configuration, null, null, [new ProbeSourceChoice("Automatic", null, null)], "/tmp/pngr-telegram-settings",
            apply, () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { }, savedWebhook: savedWebhook, testWebhook: test);
        window.Show(); window.SelectTelegramSettings();
        Find<Expander>(window, "SettingsTelegramAdvanced").IsExpanded = true;
        return window;
    }
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Concat(window.GetLogicalDescendants().OfType<T>()).Distinct().Single(c => c.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
