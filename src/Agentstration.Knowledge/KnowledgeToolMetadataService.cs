using System.Text.Json;
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
    public const string UsageAnnotation = "agentstration.io/knowledge-operation-help";
    public const string ArgumentGuidance = " Always place arguments inside request, for example {\"request\":{\"snapshotId\":\"<returned id>\"}}. Never send snapshotId beside request. Omit request.snapshotId initially; for related calls, only reuse a returned id and add this Tool's declared fields inside request.";

    public const string ODataQueryGuidance = " Query encoding: odata-options+json. request.query is an object whose option names include the dollar prefix: $filter, $select, $orderby or $apply, with string values. Use OData operators eq/ne/gt/ge/lt/le and and/or, not == or SQL. Example shape: {\"request\":{\"pageSize\":10,\"query\":{\"$filter\":\"status eq 'open'\",\"$select\":\"status,value\"}}}. Replace example fields with exact fields from this input's described schema; do not invent field names. Use request.pageSize for pages; continue with request.cursor only. The supported aggregate/groupby subset uses $apply; $top/$skip, joins and expansion are unsupported.";

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
            BusinessDescription = business.Description,
            DisplayName = name + " · " + KnowledgeAccessSchemas.Route(tool.Operation),
            Description = Format(name, business.Description, tool.Usage ?? tool.Description, tool.Profile)
        };
    }

    public static string Format(string name, string? business, string usage, StorageQueryProfilePin? profile = null) =>
        (string.IsNullOrWhiteSpace(business) ? name + ": " + usage : name + ": " + business + "\n" + usage)
        + (profile?.Id == "odata/4.01" ? ODataQueryGuidance : string.Empty) + ArgumentGuidance;

    public static StorageQueryProfilePin? Profile(ToolDefinitionVersionResource publication) =>
        publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>()
            .SingleOrDefault(b => b.Path == "/request/profile")?.Value.Deserialize<StorageQueryProfilePin>(Json);

    public static KnowledgeContributionSelection? Selection(ToolDefinitionVersionResource publication) =>
        publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>()
            .SingleOrDefault(b => b.Path == "/request/contribution")?.Value.Deserialize<KnowledgeContributionSelection>(Json);
}
