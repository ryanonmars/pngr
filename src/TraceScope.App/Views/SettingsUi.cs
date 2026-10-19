using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using TraceScope.App.Controls;

namespace TraceScope.App.Views;

internal static class SettingsUi
{
    public static readonly IBrush Muted = Brush.Parse("#A8ADB7");
    public static void Apply(Window window)
    {
        window.Styles.Add(new Style(s => s.OfType<TextBox>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 29d), new Setter(TemplatedControl.FontSizeProperty, 12d) } });
        window.Styles.Add(new Style(s => s.OfType<ComboBox>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 29d), new Setter(TemplatedControl.FontSizeProperty, 12d) } });
        window.Styles.Add(new Style(s => s.OfType<Button>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 28d), new Setter(TemplatedControl.FontSizeProperty, 11d),
            new Setter(TemplatedControl.PaddingProperty, new Thickness(10, 3)) } });
        window.Styles.Add(new Style(s => s.OfType<CheckBox>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 26d), new Setter(TemplatedControl.FontSizeProperty, 11d) } });
        window.Styles.Add(new Style(s => s.OfType<RadioButton>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 26d), new Setter(TemplatedControl.FontSizeProperty, 11d) } });
        window.Styles.Add(new Style(s => s.OfType<Expander>()) { Setters = {
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
            new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)) } });
        window.Styles.Add(new Style(s => s.OfType<Expander>().Template().OfType<ToggleButton>()) { Setters = {
            new Setter(TemplatedControl.MinHeightProperty, 30d), new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 3)),
            new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            new Setter(TemplatedControl.FontSizeProperty, 12d) } });
        window.Styles.Add(new Style(s => s.OfType<TextBox>().Class("invalid")) { Setters = {
            new Setter(TemplatedControl.BorderBrushProperty, Brush.Parse("#D49A81")) } });
    }
    public static TextBlock Heading(string text) => new() { Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 2) };
    public static TextBlock Note(string text) => new() { Text = text, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    public static Border Divider() => new() { Height = 1, Background = Brush.Parse("#35383F"), Margin = new Thickness(0, 4, 0, 3) };
    public static StackPanel Section(string title, params Control[] controls)
    {
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(Heading(title));
        foreach (var control in controls) section.Children.Add(control);
        return section;
    }
    public static StackPanel Page(params Control[] controls)
    {
        var page = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var control in controls) page.Children.Add(control);
        return page;
    }
    public static StackPanel Field(string label, Control input, string? unit = null)
    {
        AutomationProperties.SetName(input, unit is null ? label : $"{label} ({unit})");
        if (input is SettingsNumberField number) number.Label = label;
        Control content = input;
        if (unit is not null) content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { input, new TextBlock { Text = unit, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center } } };
        return new StackPanel { Spacing = 5, Children = { new TextBlock { Text = label, FontSize = 11, Foreground = Muted }, content } };
    }
    public static Grid Columns(params Control[] children)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", children.Select(_ => "*"))), ColumnSpacing = 12 };
        for (var i = 0; i < children.Length; i++) { Grid.SetColumn(children[i], i); grid.Children.Add(children[i]); }
        void Reflow()
        {
            grid.ColumnSpacing = children.Count(child => child.IsVisible) > 1 ? 12 : 0;
            for (var i = 0; i < children.Length; i++) grid.ColumnDefinitions[i].Width = children[i].IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        foreach (var child in children) child.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Reflow(); };
        Reflow(); return grid;
    }
    public static WrapPanel Actions(params Control[] children)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    public static SelectableTextBlock Location(string path) => new() { Text = path, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
}
