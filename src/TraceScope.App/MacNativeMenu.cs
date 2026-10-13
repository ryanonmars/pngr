using System.Runtime.InteropServices;

namespace TraceScope.App;

internal static class MacNativeMenu
{
    private const string ObjectiveC = "/usr/lib/libobjc.A.dylib";
    private static readonly List<MenuCallback> Callbacks = [];
    private static IntPtr _menuBar;
    private static bool _installed;

    internal static void Install()
    {
        if (!OperatingSystem.IsMacOS() || _installed) return;
        _installed = true;

        // Avalonia 11.3.20 normally swaps NSApp's menu on both sides of every
        // focus change. macOS 27 can leave the rendered bar blank until hover.
        // All TraceScope windows have the same commands, so retain the first
        // complete bar and leave its native objects installed across focus changes.
        foreach (var name in new[] { "AvnWindow", "AvnPanel" })
        {
            var windowClass = GetClass(name);
            if (windowClass == IntPtr.Zero) continue;
            foreach (var nameOfMethod in new[] { "showAppMenuOnly", "showWindowMenuWithAppMenu" })
            {
                var selector = Selector(nameOfMethod);
                var method = GetInstanceMethod(windowClass, selector);
                if (method == IntPtr.Zero) continue;
                var original = Marshal.GetDelegateForFunctionPointer<MenuCallback>(GetImplementation(method));
                MenuCallback callback = (window, command) =>
                {
                    var application = SendPointer(GetClass("NSApplication"), Selector("sharedApplication"));
                    if (_menuBar != IntPtr.Zero)
                    {
                        // Usually this is already installed: do not mutate or
                        // reparent any menu items, and do not reinstall the bar.
                        if (SendPointer(application, Selector("mainMenu")) != _menuBar)
                            SendPointerArgument(application, Selector("setMainMenu:"), _menuBar);
                        return;
                    }

                    original(window, command);
                    var menu = SendPointer(application, Selector("mainMenu"));
                    if (menu != IntPtr.Zero && HasItem(menu, "File") && HasItem(menu, "Window") && HasItem(menu, "Tools"))
                        _menuBar = SendPointer(menu, Selector("retain"));
                };
                Callbacks.Add(callback); // Native method pointers must outlive every window.
                ReplaceMethod(windowClass, selector, Marshal.GetFunctionPointerForDelegate(callback), GetTypeEncoding(method));
            }
        }
    }

    private static bool HasItem(IntPtr menu, string title)
    {
        var nativeTitle = SendString(GetClass("NSString"), Selector("stringWithUTF8String:"), title);
        return SendArgument(menu, Selector("itemWithTitle:"), nativeTitle) != IntPtr.Zero;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MenuCallback(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);

    [DllImport(ObjectiveC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport(ObjectiveC, EntryPoint = "class_getInstanceMethod")]
    private static extern IntPtr GetInstanceMethod(IntPtr type, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "method_getImplementation")]
    private static extern IntPtr GetImplementation(IntPtr method);

    [DllImport(ObjectiveC, EntryPoint = "method_getTypeEncoding")]
    private static extern IntPtr GetTypeEncoding(IntPtr method);

    [DllImport(ObjectiveC, EntryPoint = "class_replaceMethod")]
    private static extern IntPtr ReplaceMethod(IntPtr type, IntPtr selector, IntPtr implementation, IntPtr encoding);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern void SendPointerArgument(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendArgument(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(ObjectiveC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, string argument);
}
