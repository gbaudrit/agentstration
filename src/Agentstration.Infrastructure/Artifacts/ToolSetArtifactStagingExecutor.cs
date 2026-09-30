using System.Text.Json;
using Agentstration.Artifacts.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Artifacts;

public sealed class ToolSetArtifactStagingExecutor(ToolSetService toolSets, IToolExecutionPipeline pipeline) : IArtifactStagingToolExecutor
{
    public async Task ValidateAsync(ArtifactStagingBindingResource binding, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        foreach (var capability in RequiredCapabilities)
            _ = await ResolveAsync(binding, capability, cancellationToken);
    }

    public async Task<ArtifactBackendCreateResult> CreateAsync(ArtifactStagingBindingResource binding, StagedArtifactId artifactId,
        string mediaType, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(binding, ArtifactCapabilities.Create,
            JsonSerializer.SerializeToElement(new { artifactId = artifactId.ToString(), mediaType }), context, cancellationToken);
        return new ArtifactBackendCreateResult(RequiredString(result, "backendReference"));
    }

    public async Task<long> WriteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend, long offset,
        ReadOnlyMemory<byte> content, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(binding, ArtifactCapabilities.Write,
            JsonSerializer.SerializeToElement(new { backendReference = backend.BackendReference, offset, contentBase64 = Convert.ToBase64String(content.Span) }), context, cancellationToken);
        return RequiredLong(result, "length");
    }

    public async Task<ArtifactContentChunk> ReadAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        long offset, int length, ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(binding, ArtifactCapabilities.Read,
            JsonSerializer.SerializeToElement(new { backendReference = backend.BackendReference, offset, length }), context, cancellationToken);
        return new ArtifactContentChunk(RequiredLong(result, "offset"), RequiredString(result, "contentBase64"), RequiredBool(result, "endOfContent"));
    }

    public async Task<ArtifactBackendStat> StatAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        var result = await InvokeAsync(binding, ArtifactCapabilities.Stat,
            JsonSerializer.SerializeToElement(new { backendReference = backend.BackendReference }), context, cancellationToken);
        return new ArtifactBackendStat(RequiredLong(result, "length"), RequiredString(result, "sha256"));
    }

    public async Task DeleteAsync(ArtifactStagingBindingResource binding, ArtifactBackendResolution backend,
        ArtifactBackendCommandContext context, CancellationToken cancellationToken) =>
        _ = await InvokeAsync(binding, ArtifactCapabilities.Delete,
            JsonSerializer.SerializeToElement(new { backendReference = backend.BackendReference }), context, cancellationToken);

    private async Task<JsonElement> InvokeAsync(ArtifactStagingBindingResource binding, string capability, JsonElement arguments,
        ArtifactBackendCommandContext context, CancellationToken cancellationToken)
    {
        var selected = await ResolveAsync(binding, capability, cancellationToken);
        var member = selected.Member;
        var result = await pipeline.ExecuteAsync(new ToolExecutionContext
        {
            OwnerKind = ToolExecutionOwnerKind.ArtifactService,
            ToolCallId = context.CallId,
            InvocationId = $"{context.CallId}:attempt:1",
            ToolId = member.ToolName,
            ToolNamespace = member.ToolNamespace,
            ToolName = member.ExternalToolId,
            ToolProviderId = member.ProviderName,
            ToolProviderNamespace = member.ProviderNamespace,
            ExternalToolId = member.ExternalToolId,
            TenantId = context.TenantId,
            WorkspaceId = context.WorkspaceId,
            PrincipalId = context.PrincipalId,
            RunId = context.RunId,
            FlowStepId = context.FlowStepId,
            CorrelationId = context.CorrelationId,
            Arguments = arguments
        }, cancellationToken);
        return result is { ValueKind: JsonValueKind.Object } value
            ? value
            : throw Error("artifact_staging_backend_result_invalid", $"Capability '{capability}' returned no structured result.");
    }

    private Task<ToolSetRouteSelection> ResolveAsync(ArtifactStagingBindingResource binding, string capability, CancellationToken cancellationToken) =>
        toolSets.ResolveRouteExactAsync(
            binding.ScopeRef ?? throw Error("artifact_staging_binding_scope_invalid", "The Artifact staging binding has no Workspace scope."),
            binding.Definition.ToolSet.Namespace ?? binding.Namespace, binding.Definition.ToolSet.Name,
            binding.Definition.ToolSetVersion, capability, "default", cancellationToken);

    private static readonly string[] RequiredCapabilities =
        [ArtifactCapabilities.Create, ArtifactCapabilities.Write, ArtifactCapabilities.Read, ArtifactCapabilities.Stat, ArtifactCapabilities.Delete];
    private static string RequiredString(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()! : throw Error("artifact_staging_backend_result_invalid", $"Backend result property '{name}' is required.");
    private static long RequiredLong(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result)
        ? result : throw Error("artifact_staging_backend_result_invalid", $"Backend result property '{name}' is required.");
    private static bool RequiredBool(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? property.GetBoolean() : throw Error("artifact_staging_backend_result_invalid", $"Backend result property '{name}' is required.");
    private static ArtifactValidationException Error(string code, string message) => new(code, message);
}
