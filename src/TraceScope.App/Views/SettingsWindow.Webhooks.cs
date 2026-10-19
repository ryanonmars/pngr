using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TraceScope.Core;

namespace TraceScope.App.Views;

public sealed partial class SettingsWindow
{
    private WebhookAlertConfiguration _webhookDraft = new();
    private Func<WebhookAlertConfiguration>? _savedWebhook;
    private Func<WebhookAlertConfiguration, Task<NotificationResult>>? _testWebhook;
    private readonly CheckBox _webhookEnabled = Toggle("SettingsWebhookEnabled", "Enable webhook delivery");
    private readonly ComboBox _webhookFormat = new() { Name = "SettingsWebhookFormat", Width = 200,
        ItemsSource = new[] { "Generic JSON", "Discord", "Telegram" } };
    private readonly TextBox _webhookUrl = new() { Name = "SettingsWebhookUrl", MaxLength = 2560, PasswordChar = '●', MinWidth = 240 };
    private readonly TextBox _webhookBearer = new() { Name = "SettingsWebhookBearerReference", MinWidth = 240 };
    private readonly TextBox _telegramToken = new() { Name = "SettingsTelegramBotToken", MaxLength = 256, PasswordChar = '●', MinWidth = 240 };
    private readonly TextBox _telegramChat = new() { Name = "SettingsTelegramChatId", MinWidth = 240, MaxLength = 256,
        Watermark = "123456789, -1001234567890 or @channel" };
    private readonly Button _replaceTelegramToken = new() { Name = "SettingsReplaceTelegramToken", Content = "Replace bot token…" };
    private readonly Button _testWebhookButton = new() { Name = "SettingsTestWebhook", Content = "Test notification" };
    private readonly TextBlock _webhookTestStatus = new() { Name = "SettingsWebhookTestStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _connectTelegram = new() { Name = "SettingsConnectTelegram", Content = "Connect Telegram" };
    private readonly Button _disconnectTelegram = new() { Name = "SettingsDisconnectTelegram", Content = "Disconnect" };
    private readonly Button _openTelegramButton = new() { Name = "SettingsOpenTelegram", Content = "Open Telegram", IsVisible = false };
    private readonly Button _cancelTelegram = new() { Name = "SettingsCancelTelegram", Content = "Cancel pairing", IsVisible = false };
    private readonly TextBlock _telegramConnection = new() { Name = "SettingsTelegramConnection", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private Func<WebhookAlertConfiguration, Func<TelegramPairingPrompt, Task>, CancellationToken, Task<TelegramPairingResult>>? _pairTelegram;
    private Action<string>? _openTelegram;
    private CancellationTokenSource? _pairingTelegram;
    private string? _telegramDeepLink;
    private Control? _telegramTokenRow;
    private bool _settingTelegramFields;
    private bool _telegramDraftChanged;
    private bool _webhookClosed;
    private bool _replacingTelegramToken;
    private bool _replacingWebhookUrl;
    private bool _testingWebhook;

    private Control BuildWebhookSettings(WebhookAlertConfiguration configuration,
        Func<WebhookAlertConfiguration>? savedWebhook,
        Func<WebhookAlertConfiguration, Task<NotificationResult>>? testWebhook)
    {
        _webhookDraft = configuration;
        _savedWebhook = savedWebhook;
        _testWebhook = testWebhook;
        _webhookEnabled.IsChecked = configuration.Enabled;
        _webhookFormat.SelectedIndex = (int)configuration.Format;
        _webhookUrl.Text = configuration.Url;
        _webhookBearer.Text = configuration.BearerTokenEnvironmentVariable;
        _telegramToken.Text = configuration.TelegramBotToken;
        _telegramChat.Text = configuration.TelegramChatId;
        var replaceUrl = new Button { Name = "SettingsReplaceWebhookUrl", Content = "Replace webhook URL…" };
        replaceUrl.Click += (_, _) => { _replacingWebhookUrl = true; _webhookUrl.IsEnabled = true; _webhookUrl.Text = ""; _webhookUrl.Focus(); };
        _replaceTelegramToken.Click += (_, _) =>
        {
            CancelTelegramPairing();
            _replacingTelegramToken = true;
            _telegramToken.IsEnabled = true;
            _telegramToken.Text = "";
            _telegramToken.Watermark = "Paste replacement bot token";
            _webhookTestStatus.Text = "Paste the token for the same bot, then Send Test and Apply. Your destination is kept. For a different bot, use Reconnect Telegram. The previous credential is preserved.";
            UpdateTelegramConnection();
            _telegramToken.Focus();
        };
        ToolTip.SetTip(_telegramToken, "Stored in your operating system credential store. Saved tokens cannot be displayed.");
        ToolTip.SetTip(_telegramChat, "A numeric user/group ID (including negative IDs), or a public channel username beginning with @.");
        var urlPanel = new StackPanel { Spacing = 8, Children = { Row("Webhook URL", _webhookUrl), replaceUrl } };
        var bearerRow = Row("Bearer token variable", _webhookBearer);
        var docs = new Button { Content = "Telegram Bot API documentation ↗", HorizontalAlignment = HorizontalAlignment.Left };
        docs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("https://core.telegram.org/bots/api") { UseShellExecute = true }); }
            catch { _webhookTestStatus.Text = "Open https://core.telegram.org/bots/api in your browser."; }
        };
        _webhookEnabled.IsCheckedChanged += (_, _) => UpdateTelegramConnection();
        _connectTelegram.Click += async (_, _) => await ConnectTelegramAsync();
        _disconnectTelegram.Click += (_, _) =>
        {
            CancelTelegramPairing();
            _telegramChat.Text = "";
            _webhookDraft = _webhookDraft with { TelegramBotUsername = "" };
            if (_webhookFormat.SelectedIndex == (int)WebhookFormat.Telegram) _webhookEnabled.IsChecked = false;
            _telegramDraftChanged = true;
            UpdateTelegramConnection();
        };
        _cancelTelegram.Click += (_, _) => { CancelTelegramPairing(); UpdateTelegramConnection(); };
        _openTelegramButton.Click += (_, _) => OpenTelegramConversation();
        _telegramToken.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || _settingTelegramFields) return;
            CancelTelegramPairing();
            if (!string.IsNullOrWhiteSpace(_telegramToken.Text) || change.OldValue is string oldToken && !string.IsNullOrWhiteSpace(oldToken))
            {
                if (!_replacingTelegramToken)
                {
                    _telegramChat.Text = "";
                    _webhookDraft = _webhookDraft with { TelegramBotUsername = "" };
                    if (_webhookFormat.SelectedIndex == (int)WebhookFormat.Telegram) _webhookEnabled.IsChecked = false;
                }
                _telegramDraftChanged = true;
            }
            UpdateTelegramConnection();
        };
        _telegramChat.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty || _settingTelegramFields) return;
            CancelTelegramPairing();
            _webhookDraft = _webhookDraft with { TelegramBotUsername = "" };
            _telegramDraftChanged = true;
            UpdateTelegramConnection();
        };
        var advanced = new Expander { Name = "SettingsTelegramAdvanced", Header = "Advanced", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 8, Children = {
                Note("Manual configuration is for groups, channels and bots shared with other applications. Use an existing destination ID or public channel username."),
                Row("Manual Chat ID", _telegramChat) } } };
        _telegramTokenRow = Row("Telegram Bot Token", _telegramToken);
        var telegramPanel = new StackPanel { Spacing = 9, Children =
        {
            _telegramTokenRow, _telegramConnection,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _openTelegramButton, _cancelTelegram } }
        } };
        var telegramGuidance = new StackPanel { Spacing = 8, Children =
        {
            Note("Create a bot with @BotFather using /newbot, paste its token, then click Connect Telegram. Pngr opens the bot conversation; press Start to connect. Use a dedicated bot: other polling applications can interfere. No inbound ports or LAN web server are required."), docs
        } };
        void UpdateFormat()
        {
            var telegram = _webhookFormat.SelectedIndex == (int)WebhookFormat.Telegram;
            telegramPanel.IsVisible = telegramGuidance.IsVisible = advanced.IsVisible = telegram;
            urlPanel.IsVisible = !telegram;
            bearerRow.IsVisible = _webhookFormat.SelectedIndex == (int)WebhookFormat.GenericJson;
            if (!telegram) CancelTelegramPairing();
            _testWebhookButton.Content = telegram ? "Send Test" : "Test Webhook Notification";
            UpdateTelegramConnection();
            _webhookTestStatus.Text = "";
        }
        _webhookFormat.SelectionChanged += (_, _) => UpdateFormat();
        _testWebhookButton.Click += async (_, _) =>
        {
            if (!_testWebhookButton.IsEnabled || _busy || _testingWebhook || _pairingTelegram is not null || _testWebhook is null) return;
            try
            {
                var draft = ReadWebhookDraft() with { Enabled = true };
                (_original.Actions with { Webhook = draft }).Validate();
                _testingWebhook = true;
                UpdateTelegramConnection();
                _cancelButton.IsEnabled = false;
                _webhookTestStatus.Text = "Testing current fields (including unsaved changes)…";
                _webhookTestStatus.Text = (await _testWebhook(draft)).Message;
            }
            catch (InvalidDataException error) { _webhookTestStatus.Text = error.Message; }
            catch { _webhookTestStatus.Text = "Notification test failed. Check configuration, secure-store access and your connection."; }
            finally
            {
                _testingWebhook = false;
                UpdateTelegramConnection();
                _cancelButton.IsEnabled = !_busy;
            }
        };
        _testWebhookButton.IsEnabled = testWebhook is not null;
        Closed += (_, _) =>
        {
            _webhookClosed = true;
            CancelTelegramPairing();
            _settingTelegramFields = true;
            _telegramToken.Text = ""; _webhookUrl.Text = "";
            _webhookDraft = _webhookDraft with { TelegramBotToken = "", Url = "" };
            _settingTelegramFields = false;
        };
        UpdateSavedWebhookFields();
        UpdateFormat();
        return new StackPanel { Spacing = 10, Children =
        {
            Heading("Webhooks"), _webhookEnabled, Row("Webhook format", _webhookFormat), urlPanel, bearerRow,
            telegramPanel,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _testWebhookButton, _connectTelegram, _disconnectTelegram } },
            _replaceTelegramToken, _webhookTestStatus, advanced,
            Note("Test uses the current fields without saving. Apply or OK saves changes; Cancel discards them."), telegramGuidance
        } };
    }

    private void UpdateTelegramConnection()
    {
        var pairing = _pairingTelegram is not null;
        var configured = !string.IsNullOrWhiteSpace(_telegramChat.Text);
        var telegram = _webhookFormat.SelectedIndex == (int)WebhookFormat.Telegram;
        var idle = !_webhookClosed && !_busy && !_testingWebhook;
        if (_telegramTokenRow is not null) _telegramTokenRow.IsVisible = !configured || _replacingTelegramToken;
        _connectTelegram.IsVisible = telegram;
        _replaceTelegramToken.IsVisible = telegram && (_webhookDraft.TelegramBotTokenSecretId.Length > 0 || configured);
        _testWebhookButton.IsVisible = !telegram || configured;
        _connectTelegram.IsEnabled = idle && !pairing;
        _connectTelegram.Content = configured ? "Reconnect Telegram" : "Connect Telegram";
        _disconnectTelegram.IsVisible = telegram && configured;
        _disconnectTelegram.IsEnabled = idle && configured;
        _replaceTelegramToken.IsEnabled = idle;
        _openTelegramButton.IsVisible = pairing && _telegramDeepLink is not null;
        _cancelTelegram.IsVisible = pairing;
        _testWebhookButton.IsEnabled = idle && !pairing && _testWebhook is not null &&
            (!telegram || _webhookEnabled.IsChecked == true && HasValidTelegramDraft());
        _applyButton.IsEnabled = _okButton.IsEnabled = idle && !pairing;
        if (pairing) return;
        var bot = string.IsNullOrEmpty(_webhookDraft.TelegramBotUsername) ? "" : "Bot: @" + _webhookDraft.TelegramBotUsername + "\n";
        var destination = string.IsNullOrEmpty(_webhookDraft.TelegramBotUsername) ? "Manual destination configured" : "Connected · Personal Chat";
        _telegramConnection.Text = bot + (configured ? destination : "Not connected") +
            (_telegramDraftChanged ? "\nChanges are staged. Click Apply or OK to save; Cancel discards them." : "");
    }

    private bool HasValidTelegramDraft()
    {
        // Local validation accepts either a staged token or an opaque saved reference.
        // Refreshing controls must never retrieve or redisplay a saved credential.
        try
        {
            new AlertActionConfiguration { Webhook = ReadWebhookDraft() with { Enabled = true } }.Validate();
            return true;
        }
        catch (InvalidDataException) { return false; }
    }

    private void CancelTelegramPairing()
    {
        var session = _pairingTelegram;
        _telegramDeepLink = null;
        if (session is not null)
        {
            session.Cancel();
            _telegramConnection.Text = "Canceling Telegram pairing…";
        }
    }

    private void OpenTelegramConversation()
    {
        if (_telegramDeepLink is null) return;
        try
        {
            if (_openTelegram is { } open) open(_telegramDeepLink);
            else Process.Start(new ProcessStartInfo(_telegramDeepLink) { UseShellExecute = true });
        }
        catch { _webhookTestStatus.Text = "Could not open Telegram. Click Open Telegram to try again."; }
    }

    private async Task ConnectTelegramAsync()
    {
        if (_busy || _testingWebhook || _pairingTelegram is not null) return;
        using var session = new CancellationTokenSource();
        _pairingTelegram = session;
        UpdateTelegramConnection();
        _telegramConnection.Text = "Validating bot token…";
        _webhookTestStatus.Text = "";
        try
        {
            var draft = ReadWebhookDraft();
            async Task Ready(TelegramPairingPrompt prompt)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_webhookClosed || _pairingTelegram != session || session.IsCancellationRequested) return;
                    _telegramDeepLink = prompt.DeepLink;
                    _telegramConnection.Text = "Bot: @" + prompt.BotUsername + "\nPress Start in Telegram. Waiting for your private chat (expires in 5 minutes)…";
                    UpdateTelegramConnection();
                    OpenTelegramConversation();
                });
            }
            TelegramPairingResult result;
            if (_pairTelegram is { } pair) result = await pair(draft, Ready, session.Token);
            else
            {
                await using var client = new TelegramPairingClient();
                result = await client.PairAsync(draft, Ready, session.Token);
            }
            if (_webhookClosed || _pairingTelegram != session || session.IsCancellationRequested) return;
            _settingTelegramFields = true;
            _telegramChat.Text = result.ChatId;
            _settingTelegramFields = false;
            _webhookDraft = _webhookDraft with { TelegramBotUsername = result.BotUsername };
            _telegramDraftChanged = true;
            if (_webhookFormat.SelectedIndex == (int)WebhookFormat.Telegram) _webhookEnabled.IsChecked = true;
            _webhookTestStatus.Text = "Telegram confirmed the connection. Click Apply or OK to save and enable Telegram notifications.";
        }
        catch (OperationCanceledException)
        {
            if (_pairingTelegram == session) _webhookTestStatus.Text = "Pairing canceled or expired. Click Connect Telegram to try again.";
        }
        catch (TelegramPairingException error)
        {
            if (_pairingTelegram == session) _webhookTestStatus.Text = error.Message;
        }
        catch (InvalidDataException error)
        {
            if (_pairingTelegram == session) _webhookTestStatus.Text = error.Message;
        }
        catch
        {
            if (_pairingTelegram == session) _webhookTestStatus.Text = "Could not connect Telegram. Check your token and connection, or use Advanced manual configuration.";
        }
        finally
        {
            if (_pairingTelegram == session)
            {
                _pairingTelegram = null;
                _telegramDeepLink = null;
                if (!_webhookClosed) UpdateTelegramConnection();
            }
        }
    }

    private WebhookAlertConfiguration ReadWebhookDraft() => _webhookDraft with
    {
        Enabled = _webhookEnabled.IsChecked == true,
        Format = (WebhookFormat)_webhookFormat.SelectedIndex,
        Url = _webhookUrl.Text?.Trim() ?? "",
        UrlSecretId = _replacingWebhookUrl && !string.IsNullOrWhiteSpace(_webhookUrl.Text) ? "" : _webhookDraft.UrlSecretId,
        BearerTokenEnvironmentVariable = _webhookBearer.Text?.Trim() ?? "",
        TelegramBotToken = _telegramToken.Text?.Trim() ?? "",
        TelegramBotTokenSecretId = _webhookDraft.TelegramBotTokenSecretId,
        TelegramChatId = _telegramChat.Text?.Trim() ?? ""
    };

    private void WebhookSettingsSaved()
    {
        _webhookDraft = _savedWebhook?.Invoke() ?? ReadWebhookDraft();
        _replacingTelegramToken = _replacingWebhookUrl = false;
        // Do not retrieve saved credentials. Only opaque secure-store references enter the editor.
        _settingTelegramFields = true;
        if (_webhookDraft.TelegramBotTokenSecretId.Length > 0) _telegramToken.Text = "";
        _settingTelegramFields = false;
        _telegramDraftChanged = false;
        if (_webhookDraft.UrlSecretId.Length > 0) _webhookUrl.Text = "";
        UpdateSavedWebhookFields();
        UpdateTelegramConnection();
    }

    private void UpdateSavedWebhookFields()
    {
        var storedToken = _webhookDraft.TelegramBotTokenSecretId.Length > 0 && !_replacingTelegramToken;
        _telegramToken.IsEnabled = !storedToken;
        _telegramToken.Watermark = storedToken ? "●●●●●●●● — stored securely" : "Paste bot token";
        _replaceTelegramToken.IsVisible = storedToken;
        _replaceTelegramToken.Content = "Change Bot Token…";
        var storedUrl = _webhookDraft.UrlSecretId.Length > 0;
        _webhookUrl.IsEnabled = !storedUrl;
        _webhookUrl.Watermark = storedUrl ? "●●●●●●●● — stored securely" : "HTTP(S) webhook URL";
    }
}
