using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public sealed record KnowledgeBusinessDescription(string? Description, string Origin);

/// <summary>Live editorial metadata over immutable execution selections. Never resolves content or providers.</summary>
public sealed class KnowledgeToolMetadataService(IResourceStore resources,
    KnowledgeSourceManagementService sources, ICurrentRequestContext current)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string SelectionAnnotation = "agentstration.io/knowledge-contribution";
    public const string UsageAnnotation = "agentstration.io/knowledge-operation-help";
    public const string ArgumentGuidance = " Always place arguments inside request, for example {\"request\":{\"snapshotId\":\"<returned id>\"}}. Never send snapshotId beside request. Omit request.snapshotId initially; for related calls, only reuse a returned id and add this Tool's declared fields inside request. For input details, copy the describeTool and describeArguments returned by discovery. Per-input describe Tools already fix their input; do not supply request.input.";

    public const string FlatArgumentGuidance = " Pass declared arguments directly as Tool properties; do not add a request wrapper. Omit snapshotId initially; for related calls, only reuse an exact returned snapshotId. For input details, copy the describeTool and describeArguments returned by discovery. Per-input describe Tools already fix their input and accept {}; do not supply input.";

    public const string ODataQueryGuidance = " Query encoding: odata-options+json. request.query is an object whose option names include the dollar prefix: $filter, $select, $orderby or $apply, with string values. Use OData operators eq/ne/gt/ge/lt/le and and/or, not == or SQL. Example shape: {\"request\":{\"pageSize\":10,\"query\":{\"$filter\":\"status eq 'open'\",\"$select\":\"status,value\"}}}. Replace example fields with exact fields from this input's described schema; do not invent field names. Use request.pageSize for pages; continue with request.cursor only. The supported aggregate/groupby subset uses $apply; $top/$skip, joins and expansion are unsupported.";

    public const string FlatODataQueryGuidance = " Query encoding: odata-options+json. query is an object whose option names include the dollar prefix: $filter, $select, $orderby or $apply, with string values. Use OData operators eq/ne/gt/ge/lt/le and and/or, not == or SQL. Example shape: {\"pageSize\":10,\"query\":{\"$filter\":\"status eq 'open'\",\"$select\":\"status,value\"}}. Replace example fields with exact fields from this input's described schema; do not invent field names. Use pageSize for pages; continue with cursor only. The supported aggregate/groupby subset uses $apply; $top/$skip, joins and expansion are unsupported.";

    public async Task<KnowledgeSourceResource?> ResolveSourceAsync(ToolDefinitionVersionResource publication, CancellationToken ct)
    {
        var literal = publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>().SingleOrDefault(b => b.Path == "/source");
        if (literal is null || !publication.Metadata.Annotations.TryGetValue("agentstration.io/knowledge-source-uid", out var identity)
            || !Guid.TryParse(identity, out var uid)) return null;
        var value = literal.Value;
        var consumer = KnowledgeOperationalRead.AdministrativeScope(current)
            ?? ResourceScopeRef.Workspace(current.Current.WorkspaceId);
        var id = new KnowledgeSourceId(value.GetProperty("name").GetString()!,
            ResourceNamespace.Parse(value.GetProperty("namespace").GetString()!),
            ResourceScopeRef.Parse(value.GetProperty("scopeRef").GetString()!));
        var source = (await sources.ResolveForUseAsync(id, consumer, ct)).Value;
        return source.Uid == uid && value.GetProperty("uid").GetGuid() == uid ? source : null;
    }

    public async Task<KnowledgeBusinessDescription> DescriptionAsync(KnowledgeSourceResource source,
        KnowledgeContributionSelection? selection, string? fallback, CancellationToken ct)
    {
        if (selection is null || selection == new KnowledgeContributionSelection("derived", source.Uid))
            return new(source.Definition.Description, "knowledge_source");
        var pin = source.Composition?.DataSources.SingleOrDefault(b => b.BindingName == selection.BindingName && b.DataSourceUid == selection.DataSourceUid);
        var binding = source.Definition.DataSources.SingleOrDefault(b => b.Name == selection.BindingName);
        if (pin is null || binding is null) return new(fallback, "published");
        if (!string.IsNullOrWhiteSpace(binding.Description)) return new(binding.Description, "input");
        // Only the explicitly bound business description is inherited through Knowledge Source Use.
        // Connection configuration, private parameters and executable Data Source access remain outside this boundary.
        var dataSource = (await resources.GetExactAsync<DataSourceResource>(new(pin.DataSourceScopeRef,
            pin.DataSourceNamespace, DataSourceResourceKinds.DataSource, pin.DataSourceName), ct))?.Value;
        return dataSource?.Uid == selection.DataSourceUid
            ? new(dataSource.Definition.Description, "data_source") : new(fallback, "published");
    }

    public async Task<KnowledgeToolProposal> PresentAsync(KnowledgeSourceResource source, KnowledgeToolProposal tool, CancellationToken ct)
    {
        var business = await DescriptionAsync(source, tool.Contribution, tool.BusinessDescription, ct);
        var name = tool.Contribution?.BindingName ?? source.Definition.DisplayName;
        return tool with
        {
            InputSchema = InputSchemaForModel(tool.InputSchema, tool.Profile),
            BusinessDescription = business.Description,
            DisplayName = name + " · " + KnowledgeAccessSchemas.Route(tool.Operation),
            Description = Format(name, business.Description, tool.Usage ?? tool.Description, tool.Profile, UsesFlatArguments(tool.InputSchema))
        };
    }

    public static bool UsesFlatArguments(JsonElement schema) => schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
        && !properties.TryGetProperty("request", out _);

    public static string Format(string name, string? business, string usage, StorageQueryProfilePin? profile = null, bool flatArguments = false) =>
        (string.IsNullOrWhiteSpace(business) ? name + ": " + usage : name + ": " + business + "\n" + usage)
        + (profile?.Id == "odata/4.01" ? flatArguments ? FlatODataQueryGuidance : ODataQueryGuidance : string.Empty)
        + (flatArguments ? FlatArgumentGuidance : ArgumentGuidance);

    // Presentation may narrow a pinned profile's accepted input language, never widen the published relay.
    public static JsonElement InputSchemaForModel(JsonElement schema, StorageQueryProfilePin? profile)
    {
        if (profile is not { Id: "odata/4.01", Encoding: "odata-options+json" }
            || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return schema;
        var flatArguments = UsesFlatArguments(schema);
        var argumentSchema = flatArguments ? schema : properties.GetProperty("request");
        if (!argumentSchema.TryGetProperty("properties", out var requestProperties)
            || !requestProperties.TryGetProperty("query", out _)) return schema;
        var root = JsonNode.Parse(schema.GetRawText())!.AsObject();
        var requestNode = flatArguments ? root : root["properties"]!["request"]!.AsObject();
        var fields = requestNode["properties"]!.AsObject();
        requestNode["description"] = flatArguments
            ? "Tool arguments. Pass snapshotId, pageSize, query or cursor directly; do not add a request wrapper."
            : "Call arguments. Put all parameters here, including pageSize as a JSON number. No parameters belong beside request.";
        fields["query"] = new JsonObject
        {
            ["type"] = "object",
            ["description"] = "OData options object, not a URL or query string. Use declared record fields and OData expressions, for example status eq 'open'.",
            ["properties"] = new JsonObject
            {
                ["$filter"] = Option(4096),
                ["$select"] = Option(1024),
                ["$orderby"] = Option(1024),
                ["$apply"] = Option(2048)
            },
            ["additionalProperties"] = false
        };
        if (fields["pageSize"] is JsonObject pageSize)
            pageSize["description"] = flatArguments
                ? "Optional JSON integer, for example 50. Pass it directly as pageSize; never use a quoted string."
                : "Optional JSON integer, for example 50. Put it inside request; never use a quoted string.";
        if (!flatArguments)
        {
            var required = root["required"] as JsonArray ?? new JsonArray();
            if (!required.Any(value => value?.GetValue<string>() == "request")) required.Add("request");
            root["required"] = required.DeepClone();
        }
        return JsonSerializer.SerializeToElement(root);

        static JsonObject Option(int maximum) => new() { ["type"] = "string", ["maxLength"] = maximum };
    }

    public static StorageQueryProfilePin? Profile(ToolDefinitionVersionResource publication) =>
        publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>()
            .SingleOrDefault(b => b.Path == "/request/profile")?.Value.Deserialize<StorageQueryProfilePin>(Json);

    public static KnowledgeContributionSelection? Selection(ToolDefinitionVersionResource publication)
    {
        var literal = publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>().SingleOrDefault(b => b.Path == "/request/contribution");
        return literal is not null ? literal.Value.Deserialize<KnowledgeContributionSelection>(Json)
            : publication.Metadata.Annotations.TryGetValue(SelectionAnnotation, out var value)
                ? JsonSerializer.Deserialize<KnowledgeContributionSelection>(value, Json) : null;
    }
}
