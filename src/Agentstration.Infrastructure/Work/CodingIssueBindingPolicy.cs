using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed class CodingIssueBindingPolicy(IWorkTaskProviderGateway gateway, IResourceStore resources) : ICodingIssueBindingPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public async Task<Result<CodingIssueBinding>> ResolveAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkSharingIssueReference issue, CancellationToken token)
    {
        var route = await gateway.ResolveAsync(workspaceId, principalId, binding, ExternalTaskOperationKind.Update, token);
        if (!route.IsSuccess) return Failure(route.ErrorCode!);
        if (binding.Target.Address.Kind != ToolResourceKinds.Tool || route.Value!.Tool != binding.Target) return Failure("coding_issue_protected_connection_required");
        var tool = (await resources.GetExactAsync<ToolResource>(route.Value.Tool.Address, token))?.Value;
        if (tool is null || tool.ScopeRef is not { } scope || tool.Definition.ExternalId is not { } name
            || !tool.Definition.Metadata.TryGetValue("agentstration.toolPublicationUid", out var identity)
            || identity.ValueKind != JsonValueKind.String || !identity.TryGetGuid(out var uid)) return Failure("coding_issue_protected_connection_required");
        var publication = (await resources.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(scope, tool.Namespace, ToolResourceKinds.ToolDefinitionVersion, name), token))?.Value;
        if (publication?.Uid != uid || publication.Definition is not RelayToolDefinitionProperties relay || publication.Source is not { } source
            || !relay.Bindings.OfType<SecretToolArgumentBinding>().Any(value => value.Path == "/credential")
            || !Capabilities(tool, issue.Provider) || !Capabilities(source.Tool, issue.Provider)) return Failure("coding_issue_capability_missing");
        if (relay.InputEnvelope is not null || relay.Bindings.Any(value => value.Path is not ("/credential" or "/apiBaseUrl" or "/allowedRepositories" or "/managedLabelPrefix")))
            return Failure("coding_issue_protected_connection_required");
        var literals = relay.Bindings.OfType<LiteralToolArgumentBinding>().ToDictionary(value => value.Path, value => value.Value);
        if (!literals.TryGetValue("/apiBaseUrl", out var api) || api.ValueKind != JsonValueKind.String
            || !literals.TryGetValue("/allowedRepositories", out var repositories) || repositories.ValueKind != JsonValueKind.Array
            || !repositories.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.Object && value.TryGetProperty("repository", out var repository)
                && repository.ValueKind == JsonValueKind.String && string.Equals(repository.GetString(), issue.Repository, StringComparison.OrdinalIgnoreCase))
            || !literals.TryGetValue("/managedLabelPrefix", out var label) || label.ValueKind != JsonValueKind.String || label.GetString() is not { Length: > 0 } prefix
            || !issue.PlanLabelPrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !issue.ParticipantLabelPrefix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return Failure("coding_issue_protected_connection_required");
        return Result<CodingIssueBinding>.Success(new(issue.Provider, prefix));
    }
    private static bool Capabilities(ToolResource tool, string provider)
    {
        if (!tool.Definition.Metadata.TryGetValue(CodingIssueProtocol.MetadataKey, out var metadata)) return false;
        try
        {
            return metadata.Deserialize<CodingIssueCapabilities>(Json) is { Version: CodingIssueProtocol.Version, NativeHierarchy: true,
                NativeDependencies: true, CompletionReason: true, ManagedLabels: true, RevisionObservation: true } capabilities && capabilities.Provider == provider;
        }
        catch (JsonException) { return false; }
    }
    private static Result<CodingIssueBinding> Failure(string code) => Result<CodingIssueBinding>.Failure(code);
}
