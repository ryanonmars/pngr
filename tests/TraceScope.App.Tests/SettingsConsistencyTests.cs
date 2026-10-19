using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TraceScope.App.Controls;
using TraceScope.App.Views;
using TraceScope.Core;
using Xunit;

namespace TraceScope.App.Tests;

public sealed class SettingsConsistencyTests
{
    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("65536")]
    public void NumericTextRejectsInvalidAndFractionalIntegerValues(string text)
    {
        var input = new SettingsNumberField { Minimum = 1, Maximum = 65535, Label = "Port", Text = text };
        Assert.Throws<InvalidDataException>(() => input.Read());
        Assert.Contains("invalid", input.Classes);
        input.Text = "443";
        Assert.Equal(443, input.Read());
        Assert.DoesNotContain("invalid", input.Classes);
    }

    [AvaloniaFact]
    public async Task UntouchedSettingsPreserveExactTimingDoublesAndCredentialReferences()
    {
        var original = new WorkspaceConfiguration {
            DefaultProbeOptions = new() { Interval = TimeSpan.FromTicks(12345678), Timeout = TimeSpan.FromTicks(7654321), Protocol = ProbeProtocol.Udp },
            Display = new() { WarningLatencyMs = 59999.123456789, CriticalLatencyMs = 60000, FixedMaximumMs = 345.1234567890123 },
            Actions = new() { Webhook = new() { UrlSecretId = "11111111111111111111111111111111", TelegramBotTokenSecretId = "22222222222222222222222222222222", TelegramChatId = "-100123456789" } }
        };
        SettingsChange? applied = null;
        var window = Open(original, change => { applied = change; return Task.CompletedTask; });
        Click(Get<Button>(window, "SettingsApply")); await Until(() => applied is not null);
        Assert.Equal(original.DefaultProbeOptions, applied!.Configuration.DefaultProbeOptions);
        Assert.Equal(original.Display, applied.Configuration.Display);
        Assert.Equal(original.Actions, applied.Configuration.Actions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AutomaticAndFixedGraphScalingStageWithoutChangingStoredSemantics()
    {
        SettingsChange? applied = null;
        var window = Open(new(), change => { applied = change; return Task.CompletedTask; }); window.SelectPage("Display");
        Assert.True(Get<RadioButton>(window, "SettingsGraphAutomatic").IsChecked);
        Get<RadioButton>(window, "SettingsGraphFixed").IsChecked = true;
        var maximum = Get<SettingsNumberField>(window, "SettingsGraphMaximum");
        maximum.Value = 432.125m;
        Click(Get<Button>(window, "SettingsApply")); await Until(() => applied is not null);
        Assert.Equal(432.125, applied!.Configuration.Display.FixedMaximumMs);
        applied = null; Get<RadioButton>(window, "SettingsGraphAutomatic").IsChecked = true;
        Click(Get<Button>(window, "SettingsApply")); await Until(() => applied is not null);
        Assert.Equal(0, applied!.Configuration.Display.FixedMaximumMs);
        Get<RadioButton>(window, "SettingsGraphFixed").IsChecked = true;
        Assert.Equal(432.125m, maximum.Value);
        window.Close();
    }

    [AvaloniaFact]
    public void InvalidPortCannotBeDiscardedBySwitchingProtocol()
    {
        var window = Open(new(), _ => Task.CompletedTask); window.SelectPage("Monitoring");
        var protocol = Get<ComboBox>(window, "SettingsProtocol"); protocol.SelectedItem = ProbeProtocol.Tcp;
        var port = Get<SettingsNumberField>(window, "SettingsDestinationPort"); port.Text = "invalid";
        protocol.SelectedItem = ProbeProtocol.Udp;
        Assert.Equal(ProbeProtocol.Tcp, protocol.SelectedItem);
        Assert.Equal("invalid", port.Text);
        Assert.Contains("Port", Get<TextBlock>(window, "SettingsStatus").Text!, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(760)]
    [InlineData(830)]
    public void EveryPageUsesPlainNumericFieldsWithinContentBounds(int width)
    {
        var window = Open(new(), _ => Task.CompletedTask); window.Width = width; window.Height = 580;
        foreach (var page in new[] { "General", "Monitoring", "Display", "History", "Alerts & notifications", "Notification actions", "Web Server" })
        {
            window.SelectPage(page); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Empty(window.GetVisualDescendants().OfType<NumericUpDown>());
            foreach (var field in window.GetVisualDescendants().OfType<SettingsNumberField>().Where(f => f.IsEffectivelyVisible))
            {
                var origin = field.TranslatePoint(default, window)!.Value;
                Assert.True(field.Bounds.Width > 0);
                Assert.True(origin.X >= 175 && origin.X + field.Bounds.Width < width, field.Name);
                Assert.True(field.Focusable);
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingApplyLocksThePageAndFailedSaveRetainsEditableDraft()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = Open(new(), _ => { started.TrySetResult(); return finish.Task; });
        window.SelectPage("History");
        var retention = Get<SettingsNumberField>(window, "SettingsRetention"); retention.Value = 60;
        Click(Get<Button>(window, "SettingsApply")); await started.Task;
        Assert.False(retention.IsEffectivelyEnabled);
        finish.SetException(new IOException("Simulated save failure"));
        await Until(() => Get<TextBlock>(window, "SettingsStatus").Text?.Contains("not applied") == true);
        Assert.True(retention.IsEffectivelyEnabled);
        Assert.Equal(60m, retention.Value);
        window.Close();
    }

    private static SettingsWindow Open(WorkspaceConfiguration configuration, Func<SettingsChange, Task> apply)
    {
        var window = new SettingsWindow(configuration, null, null, [new("System route", null, null)], Path.GetTempPath(), apply,
            () => Task.FromResult(new NotificationResult(true, "Permission")), _ => { }, automaticAddress: _ => Task.FromResult<WebServerAddress?>(null));
        window.Show(); return window;
    }
    private static T Get<T>(Window window, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window.GetVisualDescendants().OfType<T>().Concat(window.GetLogicalDescendants().OfType<T>()).Distinct().Single(c => c.Name == name);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > end) throw new TimeoutException(); await Task.Delay(10); }
    }
}
