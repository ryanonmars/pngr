namespace TraceScope.Core;

/// <summary>Organization only. Legacy names and alert matching remain ordinal and exact.</summary>
public static class TargetGroupCatalog
{
    public const string Default = "Default";

    public static string[] List(WorkspaceConfiguration workspace) => new[] { Default }
        .Concat(workspace.Groups).Concat(workspace.Targets.Select(t => t.Group))
        .Concat(workspace.Alerts.Select(r => r.Group).OfType<string>())
        .Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal)
        .OrderBy(g => g == Default ? 0 : 1).ThenBy(g => g, StringComparer.Ordinal).ToArray();

    public static bool IsDefault(string name) => name == Default;

    public static string ValidateName(string? input, IEnumerable<string> existing, string? replacing = null)
    {
        var name = input?.Trim() ?? "";
        if (name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new InvalidDataException("Enter a group name of 1–80 characters without control characters.");
        if (name.Equals(Default, StringComparison.OrdinalIgnoreCase)
            || existing.Any(g => g != replacing && g.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A group with that name already exists. Default is reserved.");
        return name;
    }

    public static WorkspaceConfiguration Create(WorkspaceConfiguration workspace, string? input)
    {
        var name = ValidateName(input, List(workspace));
        var next = workspace with { Groups = List(workspace).Append(name).ToArray() };
        next.Validate();
        return next;
    }

    public static WorkspaceConfiguration Rename(WorkspaceConfiguration workspace, string oldName, string? input)
    {
        RequireEditable(workspace, oldName);
        var name = ValidateName(input, List(workspace), oldName);
        return workspace with
        {
            Groups = List(workspace).Select(g => g == oldName ? name : g).ToArray(),
            Targets = workspace.Targets.Select(t => t.Group == oldName ? t with { Group = name } : t).ToArray(),
            Alerts = workspace.Alerts.Select(r => r.Group == oldName ? r with { Group = name } : r).ToArray()
        };
    }

    public static WorkspaceConfiguration Delete(WorkspaceConfiguration workspace, string name, bool removeReferencedRules = false)
    {
        RequireEditable(workspace, name);
        if (!removeReferencedRules && workspace.Alerts.Any(r => r.Group == name))
            throw new InvalidOperationException("This group is referenced by alert rules. Confirm removal of those rules or cancel.");
        return workspace with
        {
            Groups = List(workspace).Where(g => g != name).ToArray(),
            Targets = workspace.Targets.Select(t => t.Group == name ? t with { Group = Default } : t).ToArray(),
            Alerts = workspace.Alerts.Where(r => r.Group != name).ToArray()
        };
    }

    public static WorkspaceConfiguration Move(WorkspaceConfiguration workspace, IEnumerable<string> targets, string group)
    {
        if (!List(workspace).Contains(group, StringComparer.Ordinal)) throw new InvalidDataException("Select an existing group.");
        var selected = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return workspace with
        {
            Groups = List(workspace),
            Targets = workspace.Targets.Select(t => selected.Contains(t.Target) ? t with { Group = group } : t).ToArray()
        };
    }

    private static void RequireEditable(WorkspaceConfiguration workspace, string name)
    {
        if (IsDefault(name)) throw new InvalidOperationException("Default cannot be renamed or deleted.");
        if (!List(workspace).Contains(name, StringComparer.Ordinal)) throw new InvalidDataException("Select an existing group.");
    }
}
