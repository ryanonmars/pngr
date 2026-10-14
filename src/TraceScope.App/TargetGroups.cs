using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace TraceScope.App;

public partial class MainWindow
{
    private async void OpenTargetGroup(object? sender, RoutedEventArgs e)
    {
        var target = (sender as Control)?.DataContext as TargetItem ?? _selected;
        if (target?.Session is null || target.IsHistorical) return;
        var dialog = ToolWindow("Assign group", 400, 190);
        var input = new TextBox { Name = "TargetGroupName", Text = target.Group, MaxLength = 80, MinHeight = 32 };
        var cancel = new Button { Content = "Cancel" };
        var save = new Button { Name = "SaveTargetGroup", Content = "Save" };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            if (!_targets.Contains(target)) { dialog.Close(); return; }
            target.Group = string.IsNullOrWhiteSpace(input.Text) ? "Default" : input.Text.Trim();
            target.Session.Group = target.Group;
            target.Refresh();
            AssignAlerts(target);
            if (ReferenceEquals(_selected, target))
            {
                _settingOptions = true;
                try { GroupInput.Text = target.Group; }
                finally { _settingOptions = false; }
            }
            RefreshAllTargets();
            ScheduleWorkspaceSave();
            dialog.Close();
        };
        dialog.Content = ToolPanel(new TextBlock { Text = $"Group for {target.Target}" }, input,
            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8, Children = { cancel, save } });
        await dialog.ShowDialog(this);
    }
}
