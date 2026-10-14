using System.Reflection;
using Avalonia;
using Avalonia.Controls;

namespace TraceScope.App.Tests;

internal static class MenuTestSupport
{
    internal static NativeMenu WindowsMenu(MainWindow window)
    {
        Action<string> execute = header => typeof(MainWindow)
            .GetMethod("ExecuteAppMenuCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [header]);
        var menu = (NativeMenu)typeof(App).GetMethod("CreateWindowMenuForPlatform", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Application.Current, [true, execute])!;
        NativeMenu.SetMenu(window, menu);
        typeof(MainWindow).GetMethod("RegisterApplicationMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [menu]);
        return menu;
    }

    internal static NativeMenuItem Item(MainWindow window, string header)
        => Leaves(NativeMenu.GetMenu(window)!).Single(item => item.Header == header);

    internal static IEnumerable<NativeMenuItem> Leaves(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            if (item.Menu is null) yield return item;
            else foreach (var child in Leaves(item.Menu)) yield return child;
        }
    }

    internal static void Click(MainWindow window, string header)
    {
        if (!Leaves(NativeMenu.GetMenu(window)!).Any(item => item.Header == header)) WindowsMenu(window);
        var item = Item(window, header);
        typeof(NativeMenuItem).GetMethod("Avalonia.Controls.INativeMenuItemExporterEventsImplBridge.RaiseClicked",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
    }
}
