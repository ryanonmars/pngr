using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class TargetGroupCatalogTests
{
    private static WorkspaceConfiguration Workspace() => new()
    {
        Targets = [new("a.example", new() { DestinationPort = 8443 }, "Work", true), new("b.example", new(), "Default")],
        Alerts = [new() { Id = "intersection", Target = "a.example", Group = "Work" },
            new() { Id = "case-sensitive", Group = "work" }, new() { Id = "all" }]
    };

    [Fact]
    public async Task EmptyGroupsAndLegacyTargetsRoundTripThroughSaveRestartAndExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pngr-groups", Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = TargetGroupCatalog.Create(Workspace(), "  Gaming  ");
            var path = Path.Combine(directory, "workspace.json");
            await new WorkspaceStore(path).SaveAsync(workspace);
            var loaded = (await new WorkspaceStore(path).LoadAsync())!;
            Assert.Contains("Gaming", TargetGroupCatalog.List(loaded));
            Assert.DoesNotContain(loaded.Targets, t => t.Group == "Gaming");
            using var stream = new MemoryStream();
            await WorkspaceStore.ExportAsync(stream, loaded); stream.Position = 0;
            var imported = await WorkspaceStore.ImportAsync(stream);
            Assert.Equal(loaded.Groups, imported.Groups); Assert.Equal(loaded.Targets, imported.Targets);
            using var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Targets\":[{\"Target\":\"legacy.example\",\"Options\":{},\"Group\":\"Legacy\"}]}"));
            var old = await WorkspaceStore.ImportAsync(legacy);
            Assert.Equal(1, old.Version); Assert.Empty(old.Groups); Assert.Contains("Legacy", TargetGroupCatalog.List(old));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CatalogPreservesEveryGroupInAMaximumSizeLegacyWorkspace()
    {
        var legacy = new WorkspaceConfiguration
        {
            Targets = Enumerable.Range(0, 500).Select(i => new WorkspaceTarget($"host{i}.example", new(), $"Target group {i}")).ToArray(),
            Alerts = Enumerable.Range(0, 200).Select(i => new AlertRule { Group = $"Inactive group {i}" }).ToArray()
        };
        legacy.Validate();
        var catalog = TargetGroupCatalog.List(legacy);
        Assert.Equal(701, catalog.Length);
        (legacy with { Groups = catalog }).Validate();
    }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("Work")] [InlineData(" work ")]
    [InlineData("default")] [InlineData(" DEFAULT ")] [InlineData("bad\nname")]
    public void InvalidOrDuplicateNamesAreRejected(string name)
        => Assert.Throws<InvalidDataException>(() => TargetGroupCatalog.Create(Workspace(), name));

    [Fact]
    public void LongNamesAndDefaultChangesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TargetGroupCatalog.Create(Workspace(), new string('x', 81)));
        Assert.Throws<InvalidOperationException>(() => TargetGroupCatalog.Rename(Workspace(), "Default", "Other"));
        Assert.Throws<InvalidOperationException>(() => TargetGroupCatalog.Delete(Workspace(), "Default", true));
    }

    [Fact]
    public void RenameMigratesOnlyExactRuleReferencesAndPreservesTargetConfiguration()
    {
        var before = Workspace();
        var next = TargetGroupCatalog.Rename(before, "Work", "Office");
        Assert.Equal(before.Targets[0] with { Group = "Office" }, next.Targets[0]);
        Assert.Equal(before.Targets[1], next.Targets[1]);
        Assert.Equal(before.Alerts[0] with { Group = "Office" }, next.Alerts[0]);
        Assert.Equal(before.Alerts[1], next.Alerts[1]); Assert.Equal(before.Alerts[2], next.Alerts[2]);
        Assert.Contains("work", next.Groups); Assert.DoesNotContain("Work", next.Groups);
    }

    [Fact]
    public void ReferencedDeletionRequiresDecisionAndNeverBroadensRules()
    {
        var before = Workspace();
        Assert.Throws<InvalidOperationException>(() => TargetGroupCatalog.Delete(before, "Work"));
        var next = TargetGroupCatalog.Delete(before, "Work", true);
        Assert.Equal(before.Targets[0] with { Group = "Default" }, next.Targets[0]);
        Assert.DoesNotContain(next.Alerts, r => r.Id == "intersection");
        Assert.Equal(before.Alerts.Skip(1), next.Alerts);
        Assert.DoesNotContain("Work", TargetGroupCatalog.List(next));
    }

    [Fact]
    public void MultipleTargetsMoveAndReturnToDefaultWithoutChangingOtherFields()
    {
        var before = TargetGroupCatalog.Create(Workspace(), "Gaming");
        var next = TargetGroupCatalog.Move(before, ["A.EXAMPLE", "b.example"], "Gaming");
        Assert.All(next.Targets, t => Assert.Equal("Gaming", t.Group));
        Assert.Equal(before.Targets.Select(t => t.Options), next.Targets.Select(t => t.Options));
        Assert.Equal(before.Alerts, next.Alerts);
        Assert.Equal(before.Targets.Select(t => t with { Group = "Default" }),
            TargetGroupCatalog.Move(next, ["a.example", "b.example"], "Default").Targets);
        Assert.Throws<InvalidDataException>(() => TargetGroupCatalog.Move(next, ["a.example"], "Missing"));
        Assert.DoesNotContain("Gaming", TargetGroupCatalog.Delete(before, "Gaming").Groups);
    }
}
