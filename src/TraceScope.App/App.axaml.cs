using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace TraceScope.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS())
        {
            MacNativeMenu.Install();
            // Export the initial complete menu; native presentation keeps it stable
            // when focus changes between the main and utility windows.
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            {
                NativeMenu.SetMenu(window, CreateWindowMenu());
            });
        }
    }

    private NativeMenu CreateWindowMenu()
    {
        var menu = new NativeMenu();
        AddSubmenu("File", "Open History…", "Export CSV…");
        AddSubmenu("Window", "All Targets", "Summary", "Network", "Web");
        AddSubmenu("Tools", "Alerts", "Workspace");
        return menu;

        void AddSubmenu(string header, params string[] commands)
        {
            var submenu = new NativeMenu();
            foreach (var command in commands)
            {
                var item = new NativeMenuItem(command);
                item.Click += AppMenuCommand;
                submenu.Add(item);
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
