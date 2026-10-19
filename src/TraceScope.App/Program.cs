using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using System.Runtime.InteropServices;

namespace TraceScope.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--read-webhook-secret")
        {
            Environment.ExitCode = MacCredentialReader.RunChild(args[1], args[2]);
            return;
        }
        // Installer hooks must run before Avalonia or any monitoring/database initialization.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (args.Length == 2 && args[0] == "--verify-saved-telegram")
        {
            Environment.ExitCode = TelegramCredentialVerification.RunNativeAsync(args[1], Console.Out).GetAwaiter().GetResult();
            return;
        }
        if (args.Length == 3 && args[0] == "--verify-keychain-signature")
        {
            Environment.ExitCode = KeychainSignatureVerification.RunNative(args[1], args[2], Console.Out);
            return;
        }
        if (args.Contains("--verify-webhook-credentials", StringComparer.Ordinal))
        {
            Environment.ExitCode = WebhookCredentialVerification.Run(TraceScope.Core.WebhookSecretStore.Create(), Console.Out);
            return;
        }
        if (OperatingSystem.IsMacOS()) TraceScope.Core.WebhookSecretStore.RegisterMacCredentialReader(MacCredentialReader.Read);
        if (args.Length == 3 && args[0] == "--verify-keychain-signature-isolated")
        {
            Environment.ExitCode = KeychainSignatureVerification.Run(TraceScope.Core.WebhookSecretStore.Create(), args[1], args[2], Console.Out);
            return;
        }
        if (args.Length == 2 && args[0] == "--verify-saved-telegram-isolated")
        {
            Environment.ExitCode = TelegramCredentialVerification.RunNativeAsync(args[1], Console.Out).GetAwaiter().GetResult();
            return;
        }
        using var signal = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.MainWindow?.Close();
            });
        });
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
