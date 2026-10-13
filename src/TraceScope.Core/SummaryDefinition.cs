namespace TraceScope.Core;

public enum SummaryLayout { Table, Tiles, Graphs }

/// <summary>Target names survive new monitoring sessions and workspace restoration.</summary>
public sealed record SummaryDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New summary";
    public IReadOnlyList<string> Targets { get; init; } = [];
    public SummaryLayout Layout { get; init; } = SummaryLayout.Table;
    public int FocusMinutes { get; init; } = 5;

    public void Validate()
    {
        if (!Guid.TryParse(Id, out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 80
            || !Enum.IsDefined(Layout) || FocusMinutes is < 1 or > 1440 || Targets is null
            || Targets.Count > 500 || Targets.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 253)
            || Targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Targets.Count)
            throw new InvalidDataException("Invalid summary configuration.");
    }

    public bool Includes(string target) => Targets.Contains(target, StringComparer.OrdinalIgnoreCase);
}
