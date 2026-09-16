using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.Web.Console;

public sealed record ResourcePlanFieldDifference(string Path, string? Current, string? Proposed);
public sealed record ResourcePlanGraphNode(string LogicalId, string Kind, ResourceChangeOperation Operation, int X, int Y);
public sealed record ResourcePlanGraphEdge(string From, string To, int X1, int Y1, int X2, int Y2);
public sealed record ResourcePlanGraph(IReadOnlyList<ResourcePlanGraphNode> Nodes, IReadOnlyList<ResourcePlanGraphEdge> Edges, int Width, int Height);

public static class ResourcePlanReviewProjection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static ResourceChangeSetSnapshot? LatestChangeSet(ResourcePlan plan, IEnumerable<ResourceChangeSetSnapshot> changeSets) =>
        changeSets.Where(value => value.Value.PlanId == plan.Id)
            .OrderByDescending(value => value.Value.PlanRevision)
            .ThenByDescending(value => value.Value.CreatedAt)
            .FirstOrDefault();

    public static bool IsStale(ResourcePlan plan, ResourceChangeSet changeSet) =>
        changeSet.PlanId != plan.Id || changeSet.PlanRevision != plan.Revision;

    public static ResourceChangeSetValidation? LatestValidation(ResourceChangeSet changeSet, IEnumerable<ResourceChangeSetValidation> validations) =>
        validations.Where(value => value.ChangeSetId == changeSet.Id)
            .OrderByDescending(value => value.ValidatedAt)
            .FirstOrDefault();

    public static bool IsStale(ResourcePlan plan, ResourceChangeSet changeSet, ResourceChangeSetValidation validation) =>
        IsStale(plan, changeSet) || validation.ChangeSetDigest != changeSet.Digest || validation.PlanRevision != changeSet.PlanRevision;

    public static IReadOnlyList<ResourcePlanFieldDifference> Differences(ResourceChange change)
    {
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        var proposed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (change.Current is not null) Flatten(change.Current.Document, string.Empty, current);
        Flatten(JsonSerializer.SerializeToElement(change.Proposed, JsonOptions), string.Empty, proposed);
        return current.Keys.Union(proposed.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Where(path => !current.TryGetValue(path, out var before) || !proposed.TryGetValue(path, out var after) || before != after)
            .Select(path => new ResourcePlanFieldDifference(path,
                current.GetValueOrDefault(path), proposed.GetValueOrDefault(path)))
            .ToArray();
    }

    public static ResourcePlanGraph Graph(ResourceChangeSet changeSet)
    {
        var nodes = new List<ResourcePlanGraphNode>();
        var byId = new Dictionary<string, ResourcePlanGraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changeSet.Changes.OrderBy(value => value.Order))
        {
            var depth = change.DependsOn.Where(byId.ContainsKey).Select(value => (byId[value].X - 24) / 240 + 1).DefaultIfEmpty(0).Max();
            var node = new ResourcePlanGraphNode(change.LogicalId, change.Proposed.Kind, change.Operation,
                24 + depth * 240, 24 + nodes.Count * 92);
            nodes.Add(node);
            byId.TryAdd(node.LogicalId, node);
        }
        var edges = changeSet.Changes.SelectMany(change => change.DependsOn.Select(dependency => (change.LogicalId, dependency)))
            .Where(pair => byId.ContainsKey(pair.LogicalId) && byId.ContainsKey(pair.dependency))
            .Select(pair => new ResourcePlanGraphEdge(pair.dependency, pair.LogicalId,
                byId[pair.dependency].X + 188, byId[pair.dependency].Y + 26,
                byId[pair.LogicalId].X, byId[pair.LogicalId].Y + 26))
            .ToArray();
        return new(nodes, edges, Math.Max(260, nodes.Select(value => value.X + 220).DefaultIfEmpty(0).Max()),
            Math.Max(90, nodes.Count * 92 + 24));
    }

    private static void Flatten(JsonElement value, string path, IDictionary<string, string> fields)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
                Flatten(property.Value, string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}", fields);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            fields[path] = value.GetRawText();
        else
            fields[path] = value.ToString();
    }
}
