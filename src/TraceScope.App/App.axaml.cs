using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Input;

namespace TraceScope.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS())
        {
            MacNativeMenu.Install();

        }
        // One menu model: native macOS menu bar, Windows application menu bar.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            var main = window as MainWindow;
            var menu = CreateWindowMenuForPlatform(OperatingSystem.IsWindows(), main is null ? null : main.ExecuteAppMenuCommand);
            NativeMenu.SetMenu(window, menu);
            main?.RegisterApplicationMenu(menu);
        });
    }

    internal NativeMenu CreateWindowMenu() => CreateWindowMenuForPlatform(OperatingSystem.IsWindows());

    internal NativeMenu CreateWindowMenuForPlatform(bool windows, Action<string>? execute = null)
    {
        var menu = new NativeMenu();
        AddSubmenu("File", windows
            ? ["Open History…", "Export CSV…", "Workspace…", "Check for Updates…"]
            : ["Open History…", "Export CSV…", "Workspace…"]);
        AddSubmenu("Window", "All Targets", "Summary");
        AddSubmenu("Tools", windows
            ? ["Settings…", "Alerts…", "Manage Groups…", "Network…", "Local Web / API…"]
            : ["Alerts…", "Manage Groups…", "Network…", "Local Web / API…"]);
        var tools = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == "Tools").Menu!;
        var profiles = new NativeMenu();
        foreach (var command in new[] { "Load Profile…", "Save Current Configuration…", "Manage/Delete Profiles…" })
        {
            profiles.Add(Command(command));
        }
        tools.Add(new NativeMenuItem("Profiles") { Menu = profiles });
        return menu;

        NativeMenuItem Command(string header)
        {
            var item = new NativeMenuItem(header);
            if (windows && header == "Settings…") item.Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control);
            if (execute is null) item.Click += AppMenuCommand;
            else item.Click += (_, _) => execute(header);
            return item;
        }

        void AddSubmenu(string header, params string[] commands)
        {
            var submenu = new NativeMenu();
            foreach (var command in commands)
            {
                submenu.Add(Command(command));
            }
            menu.Add(new NativeMenuItem(header) { Menu = submenu });
        }
    }
    private void AppMenuCommand(object? sender, EventArgs e)
    {
        if (sender is not NativeMenuItem item || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow window })
            return;
        if (item.Header?.ToString() == "Settings…") window.OpenSettingsFromMenu();
        else window.ExecuteAppMenuCommand(item.Header?.ToString() ?? string.Empty);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow(desktop.Args ?? []);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
