using System.Globalization;
using Avalonia.Controls;
using Avalonia;

namespace TraceScope.App.Controls;

/// <summary>Plain numeric input. Invalid text stays editable and is rejected on Apply.</summary>
public sealed class SettingsNumberField : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);
    public decimal Minimum { get; init; }
    public decimal Maximum { get; init; }
    public bool WholeNumber { get; init; } = true;
    public string Label { get; set; } = "Value";
    private string? _doubleText;
    private double _doubleValue;
    public event EventHandler? ValueChanged;

    public SettingsNumberField()
    {
        Width = 132;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;

    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) { Classes.Remove("invalid"); ValueChanged?.Invoke(this, EventArgs.Empty); }
    }

    public decimal? Value
    {
        get => decimal.TryParse(Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ? value : null;
        set => Text = value?.ToString(CultureInfo.CurrentCulture);
    }

    public decimal Read()
    {
        if (Value is not { } value || value < Minimum || value > Maximum || WholeNumber && decimal.Truncate(value) != value)
        {
            Classes.Add("invalid");
            throw new InvalidDataException($"{Label}: enter {(WholeNumber ? "a whole number" : "a number")} from {Minimum} to {Maximum}.");
        }
        return value;
    }

    public void SetDouble(double value)
    {
        _doubleValue = value;
        Text = _doubleText = value.ToString("R", CultureInfo.CurrentCulture);
    }

    public double ReadDouble()
    {
        // Preserve the exact untouched value; workspace validation still applies.
        if (_doubleText is not null && Text == _doubleText) return _doubleValue;
        Read();
        return double.Parse(Text!, CultureInfo.CurrentCulture);
    }

    public void SetDuration(TimeSpan value) => Value = (decimal)value.Ticks / TimeSpan.TicksPerSecond;
    public TimeSpan ReadDuration()
    {
        var ticks = Read() * TimeSpan.TicksPerSecond;
        if (ticks != decimal.Truncate(ticks))
        {
            Classes.Add("invalid");
            throw new InvalidDataException($"{Label}: use no more than seven decimal places in seconds.");
        }
        return TimeSpan.FromTicks((long)ticks);
    }
}
