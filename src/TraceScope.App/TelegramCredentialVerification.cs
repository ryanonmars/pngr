using System.Runtime.InteropServices;
using System.Text.Json;
using TraceScope.Core;

namespace TraceScope.App;

/// <summary>Explicit, read-only saved-configuration diagnostic; sends one user-requested Telegram test.</summary>
public static class TelegramCredentialVerification
{
    public static async Task<int> RunNativeAsync(string workspacePath, TextWriter output)
    {
        var original = false;
        if (OperatingSystem.IsMacOS())
        {
            var status = SecKeychainGetUserInteractionAllowed(out original);
            if (status == 0) status = SecKeychainSetUserInteractionAllowed(false);
            if (status != 0) { output.WriteLine($"Telegram diagnostic could not set noninteractive credential access (OSStatus {status})."); return 1; }
        }
        try
        {
            // Direct deserialization deliberately avoids WorkspaceStore.Load, migration,
            // saving, deletion, background monitoring, and any Discord credential use.
            var workspace = JsonSerializer.Deserialize<WorkspaceConfiguration>(await File.ReadAllTextAsync(workspacePath), WorkspaceStore.JsonOptions);
            return await RunAsync(workspace?.Actions.Webhook, WebhookSecretStore.Create(), output);
        }
        catch { output.WriteLine("Telegram diagnostic could not read saved configuration. No credential details are displayed."); return 1; }
        finally { if (OperatingSystem.IsMacOS()) SecKeychainSetUserInteractionAllowed(original); }
    }

    public static async Task<int> RunAsync(WebhookAlertConfiguration? configuration, IWebhookSecretStore store,
        TextWriter output, HttpMessageHandler? httpHandler = null)
    {
        if (configuration is null || configuration.Format != WebhookFormat.Telegram || !configuration.Enabled ||
            configuration.TelegramBotToken.Length != 0 ||
            !Guid.TryParseExact(configuration.TelegramBotTokenSecretId, "N", out _) ||
            string.IsNullOrEmpty(configuration.TelegramChatId))
        {
            output.WriteLine("Telegram diagnostic requires enabled, saved Telegram settings with a secure credential reference and destination. No request was sent.");
            return 1;
        }
        try
        {
            await using var dispatcher = new AlertActionDispatcher(secretStore: store, httpHandler: httpHandler);
            var result = await dispatcher.SendTestWebhookAsync(configuration);
            output.WriteLine(result.Success ? "Telegram API confirmed test notification delivery." : result.Message);
            return result.Success ? 0 : 1;
        }
        catch { output.WriteLine("Telegram diagnostic failed. No credential details are displayed."); return 1; }
    }

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] out bool allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] bool allowed);
}
