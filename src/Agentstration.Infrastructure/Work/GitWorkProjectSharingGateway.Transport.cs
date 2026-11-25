using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Application.Work;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

public sealed partial class GitWorkProjectSharingGateway(IWorkProjectBindingResolver bindings, IResourceStore resources,
    IToolExecutionPipeline pipeline, ICurrentRequestContext current, IAuthorizationService authorization, TimeProvider clock) : IWorkProjectSharingGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32 };
    private sealed record GitFile(string Path, string Revision, string ContentBase64, bool Executable);
    private sealed record GitResponse(string OperationKey, string IntentDigest, string Status, string RefName,
        string? Revision = null, string? BaseRevision = null, string? CurrentRevision = null,
        IReadOnlyList<GitFile>? Files = null, string? DiffBase64 = null, string? ErrorCode = null);
    private sealed record Route(ToolResource Tool, ToolProviderResource Provider, string RefName);
    private async Task<Result<Route>> ResolveAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, CancellationToken token)
    {
        Result<Route> Failure(string code) => Result<Route>.Failure(code);
        var trusted = current.Current;
        if (trusted.WorkspaceId != workspaceId.Value || trusted.PrincipalId != principalId
            || !await authorization.HasPermissionAsync(trusted, AuthorizationPermissions.RunsExecute, token)) return Failure("sharing_provider_denied");
        if (binding.Purpose != WorkProjectBindingPurpose.Sharing || binding.Target.Address.Kind != ToolResourceKinds.Tool)
            return Failure("sharing_repository_connection_required");
        var access = await bindings.ResolveAsync(workspaceId, principalId, binding, token);
        if (!access.IsSuccess) return Failure(access.ErrorCode!);
        var tool = (await resources.GetExactAsync<ToolResource>(binding.Target.Address, token))?.Value;
        if (tool is null || tool.Uid != binding.Target.Uid || tool.Generation != binding.Target.Generation || !tool.Definition.Enabled || tool.Definition.Discovery?.Available != true) return Failure("sharing_provider_unavailable");
        if (tool.Definition.RequiresApproval) return Failure("sharing_provider_approval_required");
        if (tool.ScopeRef is not { } scope || tool.Definition.ExternalId is not { } externalId
            || !tool.Definition.Metadata.TryGetValue("agentstration.toolPublicationUid", out var publicationId)
            || publicationId.ValueKind != JsonValueKind.String || !publicationId.TryGetGuid(out var uid)) return Failure("sharing_protected_relay_required");
        var publication = (await resources.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(scope, tool.Namespace,
            ToolResourceKinds.ToolDefinitionVersion, externalId), token))?.Value;
        if (publication?.Uid != uid || publication.Definition is not RelayToolDefinitionProperties relay || publication.Source is not { } source
            || !relay.Bindings.OfType<SecretToolArgumentBinding>().Any(value => value.Path == "/credential")) return Failure("sharing_protected_relay_required");
        if (!Capabilities(tool) || !Capabilities(source.Tool)) return Failure("sharing_capability_missing");
        if (relay.InputEnvelope is not null || relay.Bindings.Any(value => value.Path is not
            ("/credential" or "/repository" or "/authentication" or "/username" or "/allowedRefPrefixes" or "/allowedPathPrefixes" or "/request/refName")))
            return Failure("sharing_protected_relay_required");
        var literals = relay.Bindings.OfType<LiteralToolArgumentBinding>().ToDictionary(value => value.Path, value => value.Value);
        if (!literals.TryGetValue("/repository", out var repository) || repository.ValueKind != JsonValueKind.String
            || !literals.TryGetValue("/authentication", out var authentication) || authentication.ValueKind != JsonValueKind.String || authentication.GetString() is not ("Basic" or "Bearer")
            || !literals.TryGetValue("/request/refName", out var reference) || reference.ValueKind != JsonValueKind.String
            || reference.GetString() is not { } refName || !refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            || !literals.TryGetValue("/allowedRefPrefixes", out var refs) || refs.ValueKind != JsonValueKind.Array
            || !refs.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && refName.StartsWith(value.GetString()!, StringComparison.Ordinal))
            || !literals.TryGetValue("/allowedPathPrefixes", out var paths) || paths.ValueKind != JsonValueKind.Array
            || !ValidLocation(connection.Location) || !paths.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && (connection.Location == value.GetString() || connection.Location.StartsWith(value.GetString() + "/", StringComparison.Ordinal))))
            return Failure("sharing_protected_relay_required");
        if (tool.Definition.Provider is not { } providerReference) return Failure("sharing_provider_unavailable");
        var providerScope = providerReference.ScopeRef ?? scope;
        var providerAddress = ScopedResourceAddress.Create(providerScope, providerReference.Namespace ?? tool.Namespace, ToolResourceKinds.ToolProvider, providerReference.Name);
        var provider = (await resources.GetExactAsync<ToolProviderResource>(providerAddress, token))?.Value;
        if (provider is null || !provider.Definition.Enabled || provider.Definition.Mcp?.Internal != true) return Failure("sharing_provider_unavailable");
        var providerAccess = await bindings.ResolveAsync(workspaceId, principalId,
            binding with { Target = new(providerAddress, provider.Uid, provider.Generation) }, token);
        if (!providerAccess.IsSuccess) return Failure(providerAccess.ErrorCode!);
        return Result<Route>.Success(new(tool, provider, refName));
    }
    private async Task<Result<GitResponse>> InvokeAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding,
        WorkProjectSharingConnection connection, string operation, string mode, string key, string digest, object input, CancellationToken token)
    {
        Result<GitResponse> Failure(string code) => Result<GitResponse>.Failure(code);
        var resolved = await ResolveAsync(workspaceId, principalId, binding, connection, token);
        if (!resolved.IsSuccess) return Failure(resolved.ErrorCode!);
        var route = resolved.Value!; var tool = route.Tool; var provider = route.Provider;
        var trusted = current.Current; var scope = tool.ScopeRef!.Value; var externalId = tool.Definition.ExternalId!;
        var providerScope = provider.ScopeRef!.Value;
        var arguments = JsonSerializer.SerializeToElement(new { operation, mode, operationKey = key, intentDigest = digest, request = input }, Json);
        if (arguments.GetRawText().Length > 60_000) return Failure("sharing_transport_payload_limit");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var output = await pipeline.ExecuteAsync(new ToolExecutionContext
            {
                OwnerKind = ToolExecutionOwnerKind.WorkOperation, ToolCallId = key,
                InvocationId = $"sharing:{key}:{mode}",
                ToolId = tool.Name, ToolName = externalId, ToolNamespace = tool.Namespace, ToolScopeRef = scope,
                ToolUid = tool.Uid, ToolGeneration = tool.Generation, ExternalToolId = externalId,
                ToolProviderId = provider.Name, ToolProviderScopeRef = providerScope, ToolProviderNamespace = provider.Namespace,
                ToolProviderUid = provider.Uid, ToolProviderGeneration = provider.Generation,
                TenantId = trusted.TenantId, WorkspaceId = workspaceId, PrincipalId = principalId,
                CorrelationId = $"sharing:{key}", PersistArguments = false,
                Arguments = arguments
            }, linked.Token);
            if (output is null || Encoding.UTF8.GetByteCount(output.Value.GetRawText()) > 2 * 1024 * 1024 || output.Value.ValueKind != JsonValueKind.Object
                || !output.Value.TryGetProperty("structuredContent", out var content) || content.ValueKind != JsonValueKind.Object)
                return Failure("sharing_provider_response_invalid");
            var result = content.Deserialize<GitResponse>(Json);
            if (result is null || result.OperationKey != key || result.IntentDigest != digest || result.RefName != route.RefName
                || result.Status is not ("Missing" or "Succeeded" or "Conflict" or "Rejected" or "Uncertain")
                || result.ErrorCode is { Length: > 128 } || result.Files is { Count: > 64 }
                || result.Revision is not null && !Revision(result.Revision)) return Failure("sharing_provider_response_invalid");
            return Result<GitResponse>.Success(result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("sharing_provider_timeout"); }
        catch (ToolExecutionDeniedException) { return Failure("sharing_provider_governance_denied"); }
        catch (ToolInputValidationException) { return Failure("sharing_provider_contract_invalid"); }
        catch (ToolDefinitionInvocationException) { return Failure("sharing_provider_connection_unavailable"); }
        catch (ToolResolutionException) { return Failure("sharing_provider_outcome_uncertain"); }
        catch (HttpRequestException) { return Failure("sharing_provider_unavailable"); }
        catch (McpException) { return Failure("sharing_provider_outcome_uncertain"); }
        catch (JsonException) { return Failure("sharing_provider_response_invalid"); }
    }

    private static bool Capabilities(ToolResource tool)
    {
        if (!tool.Definition.Metadata.TryGetValue("agentstration.git.repository", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        return value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "1.0"
            && new[] { "expectedBase", "exactRevisionReads", "historyRead", "idempotentOperations", "operationRecovery" }
                .All(key => value.TryGetProperty(key, out var flag) && flag.ValueKind == JsonValueKind.True)
            && value.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array
            && new[] { "resolve", "read", "publish" }.All(operation => operations.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == operation));
    }
    private static bool ValidLocation(string location) => location.Length <= 256 && location.Split('/').All(segment => WorkProjectDefinition.ValidName(segment) && segment != ".git");
    private static bool Revision(string value) => value.Length == 40 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
