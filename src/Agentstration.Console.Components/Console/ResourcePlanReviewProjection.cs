using System.Text.Json;
using Agentstration.ResourcePlanning.Contracts;

namespace Agentstration.Web.Console;

public sealed record ResourcePlanFieldDifference(string Path, string? Current, string? Proposed);
public sealed record ResourcePlanGraphNode(string LogicalId, string Kind, ResourceChangeOperation Operation, int X, int Y);
public sealed record ResourcePlanGraphEdge(string From, string To);
public sealed record ResourcePlanGraph(IReadOnlyList<ResourcePlanGraphNode> Nodes, IReadOnlyList<ResourcePlanGraphEdge> Edges);

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

    public static IReadOnlyList<ResourcePlanFieldDifference> ReviewFields(ResourceChange change) => Differences(change)
        .Where(value => value.Path.StartsWith("definition.", StringComparison.Ordinal))
        .Where(value => change.Operation != ResourceChangeOperation.Create || !IsEmpty(value.Proposed))
        .OrderBy(value => ReviewRank(value.Path))
        .ThenBy(value => value.Path, StringComparer.Ordinal)
        .ToArray();

    public static IReadOnlyList<ResourcePlanFieldDifference> HighlightFields(ResourceChange change)
    {
        string[] paths = change.Proposed.Kind switch
        {
            "Agent" => ["definition.modelProfile.name", "definition.runtimeProfile.name", "definition.behaviors"],
            "Flow" => ["definition.spec.flowKind", "definition.spec.pattern.strategy", "definition.version"],
            "Entry" => ["definition.presentation.kind", "definition.binding.resourceId", "definition.behavior.allowConversation"],
            _ => []
        };
        var differences = ReviewFields(change).ToDictionary(value => value.Path, StringComparer.Ordinal);
        return paths.Where(differences.ContainsKey).Select(path => differences[path]).ToArray();
    }

    public static string DisplayName(ResourceChange change) => DefinitionText(change, "displayName") ?? change.LogicalId;

    public static string? Description(ResourceChange change) => DefinitionText(change, "description");

    public static string? ModelProfile(ResourceChange change)
    {
        if (!change.Proposed.Definition.TryGetProperty("modelProfile", out var profile) || profile.ValueKind != JsonValueKind.Object
            || !profile.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) return null;
        var profileName = name.GetString();
        if (string.IsNullOrWhiteSpace(profileName)) return null;
        var profileNamespace = profile.TryGetProperty("namespace", out var @namespace) && @namespace.ValueKind == JsonValueKind.String
            ? @namespace.GetString() : null;
        return $"{(string.IsNullOrWhiteSpace(profileNamespace) ? change.Proposed.Metadata.Namespace.Value : profileNamespace)}/{profileName}";
    }

    public static ResourceChange? ChangeForIssue(ResourceChangeSet changeSet, ResourceChangeSetValidationIssue issue)
    {
        const string prefix = "changes[";
        if (!issue.Path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var closing = issue.Path.IndexOf(']', prefix.Length);
        return closing > prefix.Length && int.TryParse(issue.Path.AsSpan(prefix.Length, closing - prefix.Length), out var order)
            ? changeSet.Changes.FirstOrDefault(value => value.Order == order) : null;
    }

    public static string FormatValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        if (!value.StartsWith('[')) return value;
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.ValueKind != JsonValueKind.Array) return value;
            var items = json.RootElement.EnumerateArray().ToArray();
            return items.Length == 0 ? "—" : items.All(item => item.ValueKind == JsonValueKind.String)
                ? string.Join(", ", items.Select(item => item.GetString())) : value;
        }
        catch (JsonException) { return value; }
    }

    public static ResourcePlanGraph Graph(ResourceChangeSet changeSet)
    {
        var changes = changeSet.Changes.OrderBy(value => value.Order).ToArray();
        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            var depth = change.DependsOn.Where(depths.ContainsKey).Select(value => depths[value] + 1).DefaultIfEmpty(0).Max();
            depths.TryAdd(change.LogicalId, depth);
        }
        var layerCounts = changes.GroupBy(change => depths[change.LogicalId]).ToDictionary(group => group.Key, group => group.Count());
        var maximumRows = layerCounts.Values.DefaultIfEmpty(1).Max();
        var layerRows = new Dictionary<int, int>();
        var nodes = changes.Select(change =>
        {
            var depth = depths[change.LogicalId];
            var row = layerRows.GetValueOrDefault(depth);
            layerRows[depth] = row + 1;
            return new ResourcePlanGraphNode(change.LogicalId, change.Proposed.Kind, change.Operation,
                depth * 300, (maximumRows - layerCounts[depth]) * 85 + row * 170);
        }).ToArray();
        var edges = changes.SelectMany(change => change.DependsOn.Select(dependency => (change.LogicalId, dependency)))
            .Where(pair => depths.ContainsKey(pair.LogicalId) && depths.ContainsKey(pair.dependency))
            .Select(pair => new ResourcePlanGraphEdge(pair.dependency, pair.LogicalId))
            .ToArray();
        return new(nodes, edges);
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

    private static string? DefinitionText(ResourceChange change, string property) =>
        change.Proposed.Definition.ValueKind == JsonValueKind.Object
        && change.Proposed.Definition.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsEmpty(string? value) => string.IsNullOrWhiteSpace(value) || value is "[]" or "{}";

    private static int ReviewRank(string path) => path switch
    {
        "definition.displayName" => 0,
        "definition.description" => 1,
        "definition.modelProfile.name" => 2,
        "definition.runtimeProfile.name" => 3,
        "definition.spec.flowKind" or "definition.presentation.kind" => 4,
        "definition.spec.pattern.strategy" or "definition.binding.resourceId" => 5,
        "definition.instructions" => 6,
        "definition.behaviors" => 7,
        _ => 10
    };
}
