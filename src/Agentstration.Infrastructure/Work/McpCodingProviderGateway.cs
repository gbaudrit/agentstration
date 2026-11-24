using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

public sealed class McpCodingProviderGateway(IWorkProjectBindingResolver bindings, IResourceStore resources,
    IToolExecutionPipeline pipeline, ICurrentRequestContext current, IAuthorizationService authorization, TimeProvider clock) : ICodingProviderGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };

    public async Task<Result<CodingProviderResponse>> InvokeAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, CodingProviderRequest request, CancellationToken token)
    {
        var trusted = current.Current;
        if (trusted.WorkspaceId != workspaceId.Value || trusted.PrincipalId != principalId
            || !await authorization.HasPermissionAsync(trusted, AuthorizationPermissions.RunsExecute, token)) return Failure("coding_provider_denied");
        if (binding.Purpose != WorkProjectBindingPurpose.Repository || binding.Target.Address.Kind != ToolResourceKinds.Tool)
            return Failure("coding_repository_connection_required");
        var access = await bindings.ResolveAsync(workspaceId, principalId, binding, token);
        if (!access.IsSuccess) return Failure(access.ErrorCode!);
        var tool = (await resources.GetExactAsync<ToolResource>(binding.Target.Address, token))?.Value;
        if (tool is null || tool.Uid != binding.Target.Uid || tool.Generation != binding.Target.Generation || !tool.Definition.Enabled || tool.Definition.Discovery?.Available != true) return Failure("coding_provider_unavailable");
        if (tool.Definition.RequiresApproval) return Failure("coding_provider_approval_required");
        if (tool.ScopeRef is not { } scope || tool.Definition.ExternalId is not { } externalId
            || !tool.Definition.Metadata.TryGetValue("agentstration.toolPublicationUid", out var publicationId)
            || publicationId.ValueKind != JsonValueKind.String || !publicationId.TryGetGuid(out var uid)) return Failure("coding_protected_relay_required");
        var publication = (await resources.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(scope, tool.Namespace,
            ToolResourceKinds.ToolDefinitionVersion, externalId), token))?.Value;
        if (publication?.Uid != uid || publication.Definition is not RelayToolDefinitionProperties relay || publication.Source is not { } source
            || !relay.Bindings.OfType<SecretToolArgumentBinding>().Any(value => value.Path == "/credential")) return Failure("coding_protected_relay_required");
        var declared = Capabilities(tool);
        var sourceCapabilities = Capabilities(source.Tool);
        if (declared is null || sourceCapabilities is null || declared.Provider != sourceCapabilities.Provider
            || declared.Operations.Any(operation => !sourceCapabilities.Operations.Contains(operation, StringComparer.Ordinal))
            || !declared.Operations.Contains(request.Operation, StringComparer.Ordinal)) return Failure("coding_provider_capability_missing");
        if (tool.Definition.Provider is not { } providerReference) return Failure("coding_provider_unavailable");
        var providerScope = providerReference.ScopeRef ?? scope;
        var providerAddress = ScopedResourceAddress.Create(providerScope, providerReference.Namespace ?? tool.Namespace, ToolResourceKinds.ToolProvider, providerReference.Name);
        var provider = (await resources.GetExactAsync<ToolProviderResource>(providerAddress, token))?.Value;
        if (provider is null || !provider.Definition.Enabled || provider.Definition.Mcp?.Internal != true) return Failure("coding_provider_unavailable");
        var providerAccess = await bindings.ResolveAsync(workspaceId, principalId,
            binding with { Target = new(providerAddress, provider.Uid, provider.Generation) }, token);
        if (!providerAccess.IsSuccess) return Failure(providerAccess.ErrorCode!);
        if (request.OperationKey.Length != 64 || request.IntentDigest.Length != 64
            || Encoding.UTF8.GetByteCount(request.Request.GetRawText()) > 64 * 1024) return Failure("coding_provider_request_invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var output = await pipeline.ExecuteAsync(new ToolExecutionContext
            {
                OwnerKind = ToolExecutionOwnerKind.WorkOperation, ToolCallId = request.OperationKey,
                InvocationId = $"coding:{request.OperationKey}:{request.Mode}",
                ToolId = tool.Name, ToolName = externalId, ToolNamespace = tool.Namespace, ToolScopeRef = scope,
                ToolUid = tool.Uid, ToolGeneration = tool.Generation, ExternalToolId = externalId,
                ToolProviderId = provider.Name, ToolProviderScopeRef = providerScope, ToolProviderNamespace = provider.Namespace,
                ToolProviderUid = provider.Uid, ToolProviderGeneration = provider.Generation,
                TenantId = trusted.TenantId, WorkspaceId = workspaceId, PrincipalId = principalId,
                CorrelationId = $"coding:{request.OperationKey}", PersistArguments = false,
                Arguments = JsonSerializer.SerializeToElement(request, Json)
            }, linked.Token);
            if (output is null || Encoding.UTF8.GetByteCount(output.Value.GetRawText()) > 512 * 1024 || output.Value.ValueKind != JsonValueKind.Object
                || !output.Value.TryGetProperty("structuredContent", out var content) || content.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(content.GetRawText()) > 256 * 1024) return Failure("coding_provider_response_invalid");
            var result = content.Deserialize<CodingProviderResponse>(Json);
            if (result is null || result.OperationKey != request.OperationKey || result.IntentDigest != request.IntentDigest
                || !Enum.IsDefined(result.Status) || result.ErrorCode is { Length: > 128 }
                || result.Repositories is { Count: > 16 } || result.Repositories?.Any(repository => repository is null || repository.Provider != declared.Provider) == true
                || result.PullRequest is { } pullRequest && pullRequest.Provider != declared.Provider) return Failure("coding_provider_response_invalid");
            return Result<CodingProviderResponse>.Success(result);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Uncertain(request, "coding_provider_timeout"); }
        catch (ToolExecutionDeniedException) { return Failure("coding_provider_governance_denied"); }
        catch (ToolInputValidationException) { return Failure("coding_provider_contract_invalid"); }
        catch (ToolDefinitionInvocationException) { return Uncertain(request, "coding_provider_connection_unavailable"); }
        catch (ToolResolutionException) { return Uncertain(request, "coding_provider_outcome_uncertain"); }
        catch (HttpRequestException) { return Uncertain(request, "coding_provider_unavailable"); }
        catch (McpException) { return Uncertain(request, "coding_provider_outcome_uncertain"); }
        catch (JsonException) { return Failure("coding_provider_response_invalid"); }
    }

    private static CodingProviderCapabilities? Capabilities(ToolResource tool)
    {
        if (!tool.Definition.Metadata.TryGetValue(CodingProviderProtocol.MetadataKey, out var metadata)) return null;
        try
        {
            var value = metadata.Deserialize<CodingProviderCapabilities>(Json);
            return value is { Version: CodingProviderProtocol.Version, Provider.Length: > 0 and <= 64, Operations.Count: >= 1 and <= 32 }
                && value.Operations.All(operation => operation is { Length: > 0 and <= 128 }) ? value : null;
        }
        catch (JsonException) { return null; }
    }
    private static Result<CodingProviderResponse> Uncertain(CodingProviderRequest request, string code) => Result<CodingProviderResponse>.Success(
        new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Uncertain, ErrorCode: code));
    private static Result<CodingProviderResponse> Failure(string code) => Result<CodingProviderResponse>.Failure(code);
}
