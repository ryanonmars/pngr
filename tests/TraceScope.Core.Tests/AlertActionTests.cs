using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class AlertActionTests
{
    private static MonitoringEvent Event(string kind = "Alert", string text = "Measured latency is high") =>
        new(Guid.NewGuid(), Guid.Parse("b9b158a9-9459-4f58-b42c-04a3f4e48ec6"), DateTimeOffset.UtcNow, kind, text, "rule-a");
    private static async Task WaitUntil(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(20, timeout.Token); }
    private sealed class Notifier : IDesktopNotifier
    {
        public int Sent; public bool Disposed;
        public Task<NotificationResult> RequestPermissionAsync(CancellationToken ct = default) => Task.FromResult(new NotificationResult(true, "test"));
        public Task<NotificationResult> SendAsync(string title, string message, CancellationToken ct = default) { Interlocked.Increment(ref Sent); return Task.FromResult(new NotificationResult(true, "accepted")); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    [Fact] public async Task AllActionsDefaultDisabledAndNoPermissionIsRequestedImplicitly()
    {
        var notifier = new Notifier(); await using var dispatcher = new AlertActionDispatcher(notifier: notifier);
        Assert.False(dispatcher.TryEnqueue(Event())); Assert.Equal(0, notifier.Sent);
        Assert.False((await dispatcher.SendTestNotificationAsync()).Success);
        Assert.True((await dispatcher.RequestNotificationPermissionAsync()).Success);
    }
    [Fact] public async Task NativeNotificationsAreGloballySpacedAndRecoveryCanBeDisabled()
    {
        var notifier = new Notifier(); var config = new AlertActionConfiguration { NativeNotificationsEnabled = true, IncludeRecovery = false };
        await using var dispatcher = new AlertActionDispatcher(config, notifier);
        Assert.True(dispatcher.Enqueue(Event())); Assert.True(dispatcher.Enqueue(Event("RouteChange")));
        Assert.False(dispatcher.Enqueue(Event("Recovery"))); Assert.False(dispatcher.Enqueue(Event("Acknowledgment")));
        await WaitUntil(() => notifier.Sent == 1); await Task.Delay(100); Assert.Equal(1, notifier.Sent);
    }
    [Fact] public void InvalidActionsAreRejectedAndSerializedSettingsContainOnlySecretReferences()
    {
        Assert.Throws<InvalidDataException>(() => new AlertActionConfiguration { Webhook = new() { Enabled = true, Url = "file:///secret" } }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertActionConfiguration { Webhook = new() { Enabled = true, Url = "http://user:pass@localhost" } }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertActionConfiguration { Script = new() { Enabled = true, Executable = "relative" } }.Validate());
        Assert.Throws<InvalidDataException>(() => new AlertActionConfiguration { ActionTimeoutSeconds = 0 }.Validate());
        var config = new AlertActionConfiguration { Smtp = new() { PasswordEnvironmentVariable = "TRACESCOPE_SMTP_PASSWORD" } };
        config.Validate(); var json = JsonSerializer.Serialize(config);
        Assert.Contains("PasswordEnvironmentVariable", json); Assert.DoesNotContain("Password\":", json);
    }
    [Fact] public async Task RealLocalWebhookDeliversJsonWithoutShellOrRedirects()
    {
        using var listener = new HttpListener();
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var receive = Task.Run(async () => { var ctx = await listener.GetContextAsync(); using var reader = new StreamReader(ctx.Request.InputStream); var body = await reader.ReadToEndAsync(); ctx.Response.StatusCode = 204; ctx.Response.Close(); return (ctx.Request.HttpMethod, body); });
        await using var dispatcher = new AlertActionDispatcher(new() { Webhook = new() { Enabled = true, Url = $"http://127.0.0.1:{port}/" } }, new Notifier());
        var completed = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => completed.TrySetResult(result);
        var e = Event(text: "Real measured event <&> $no_interpolation"); Assert.True(dispatcher.TryEnqueue(e));
        var request = await receive.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("POST", request.HttpMethod);
        Assert.Equal(e.Message, JsonSerializer.Deserialize<MonitoringEvent>(request.body)!.Message);
        Assert.True((await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }
    [Fact] public async Task RealLocalSmtpDeliversMail()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var receive = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(); using var stream = socket.GetStream();
            using var reader = new StreamReader(stream); using var writer = new StreamWriter(stream) { AutoFlush = true, NewLine = "\r\n" };
            await writer.WriteLineAsync("220 localhost TraceScope test"); var body = new System.Text.StringBuilder(); bool data = false;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (data) { if (line == ".") { data = false; await writer.WriteLineAsync("250 queued"); } else body.AppendLine(line); }
                else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase)) { data = true; await writer.WriteLineAsync("354 end with dot"); }
                else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase)) { await writer.WriteLineAsync("221 goodbye"); break; }
                else await writer.WriteLineAsync("250 localhost");
            }
            return body.ToString();
        });
        var dispatcher = new AlertActionDispatcher(new() { Smtp = new() { Enabled = true, Host = "127.0.0.1", Port = port, UseTls = false, From = "test@localhost", To = "alerts@localhost" } }, new Notifier());
        var completed = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.ActionCompleted += result => completed.TrySetResult(result);
        Assert.True(dispatcher.TryEnqueue(Event())); Assert.True((await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        await dispatcher.DisposeAsync(); var body = await receive.WaitAsync(TimeSpan.FromSeconds(5)); listener.Stop();
        Assert.Contains("Subject:", body); Assert.Contains("Measured latency is high", body);
    }
    [Fact] public async Task ScriptReceivesEventThroughEnvironmentAndNeverInterpolatesMessageIntoCommand()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "tracescope-actions-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var dispatcher = new AlertActionDispatcher(new() { Script = new() { Enabled = true, Executable = "/bin/sh", Arguments = ["-c", "printf '%s' \"$TRACESCOPE_EVENT_MESSAGE\" > \"$1\"", "tracescope-test", path] } }, new Notifier());
            var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously); dispatcher.ActionCompleted += r => done.TrySetResult(r);
            var message = "';$(touch /should_never_exist)\nActual alert"; Assert.True(dispatcher.TryEnqueue(Event(text: message)));
            Assert.True((await done.Task.WaitAsync(TimeSpan.FromSeconds(5))).Success); Assert.Equal(message, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task ExitCancelsAndWaitsForRunningActionProcess()
    {
        if (OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Path.GetTempPath(), "tracescope-action-pid-" + Guid.NewGuid().ToString("N"));
        var notifier = new Notifier();
        var dispatcher = new AlertActionDispatcher(new() { Script = new() { Enabled = true, Executable = "/bin/sh", Arguments = ["-c", "echo $$ > \"$1\"; exec sleep 30", "tracescope-test", path] } }, notifier);
        try
        {
            Assert.True(dispatcher.TryEnqueue(Event())); await WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0);
            var pid = int.Parse((await File.ReadAllTextAsync(path)).Trim()); await dispatcher.DisposeAsync();
            Assert.True(notifier.Disposed); Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); Assert.False(dispatcher.TryEnqueue(Event()));
        }
        finally { await dispatcher.DisposeAsync(); File.Delete(path); }
    }

    [Fact] public async Task ActionCooldownIsPerConditionAndAllowsRecoveryAfterAnAlert()
    {
        if (OperatingSystem.IsWindows()) return;
        var output = Path.Combine(Path.GetTempPath(), "tracescope-action-cooldown-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var dispatcher = new AlertActionDispatcher(new() { Script = new() { Enabled = true, Executable = "/bin/sh", Arguments = ["-c", "printf '%s\\n' \"$TRACESCOPE_EVENT_KIND\" >> \"$1\"", "tracescope-test", output] } }, new Notifier());
            var completed = 0; dispatcher.ActionCompleted += _ => Interlocked.Increment(ref completed);
            Assert.True(dispatcher.Enqueue(Event())); Assert.True(dispatcher.Enqueue(Event())); Assert.True(dispatcher.Enqueue(Event("Recovery")));
            await WaitUntil(() => completed == 2); await Task.Delay(100);
            Assert.Equal(new[] { "Alert", "Recovery" }, await File.ReadAllLinesAsync(output));
        }
        finally { File.Delete(output); }
    }

    private sealed class BlockingNotifier : IDesktopNotifier
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<NotificationResult> RequestPermissionAsync(CancellationToken ct = default) => Task.FromResult(new NotificationResult(true, "test"));
        public async Task<NotificationResult> SendAsync(string title, string message, CancellationToken ct = default)
        { Started.TrySetResult(); await Release.Task.WaitAsync(ct); return new(true, "accepted"); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    [Fact] public async Task QueueIsBoundedAndSettingsDisableStopsPendingActions()
    {
        var notifier = new BlockingNotifier();
        await using var dispatcher = new AlertActionDispatcher(new() { NativeNotificationsEnabled = true, MinimumNotificationSpacingSeconds = 0 }, notifier);
        Assert.True(dispatcher.Enqueue(Event())); await notifier.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var accepted = 0;
        for (var i = 0; i < 1000; i++) if (dispatcher.Enqueue(Event())) accepted++;
        Assert.Equal(64, accepted);
        dispatcher.Configure(new()); notifier.Release.TrySetResult(); await Task.Delay(100);
        Assert.False(dispatcher.Enqueue(Event()));
    }
    [Fact] public async Task ScriptTimeoutIsReportedAndWorkerContinues()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var dispatcher = new AlertActionDispatcher(new() { ActionTimeoutSeconds = 1, Script = new() { Enabled = true, Executable = "/bin/sleep", Arguments = ["30"] } }, new Notifier());
        var done = new TaskCompletionSource<AlertActionResult>(TaskCreationOptions.RunContinuationsAsynchronously); dispatcher.ActionCompleted += r => done.TrySetResult(r);
        Assert.True(dispatcher.TryEnqueue(Event())); var result = await done.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(result.Success); Assert.Equal("Action timed out", result.Message);
    }
}
