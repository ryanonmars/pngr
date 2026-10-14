using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using TraceScope.Core;

namespace TraceScope.App.Controls;

public sealed record WindowDisplay(PixelRect WorkingArea, double Scaling, bool IsPrimary);

public sealed record WindowPlacement(double Width, double Height, int X, int Y,
    double MinWidth, double MinHeight, PixelRect WorkingArea, double Scaling);

/// <summary>Restores logical window dimensions within current physical display bounds.</summary>
public static class WindowPlacementCalculator
{
    private const double TitleBarReserve = 32;

    public static WindowPlacement Calculate(MainWindowLayout? saved, IReadOnlyList<WindowDisplay> displays)
    {
        var usable = displays.Where(d => d.WorkingArea.Width > 0 && d.WorkingArea.Height > 0).ToArray();
        if (usable.Length == 0)
            usable = [new WindowDisplay(new PixelRect(0, 0, 1280, 900), 1, true)];

        var primary = usable.FirstOrDefault(d => d.IsPrimary) ?? usable[0];
        var matched = saved?.X is { } x && saved.Y is { } y
            ? usable.FirstOrDefault(d => Contains(d.WorkingArea, x, y))
            : null;
        var display = matched ?? primary;
        var scale = double.IsFinite(display.Scaling) && display.Scaling > 0 ? display.Scaling : 1;
        var area = display.WorkingArea;
        var availableWidth = area.Width / scale;
        var availableHeight = Math.Max(1, area.Height / scale - TitleBarReserve);
        var minWidth = Math.Min(980, availableWidth);
        var minHeight = Math.Min(640, availableHeight);
        var width = Math.Clamp(ValidSize(saved?.Width, 1200), minWidth, availableWidth);
        var height = Math.Clamp(ValidSize(saved?.Height, 800), minHeight, availableHeight);
        var pixelWidth = Math.Min(area.Width, (int)Math.Ceiling(width * scale));
        var pixelHeight = Math.Min(area.Height, (int)Math.Ceiling((height + TitleBarReserve) * scale));
        var desiredX = matched is not null ? saved!.X!.Value : area.X + (area.Width - pixelWidth) / 2;
        var desiredY = matched is not null ? saved!.Y!.Value : area.Y + (area.Height - pixelHeight) / 2;
        var placedX = Math.Clamp(desiredX, area.X, area.Right - pixelWidth);
        var placedY = Math.Clamp(desiredY, area.Y, area.Bottom - pixelHeight);
        return new WindowPlacement(width, height, placedX, placedY, minWidth, minHeight, area, scale);
    }

    private static double ValidSize(double? value, double fallback) =>
        value is { } size && double.IsFinite(size) && size > 0 ? size : fallback;

    private static bool Contains(PixelRect area, int x, int y) =>
        x >= area.X && x < area.Right && y >= area.Y && y < area.Bottom;
}
