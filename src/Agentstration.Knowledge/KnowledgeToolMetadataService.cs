using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public sealed record KnowledgeBusinessDescription(string? Description, string Origin);

/// <summary>Live editorial metadata over immutable execution selections. Never resolves content or providers.</summary>
public sealed class KnowledgeToolMetadataService(IResourceStore resources,
    KnowledgeSourceManagementService sources, ICurrentRequestContext current)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string UsageAnnotation = "agentstration.io/knowledge-operation-help";

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
            Description = Format(name, business.Description, tool.Usage ?? tool.Description)
        };
    }

    public static string Format(string name, string? business, string usage) =>
        string.IsNullOrWhiteSpace(business) ? name + ": " + usage : name + ": " + business + "\n" + usage;

    public static KnowledgeContributionSelection? Selection(ToolDefinitionVersionResource publication) =>
        publication.Definition.Bindings.OfType<LiteralToolArgumentBinding>()
            .SingleOrDefault(b => b.Path == "/request/contribution")?.Value.Deserialize<KnowledgeContributionSelection>(Json);
}
