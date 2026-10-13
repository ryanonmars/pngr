using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private void ConfigureActions()
    {
        if (_closing) return;
        if (_actions is null)
        {
            _actions = new AlertActionDispatcher(_configuration.Actions);
            _actions.ActionCompleted += result => Dispatcher.UIThread.Post(async () =>
            {
                if (_closing) return;
                var origin = _monitoringEvents.FirstOrDefault(e => e.Id == result.EventId);
                if (origin is null) return;
                var entry = new MonitoringEvent(Guid.NewGuid(), origin.SessionId, DateTimeOffset.UtcNow,
                    "Action", $"{result.Action}: {(result.Success ? "completed" : "failed")} — {result.Message}", origin.AlertRuleId);
                try
                {
                    await _engine.Storage.AppendEventAsync(entry, _applicationCancellation.Token);
                    _monitoringEvents.Insert(0, entry);
                    while (_monitoringEvents.Count > 500) _monitoringEvents.RemoveAt(_monitoringEvents.Count - 1);
                    if (!result.Success) StatusText.Text = entry.Message;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { StatusText.Text = "Could not log action: " + ex.Message; }
            });
        }
        else _actions.Configure(_configuration.Actions);
    }

    private async Task MaintainHistoryAsync()
    {
        try
        {
            var count = await Task.Run(() => _engine.Storage.RetainAsync(TimeSpan.FromDays(_configuration.RetentionDays), _applicationCancellation.Token));
            if (!_closing && count > 0) StatusText.Text = $"History retention removed {count:N0} old samples.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) StatusText.Text = "History maintenance failed: " + ex.Message; }
    }

    private async void OpenAlertActions(object? sender, RoutedEventArgs e)
    {
        var settings = _configuration.Actions;
        var dialog = ToolWindow("Notifications and alert actions", 720, 650);
        var native = new CheckBox { Name = "NativeNotifications", Content = "Enable native OS notifications", IsChecked = settings.NativeNotificationsEnabled };
        var recovery = new CheckBox { Content = "Send recovery notifications and actions", IsChecked = settings.IncludeRecovery };
        var notificationSpacing = new NumericUpDown { Minimum = 0, Maximum = 86400, Value = settings.MinimumNotificationSpacingSeconds, Width = 120 };
        var actionSpacing = new NumericUpDown { Minimum = 0, Maximum = 86400, Value = settings.MinimumActionSpacingSeconds, Width = 120 };
        var timeout = new NumericUpDown { Minimum = 1, Maximum = 120, Value = settings.ActionTimeoutSeconds, Width = 120 };
        var permission = new Button { Name = "NotificationPermission", Content = "Request OS notification permission" };
        var test = new Button { Name = "TestAlertActions", Content = "Test enabled actions" };
        var status = new TextBlock { Name = "ActionStatus", TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Name = "SaveAlertActions", Content = "Save actions" };
        var smtpEnabled = new CheckBox { Content = "Enable SMTP email", IsChecked = settings.Smtp.Enabled };
        var host = Input(settings.Smtp.Host, "SMTP server");
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = settings.Smtp.Port, Width = 120 };
        var tls = new CheckBox { Content = "Use STARTTLS", IsChecked = settings.Smtp.UseTls };
        var username = Input(settings.Smtp.Username, "Username (optional)");
        var passwordVariable = Input(settings.Smtp.PasswordEnvironmentVariable, "Password environment variable name");
        var from = Input(settings.Smtp.From, "Sender email"); var to = Input(settings.Smtp.To, "Recipient email");
        var webhookEnabled = new CheckBox { Content = "Enable webhook POST", IsChecked = settings.Webhook.Enabled };
        var url = Input(settings.Webhook.Url, "HTTP(S) webhook URL");
        var tokenVariable = Input(settings.Webhook.BearerTokenEnvironmentVariable, "Bearer-token environment variable name (optional)");
        var scriptEnabled = new CheckBox { Content = "Enable local executable action", IsChecked = settings.Script.Enabled };
        var executable = Input(settings.Script.Executable, "Absolute executable path");
        var arguments = Input(JsonSerializer.Serialize(settings.Script.Arguments), "JSON argument array, e.g. [\"arg\"]");
        var directory = Input(settings.Script.WorkingDirectory, "Working directory (optional)");

        AlertActionConfiguration ReadActions()
        {
            var configuration = new AlertActionConfiguration
            {
                NativeNotificationsEnabled = native.IsChecked == true, IncludeRecovery = recovery.IsChecked == true,
                MinimumNotificationSpacingSeconds = (int)(notificationSpacing.Value ?? 30), MinimumActionSpacingSeconds = (int)(actionSpacing.Value ?? 30),
                ActionTimeoutSeconds = (int)(timeout.Value ?? 15),
                Smtp = new() { Enabled = smtpEnabled.IsChecked == true, Host = host.Text?.Trim() ?? "", Port = (int)(port.Value ?? 587),
                    UseTls = tls.IsChecked == true, Username = username.Text ?? "", PasswordEnvironmentVariable = passwordVariable.Text?.Trim() ?? "",
                    From = from.Text?.Trim() ?? "", To = to.Text?.Trim() ?? "" },
                Webhook = new() { Enabled = webhookEnabled.IsChecked == true, Url = url.Text?.Trim() ?? "", BearerTokenEnvironmentVariable = tokenVariable.Text?.Trim() ?? "" },
                Script = new() { Enabled = scriptEnabled.IsChecked == true, Executable = executable.Text?.Trim() ?? "",
                    Arguments = JsonSerializer.Deserialize<string[]>(arguments.Text ?? "[]") ?? [], WorkingDirectory = directory.Text?.Trim() ?? "" }
            };
            configuration.Validate(); return configuration;
        }
        async Task<bool> SaveActions()
        {
            try
            {
                _configuration = _configuration with { Actions = ReadActions() }; ConfigureActions();
                await SaveWorkspaceNowAsync(); status.Text = "Actions saved. Disabled actions perform no work."; return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
        }
        save.Click += async (_, _) => await SaveActions();
        permission.Click += async (_, _) =>
        {
            permission.IsEnabled = false;
            try
            {
                ConfigureActions();
                var result = await _actions!.RequestNotificationPermissionAsync(_applicationCancellation.Token);
                status.Text = result.Message;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { permission.IsEnabled = true; }
        };
        test.Click += async (_, _) =>
        {
            if (!await SaveActions()) return;
            if (_selected?.Session is not { } session) { status.Text = "Select a live target to test configured actions."; return; }
            var item = new MonitoringEvent(Guid.NewGuid(), session.Id, DateTimeOffset.UtcNow, "Alert", "TraceScope: user-requested notification/action test.", "manual-test");
            try
            {
                await _engine.Storage.AppendEventAsync(item, _applicationCancellation.Token); _monitoringEvents.Insert(0, item);
                status.Text = _actions!.TryEnqueue(item) ? "Test queued. Results appear in the alert event log." : "No actions enabled or the action queue is full.";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        var general = ToolPanel(native, recovery, FormRow("Notification spacing (s)", notificationSpacing), FormRow("Action spacing (s)", actionSpacing),
            FormRow("Action timeout (s)", timeout), permission);
        var email = ToolPanel(smtpEnabled, FormRow("SMTP server", host), FormRow("Port", port), tls, FormRow("Username", username),
            FormRow("Password reference", passwordVariable), FormRow("From", from), FormRow("To", to));
        var webhook = ToolPanel(webhookEnabled, url, tokenVariable);
        var script = ToolPanel(scriptEnabled, new TextBlock { Text = "An enabled executable runs locally for matching alerts. Enable only programs you trust.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, executable, arguments, directory);
        var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Notifications", Content = general }, new TabItem { Header = "SMTP", Content = email },
            new TabItem { Header = "Webhook", Content = webhook }, new TabItem { Header = "Executable", Content = script } } };
        dialog.Content = ToolPanel(new TextBlock { Text = "All actions are optional. Secrets are referenced by environment variable name and are never written to exported configuration.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            tabs, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, test } }, status);
        await dialog.ShowDialog(this);
    }

    private static TextBox Input(string text, string watermark) => new() { Text = text, Watermark = watermark, MaxLength = 4096 };
}
