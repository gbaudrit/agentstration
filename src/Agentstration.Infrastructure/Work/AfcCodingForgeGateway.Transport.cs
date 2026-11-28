using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class AfcCodingForgeGateway
{
    private async Task<TReply> CallAsync<TRequest, TReply>(WorkProjectResolvedBinding connection, string operation,
        Func<string, TRequest> request, int maximumBytes, CancellationToken token)
    {
        var evidence = connection.Contract!;
        var provider = evidence.Provider;
        var tools = new List<(ToolResource Tool, WorkProjectContributionPin Pin)>();
        foreach (var pin in evidence.Contributions)
        {
            var tool = (await resources.GetExactAsync<ToolResource>(pin.Resource.Address, token))?.Value;
            AfcValidation.Require(tool is not null && tool.Uid == pin.Resource.Uid && tool.Generation == pin.Resource.Generation
                && (toolAccess is null ? tool.Definition.Enabled : await toolAccess.IsEnabledAsync(tool, token)) && tool.Definition.Discovery?.Available == true, "connection_changed");
            if (tool.Definition.Schema is { Output: { } output } schema
                && JsonElement.DeepEquals(schema.Input, AfcSchemas.Input(operation)) && JsonElement.DeepEquals(output, AfcSchemas.Output(operation)))
                tools.Add((tool, pin));
        }
        AfcValidation.Require(tools.Count == 1, "invalid_mapping");
        var (selected, selectedPin) = tools[0];
        AfcValidation.Require(selected.Definition.Metadata.TryGetValue(AfcContract.Id, out var metadata), "connection_changed");
        var declaration = AfcValidation.Parse<AfcDeclaration>(metadata);
        AfcSchemas.Validate(declaration);
        var id = Guid.NewGuid().ToString("N");
        var caller = current.Current;
        var result = await pipeline.ExecuteAsync(new ToolExecutionContext
        {
            BeforeProviderInvoke = connection.Binding.Connection is { } selection && connection.Service is { } captured
                ? async ct =>
                {
                    var inspected = await bindings.InspectConnectionAsync(new(current.Current.WorkspaceId), current.Current.PrincipalId, selection, ct);
                    if (!inspected.IsSuccess || WorkPlanDefinition.Digest(inspected.Value) != WorkPlanDefinition.Digest(captured))
                        throw new ToolResolutionException("work_project_connection_changed", "The retained connection is no longer available.");
                }
            : null,
            ResolveProviderHeaders = selected.Definition.Metadata.ContainsKey(WorkProjectConnectedServiceResolver.TransportMetadata) && connection.Binding.Connection is { } headerSelection && connection.Service is { } headerEvidence
                ? ct => ResolveHeadersAsync(headerSelection, headerEvidence, ct) : null,
            OwnerKind = ToolExecutionOwnerKind.WorkOperation,
            ToolCallId = id,
            InvocationId = "work-afc:" + id,
            ToolId = selected.Name,
            ToolName = selected.Definition.ExternalId!,
            ToolUid = selected.Uid,
            ToolGeneration = selected.Generation,
            ToolScopeRef = selected.ScopeRef,
            ToolNamespace = selected.Namespace,
            ExternalToolId = selected.Definition.ExternalId,
            ToolProviderId = provider.Address.Name,
            ToolProviderUid = provider.Uid,
            ToolProviderGeneration = provider.Generation,
            ToolProviderScopeRef = provider.Address.ScopeRef,
            ToolProviderNamespace = provider.Address.Namespace,
            ExpectedProviderRouteDigest = selectedPin.RouteDigest,
            ExpectedToolSchemaDigest = ToolInvocationFingerprint.Schemas(AfcSchemas.Input(operation), AfcSchemas.Output(operation)),
            ExpectedProviderToolContracts = AfcContract.Tools.ToDictionary(name => declaration.ToolPrefix + "_" + name,
                name => ToolInvocationFingerprint.Schemas(AfcSchemas.Input(name), AfcSchemas.Output(name)), StringComparer.Ordinal),
            TenantId = caller.TenantId,
            WorkspaceId = new(caller.WorkspaceId),
            PrincipalId = caller.PrincipalId,
            CorrelationId = "work-afc:" + id,
            PersistArguments = false,
            Arguments = JsonSerializer.SerializeToElement(new AfcInvocation<TRequest>(Target(connection), request(id)), AfcContract.Json)
        }, token);
        AfcValidation.Require(result is { ValueKind: JsonValueKind.Object }, "invalid_result");
        var envelope = result.Value;
        AfcValidation.Bytes(envelope, 512 * 1024);
        AfcValidation.Require((!envelope.TryGetProperty("isError", out var error) || error.ValueKind == JsonValueKind.False)
            && envelope.TryGetProperty("structuredContent", out _), "invalid_result");
        var content = envelope.GetProperty("structuredContent"); AfcValidation.Bytes(content, maximumBytes);
        var reply = AfcValidation.Parse<AfcReply<TReply>>(content);
        AfcValidation.Require(reply.ContractId == AfcContract.Id && reply.Version == evidence.Version && reply.RequestId == id
            && reply.Error is null && reply.Data is not null, "invalid_result");
        if (reply.Data is AfcDescription description)
            AfcValidation.Require(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(description.Declaration, AfcContract.Json),
                JsonSerializer.SerializeToElement(declaration, AfcContract.Json)), "connection_changed");
        return reply.Data;
    }

    private async Task<ResolvedSecretInputs> ResolveHeadersAsync(WorkProjectConnectionSelection selection, WorkProjectConnectionEvidence evidence, CancellationToken token)
    {
        var fresh = await bindings.InspectConnectionAsync(new(current.Current.WorkspaceId), current.Current.PrincipalId, selection, token);
        if (!fresh.IsSuccess || WorkPlanDefinition.Digest(fresh.Value) != WorkPlanDefinition.Digest(evidence) || services is null)
            throw new ToolResolutionException("work_project_connection_changed", "The selected connection changed.");
        return await services.ResolveHeadersAsync(new(current.Current.WorkspaceId), selection, evidence, token);
    }

}
