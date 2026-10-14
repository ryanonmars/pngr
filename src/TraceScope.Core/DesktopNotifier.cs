using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace TraceScope.Core;

public static class DesktopNotifier
{
    public static IDesktopNotifier Create() => OperatingSystem.IsMacOS() ? new MacDesktopNotifier() :
        OperatingSystem.IsWindows() ? new WindowsDesktopNotifier() : new UnsupportedNotifier();
    private sealed class UnsupportedNotifier : IDesktopNotifier
    {
        public Task<NotificationResult> RequestPermissionAsync(CancellationToken ct = default) => Task.FromResult(new NotificationResult(false, "Native notifications are supported on macOS and Windows."));
        public Task<NotificationResult> SendAsync(string title, string message, CancellationToken ct = default) => RequestPermissionAsync(ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class MacDesktopNotifier : IDesktopNotifier
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Permission(int request, StringBuilder error, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Send([MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string message, StringBuilder error, int capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Shutdown();
    private readonly SemaphoreSlim _serial = new(1);
    private nint _library;
    private Permission? _permission;
    private Send? _send;
    private Shutdown? _shutdown;
    private bool _disposed;
    private void Load()
    {
        if (_library != 0) return;
        var path = Path.Combine(AppContext.BaseDirectory, "libTraceScopeNotifications.dylib");
        if (!File.Exists(path)) throw new IOException("Native macOS notifications require the packaged pngr.app.");
        _library = NativeLibrary.Load(path);
        _permission = Marshal.GetDelegateForFunctionPointer<Permission>(NativeLibrary.GetExport(_library, "ts_notifications_permission"));
        _send = Marshal.GetDelegateForFunctionPointer<Send>(NativeLibrary.GetExport(_library, "ts_notifications_send"));
        _shutdown = Marshal.GetDelegateForFunctionPointer<Shutdown>(NativeLibrary.GetExport(_library, "ts_notifications_shutdown"));
    }
    public Task<NotificationResult> RequestPermissionAsync(CancellationToken ct = default) => InvokeAsync(true, "", "", ct);
    public Task<NotificationResult> SendAsync(string title, string message, CancellationToken ct = default) => InvokeAsync(false, title, message, ct);
    private async Task<NotificationResult> InvokeAsync(bool request, string title, string message, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (_disposed) return new(false, "Notifier has shut down.");
            return await Task.Run(() =>
            {
                try
                {
                    Load(); var error = new StringBuilder(1024);
                    var accepted = request ? _permission!(1, error, error.Capacity) : _send!(title, message, error, error.Capacity);
                    return new NotificationResult(accepted != 0, accepted != 0 ? "Accepted by macOS notification center." : error.ToString());
                }
                catch (Exception failure) { return new(false, failure.Message); }
            }, CancellationToken.None); // Native bounded waits must finish before disposing the notifier.
        }
        finally { _serial.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _shutdown?.Invoke(); // Interrupt native bounded waits before waiting for serialization.
        await _serial.WaitAsync();
        try { // Objective-C classes and callbacks stay resident until process exit; unloading is unsafe.
            _permission = null; _send = null; }
        finally { _serial.Release(); }
    }
}

// Native Shell_NotifyIcon notification-area balloons require no app registration, PowerShell,
// service, or executable. A private in-process message loop owns and removes the transient icon.
internal sealed class WindowsDesktopNotifier : IDesktopNotifier
{
    private readonly BlockingCollection<(string Title, string Body, TaskCompletionSource<NotificationResult> Result)> _requests = new(64);
    private Thread? _thread;
    private readonly object _gate = new();
    private bool _disposed;
    public Task<NotificationResult> RequestPermissionAsync(CancellationToken ct = default) =>
        Task.FromResult(new NotificationResult(!_disposed, "Windows controls notification visibility in system notification and focus settings."));
    public Task<NotificationResult> SendAsync(string title, string message, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new NotificationResult(false, "Notifier has shut down."));
            if (_thread is null) { _thread = new Thread(Loop) { IsBackground = true, Name = "pngr native notifications" }; if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.STA); _thread.Start(); }
            var result = new TaskCompletionSource<NotificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_requests.IsAddingCompleted || !_requests.TryAdd((title, message, result))) return Task.FromResult(new NotificationResult(false, "Notification queue is full."));
            return result.Task.WaitAsync(ct);
        }
    }
    private void Loop()
    {
        nint window = 0;
        var data = new NotifyIconData();
        bool added = false;
        try
        {
            window = CreateWindowExW(0, "STATIC", "pngr notifications", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            if (window == 0) throw new IOException("Windows notification window could not be created.");
            data = new NotifyIconData { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
                Flags = 2 | 4, Icon = LoadIconW(0, (nint)32516), Tooltip = "pngr", Info = "", InfoTitle = "" };
            added = Shell_NotifyIconW(0, ref data);
            if (!added) throw new IOException("Windows notification-area icon could not be created.");
            while (!_requests.IsCompleted)
            {
                while (PeekMessageW(out var message, 0, 0, 0, 1)) { TranslateMessage(ref message); DispatchMessageW(ref message); }
                if (!_requests.TryTake(out var item, 100)) continue;
                data.Flags = 0x10; data.InfoTitle = Truncate(item.Title, 63); data.Info = Truncate(item.Body, 255); data.InfoFlags = 1; data.TimeoutOrVersion = 10000;
                var accepted = Shell_NotifyIconW(1, ref data);
                item.Result.TrySetResult(new(accepted, accepted ? "Accepted by Windows notification area." : "Windows rejected the notification."));
            }
        }
        catch (Exception error)
        {
            _requests.CompleteAdding();
            while (_requests.TryTake(out var item)) item.Result.TrySetResult(new(false, error.Message));
        }
        finally
        {
            if (added) Shell_NotifyIconW(2, ref data);
            if (window != 0) DestroyWindow(window);
        }
    }
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
    public async ValueTask DisposeAsync()
    {
        Thread? thread;
        lock (_gate) { if (_disposed) return; _disposed = true; _requests.CompleteAdding(); thread = _thread; }
        if (thread is not null) await Task.Run(thread.Join);
        _requests.Dispose();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint LoadIconW(nint instance, nint name);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(ref NativeMessage message);
}
