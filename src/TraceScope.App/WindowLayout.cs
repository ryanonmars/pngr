using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TraceScope.App.Controls;
using TraceScope.Core;

namespace TraceScope.App;

public partial class MainWindow
{
    private MainWindowLayout? _normalWindowLayout;
    private bool _restoringWindowLayout;
    private bool _windowLayoutReady;
    private bool _windowWasMaximized;

    private void InitializeMainWindowLayout()
    {
        RestoreMainWindowLayout(null);
        PositionChanged += (_, _) => RememberMainWindowLayout();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ClientSizeProperty || e.Property == WindowStateProperty)
                RememberMainWindowLayout();
        };
        var screens = Screens;
        EventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing || !_windowLayoutReady || !IsVisible) return;
            RestoreMainWindowLayout(CaptureMainWindowLayout());
            ScheduleWorkspaceSave();
        });
        screens.Changed += changed;
        Closed += (_, _) => screens.Changed -= changed;
    }

    private void RestoreMainWindowLayout(MainWindowLayout? saved)
    {
        _restoringWindowLayout = true;
        try
        {
            var displays = Screens.All.Select(s => new WindowDisplay(s.WorkingArea, s.Scaling, s.IsPrimary)).ToArray();
            var placement = WindowPlacementCalculator.Calculate(saved, displays);
            MinWidth = placement.MinWidth; MinHeight = placement.MinHeight;
            WindowState = WindowState.Normal;
            Width = placement.Width; Height = placement.Height;
            Position = new PixelPoint(placement.X, placement.Y);
            _normalWindowLayout = new MainWindowLayout
            {
                Width = placement.Width, Height = placement.Height, X = placement.X, Y = placement.Y,
                Scaling = placement.Scaling
            };
            _windowWasMaximized = saved?.Maximized == true;
            if (_windowWasMaximized) WindowState = WindowState.Maximized;
        }
        finally { _restoringWindowLayout = false; }
    }

    private void RememberMainWindowLayout(bool schedule = true)
    {
        if (_restoringWindowLayout || _closing) return;
        if (WindowState == WindowState.Normal)
        {
            _windowWasMaximized = false;
            var width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            var height = ClientSize.Height > 0 ? ClientSize.Height : Height;
            if (double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0)
                _normalWindowLayout = new MainWindowLayout
                { Width = width, Height = height, X = Position.X, Y = Position.Y, Scaling = RenderScaling };
        }
        else if (WindowState == WindowState.Maximized) _windowWasMaximized = true;
        if (_windowLayoutReady && schedule) ScheduleWorkspaceSave();
    }

    private MainWindowLayout CaptureMainWindowLayout()
    {
        RememberMainWindowLayout(false);
        return (_normalWindowLayout ?? new MainWindowLayout()) with { Maximized = _windowWasMaximized };
    }
}
