using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    // Explicit developer diagnostic: exercises the real settings controls/store/API.
    // The token exists only in memory and the masked editor; reports contain no IDs or secrets.
    private async Task VerifyTelegramReplacementApplyAsync(string reportPath)
    {
        var phase = "configuration";
        var success = false;
        var ticks = 0;
        var applyMilliseconds = 0L;
        var destinationPreserved = false;
        var botPreserved = false;
        var discordPreserved = false;
        var referenceReplaced = false;
        var fieldCleared = false;
        TextBox? editor = null;
        var heartbeat = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Normal, (_, _) => ticks++);
        heartbeat.Start();
        try
        {
            var before = _configuration.Actions.Webhook;
            if (!before.Enabled || before.Format != WebhookFormat.Telegram ||
                string.IsNullOrWhiteSpace(before.TelegramBotTokenSecretId) || string.IsNullOrWhiteSpace(before.TelegramChatId)) return;
            phase = "credential-read";
            var token = await Task.Run(() => WebhookSecretStore.Default.Retrieve(before.TelegramBotTokenSecretId));
            phase = "settings-open";
            OpenSettings(null, new RoutedEventArgs());
            var window = _settingsWindow ?? throw new InvalidOperationException();
            window.SelectTelegramSettings();
            await Task.Delay(100);
            T Find<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
            void Click(string name)
            {
                var button = Find<Button>(name);
                if (!button.IsEnabled) throw new InvalidOperationException();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Click("SettingsReplaceTelegramToken");
            editor = Find<TextBox>("SettingsTelegramBotToken");
            if (editor.PasswordChar == default) throw new InvalidOperationException();
            editor.Text = token;
            token = "";
            phase = "test-delivery";
            Click("SettingsTestWebhook");
            var testStatus = Find<TextBlock>("SettingsWebhookTestStatus");
            await WaitFor(() => Find<Button>("SettingsTestWebhook").IsEnabled, TimeSpan.FromSeconds(20));
            if (testStatus.Text != "Telegram test notification sent successfully.") return;
            phase = "apply";
            var clock = Stopwatch.StartNew();
            var ticksBeforeApply = ticks;
            Click("SettingsApply");
            await WaitFor(() => Find<Button>("SettingsApply").IsEnabled, TimeSpan.FromSeconds(10));
            applyMilliseconds = clock.ElapsedMilliseconds;
            ticks -= ticksBeforeApply;
            if (Find<TextBlock>("SettingsStatus").Text != "Settings saved. Changes apply immediately; monitoring continues.") return;
            var after = _configuration.Actions.Webhook;
            destinationPreserved = before.TelegramChatId == after.TelegramChatId;
            botPreserved = before.TelegramBotUsername == after.TelegramBotUsername;
            discordPreserved = before.UrlSecretId == after.UrlSecretId;
            referenceReplaced = before.TelegramBotTokenSecretId != after.TelegramBotTokenSecretId && after.TelegramBotTokenSecretId.Length > 0;
            fieldCleared = string.IsNullOrEmpty(editor.Text);
            success = destinationPreserved && botPreserved && discordPreserved && referenceReplaced && fieldCleared;
            phase = success ? "complete" : "verification";
        }
        catch { /* Never serialize credential-store or HTTP exception text. */ }
        finally
        {
            heartbeat.Stop();
            if (editor is not null) editor.Text = "";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                Success = success, Phase = phase, ApplyMilliseconds = applyMilliseconds, UiHeartbeatTicks = ticks,
                DestinationPreserved = destinationPreserved, BotPreserved = botPreserved, DiscordReferencePreserved = discordPreserved,
                CredentialReferenceReplaced = referenceReplaced, TokenFieldCleared = fieldCleared
            }));
            _settingsWindow?.Close();
            Close();
        }

        static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.Elapsed > timeout) throw new TimeoutException();
                await Task.Delay(25);
            }
        }
    }
}
