using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Atc.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

// The ATC SDK and wire DTOs stop here; Work persists neutral observations and receipts.
public sealed class AtcWorkTaskProviderGateway(IWorkProjectBindingResolver bindings, IResourceStore resources,
    IToolExecutionPipeline pipeline, ICurrentRequestContext current, TimeProvider clock) : IWorkTaskProviderGateway
{
    public async Task<Result<WorkTaskProviderRoute>> ResolveAsync(WorkspaceId workspaceId, Guid principalId,
        WorkProjectContextBinding binding, ExternalTaskOperationKind operation, CancellationToken token)
    {
        if (binding.Purpose != WorkProjectBindingPurpose.Tracking || binding.Contract is null || !Enum.IsDefined(operation))
            return Failure("task_provider_binding_invalid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var allowed = await bindings.ResolveAsync(workspaceId, principalId, binding, linked.Token);
            if (!allowed.IsSuccess) return Failure(allowed.ErrorCode!);
            var connection = allowed.Value!;
            if (connection.Contract is null) return Failure("task_provider_contract_missing");
            var description = await CallAsync<AtcDescribeRequest, AtcDescription>(connection, "describe",
                id => new(id), AtcContract.MaximumResultBytes, linked.Token);
            AtcSchemas.Validate(description.Declaration); AtcValidation.Capabilities(description.Capabilities);
            AtcValidation.Require(description.Target == Target(binding)
                && WorkPlanDefinition.Digest(description.Capabilities) == connection.Contract.CapabilitiesDigest
                && description.ImplementationId == connection.Contract.ImplementationId
                && description.ImplementationVersion == connection.Contract.ImplementationVersion, "connection_changed");
            var capabilities = description.Capabilities;
            if (!capabilities.Operations.Contains(Operation(operation))) return Failure("task_provider_capability_missing");
            return Result<WorkTaskProviderRoute>.Success(new(binding, binding.Target, new(AtcContract.Version,
                capabilities.Operations.Where(value => value != "list").Select(WorkOperation).ToArray(), capabilities.Recovery == AtcRecoveryScope.Durable,
                capabilities.Recovery != AtcRecoveryScope.None))
            {
                Provider = binding.Contract.Provider,
                Tracking = new(connection, new(capabilities.Operations, capabilities.UpdateFields,
                    capabilities.RelationKinds.Select(RelationKind).ToArray(), Concurrency(capabilities.Concurrency),
                    Recovery(capabilities.Recovery), capabilities.ReceiptWindowSeconds,
                    capabilities.Limits.MaximumPageSize, capabilities.Limits.MaximumResultBytes))
            });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("task_provider_timeout"); }
        catch (Exception error) when (Expected(error)) { return Failure("task_provider_contract_invalid"); }
    }

    public Task<Result<WorkTaskProviderRoute>> PrepareAsync(WorkTaskProviderRoute route, ExternalTaskOperationInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            AtcValidation.Require(input.Validate().IsSuccess && route.Tracking is not null && input.Tracking is { } target
                && target.ProviderId == route.Binding.Contract!.ProviderId && target.TargetId == route.Binding.Contract.TargetId,
                "target_mismatch");
            string? digest = null;
            if (input.Operation != ExternalTaskOperationKind.Read)
            {
                var change = Change(input);
                digest = AtcContract.Digest(Target(route.Binding), change);
                var request = new AtcMutationRequest(new string('r', 32), new string('k', 64), digest, change);
                AtcValidation.Mutation(Target(route.Binding), request, Capabilities(route));
                AtcValidation.Bytes(JsonSerializer.SerializeToElement(new AtcInvocation<AtcMutationRequest>(Target(route.Binding), request),
                    AtcContract.Json), AtcContract.MaximumRequestBytes);
            }
            return Task.FromResult(Result<WorkTaskProviderRoute>.Success(route with { Tracking = route.Tracking with { WireIntentDigest = digest } }));
        }
        catch (AtcException) { return Task.FromResult(Failure("task_provider_input_unsupported")); }
    }

    public async Task<ExternalTaskProviderResponse> InvokeAsync(WorkTaskOperation operation, bool recover, CancellationToken token)
    {
        ExternalTaskProviderResponse Unknown(string code) => new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Uncertain, ErrorCode: code);
        if (current.Current.TenantId != operation.TenantId || current.Current.WorkspaceId != operation.WorkspaceId.Value
            || current.Current.PrincipalId != operation.OwnerPrincipalId) return Unknown("task_provider_denied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var route = operation.Route;
            var allowed = await bindings.ResolveAsync(operation.WorkspaceId, operation.OwnerPrincipalId, route.Binding, linked.Token);
            if (!allowed.IsSuccess) return Unknown(allowed.ErrorCode!);
            if (route.Tracking is null || WorkPlanDefinition.Digest(allowed.Value) != WorkPlanDefinition.Digest(route.Tracking.Connection))
                return Unknown("task_provider_route_changed");
            var prepared = await PrepareAsync(route, operation.Input, linked.Token);
            if (!prepared.IsSuccess || prepared.Value!.Tracking!.WireIntentDigest != route.Tracking.WireIntentDigest)
                return Unknown("task_provider_intent_changed");
            var target = Target(route.Binding);
            var caps = Capabilities(route);
            if (WorkPlanDefinition.Digest(caps) != route.Tracking.Connection.Contract!.CapabilitiesDigest)
                return Unknown("task_provider_route_changed");
            if (operation.Input.Operation == ExternalTaskOperationKind.Read)
            {
                var item = await CallAsync<AtcReadRequest, AtcItem>(route.Tracking.Connection, "read",
                    id => new(id, operation.Input.TaskId!), caps.Limits.MaximumResultBytes, linked.Token);
                AtcValidation.Item(item, target, operation.Input.TaskId);
                return new(operation.OperationKey, operation.IntentDigest, ExternalTaskProviderStatus.Succeeded, Snapshot(item));
            }
            // Process-local recovery may recover an extant receipt, but Missing cannot authorize replay.
            // Beyond the declared receipt window even a durable provider cannot justify a new effect.
            if (recover && caps.Recovery == AtcRecoveryScope.None
                || caps.Recovery != AtcRecoveryScope.None && clock.GetUtcNow() >= operation.CreatedAt.AddSeconds(caps.ReceiptWindowSeconds))
                return Unknown("task_provider_recovery_unavailable");
            var change = Change(operation.Input);
            AtcMutationRequest? request = null;
            var receipt = await CallAsync<AtcMutationRequest, AtcReceipt>(route.Tracking.Connection, "mutate", id =>
            {
                request = new(id, operation.OperationKey, route.Tracking.WireIntentDigest!, change,
                    recover ? AtcMutationMode.Recover : AtcMutationMode.Execute);
                AtcValidation.Mutation(target, request, caps);
                return request;
            }, caps.Limits.MaximumResultBytes, linked.Token);
            AtcValidation.Receipt(receipt, target, request!);
            return new(operation.OperationKey, operation.IntentDigest, receipt.Status switch
            {
                AtcMutationStatus.Applied => ExternalTaskProviderStatus.Succeeded,
                AtcMutationStatus.Rejected => ExternalTaskProviderStatus.Rejected,
                AtcMutationStatus.Missing => ExternalTaskProviderStatus.Missing,
                _ => ExternalTaskProviderStatus.Uncertain
            }, receipt.Item is { } observed ? Snapshot(observed) : null, receipt.ErrorCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Unknown("task_provider_timeout"); }
        catch (ToolExecutionDeniedException) { return Unknown("task_provider_governance_denied"); }
        catch (Exception error) when (Expected(error)) { return Unknown("task_provider_outcome_uncertain"); }
    }

    private async Task<TReply> CallAsync<TRequest, TReply>(WorkProjectResolvedBinding connection, string operation,
        Func<string, TRequest> request, int maximumBytes, CancellationToken token)
    {
        var evidence = connection.Contract!;
        var provider = evidence.Provider;
        var tools = new List<(ToolResource Tool, WorkProjectContributionPin Pin)>();
        foreach (var pin in evidence.Contributions)
        {
            var tool = (await resources.GetExactAsync<ToolResource>(pin.Resource.Address, token))?.Value;
            AtcValidation.Require(tool is not null && tool.Uid == pin.Resource.Uid && tool.Generation == pin.Resource.Generation
                && tool.Definition.Enabled && tool.Definition.Discovery?.Available == true, "connection_changed");
            if (tool.Definition.Schema is { Output: { } output } schema
                && JsonElement.DeepEquals(schema.Input, AtcSchemas.Input(operation)) && JsonElement.DeepEquals(output, AtcSchemas.Output(operation)))
                tools.Add((tool, pin));
        }
        AtcValidation.Require(tools.Count == 1, "invalid_mapping");
        var (selected, selectedPin) = tools[0];
        AtcValidation.Require(selected.Definition.Metadata.TryGetValue(AtcContract.Id, out var metadata), "connection_changed");
        var declaration = AtcValidation.Parse<AtcDeclaration>(metadata);
        AtcSchemas.Validate(declaration);
        var id = Guid.NewGuid().ToString("N");
        var caller = current.Current;
        var result = await pipeline.ExecuteAsync(new ToolExecutionContext
        {
            OwnerKind = ToolExecutionOwnerKind.WorkOperation,
            ToolCallId = id,
            InvocationId = "work-atc:" + id,
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
            ExpectedToolSchemaDigest = ToolInvocationFingerprint.Schemas(AtcSchemas.Input(operation), AtcSchemas.Output(operation)),
            ExpectedProviderToolContracts = AtcContract.Tools.ToDictionary(name => declaration.ToolPrefix + "_" + name,
                name => ToolInvocationFingerprint.Schemas(AtcSchemas.Input(name), AtcSchemas.Output(name)), StringComparer.Ordinal),
            TenantId = caller.TenantId,
            WorkspaceId = new(caller.WorkspaceId),
            PrincipalId = caller.PrincipalId,
            CorrelationId = "work-atc:" + id,
            PersistArguments = false,
            Arguments = JsonSerializer.SerializeToElement(new AtcInvocation<TRequest>(Target(connection.Binding), request(id)), AtcContract.Json)
        }, token);
        AtcValidation.Require(result is { ValueKind: JsonValueKind.Object }, "invalid_result");
        var envelope = result.Value;
        AtcValidation.Bytes(envelope, 512 * 1024);
        AtcValidation.Require((!envelope.TryGetProperty("isError", out var error) || error.ValueKind == JsonValueKind.False)
            && envelope.TryGetProperty("structuredContent", out _), "invalid_result");
        var content = envelope.GetProperty("structuredContent"); AtcValidation.Bytes(content, maximumBytes);
        var reply = AtcValidation.Parse<AtcReply<TReply>>(content);
        AtcValidation.Require(reply.ContractId == AtcContract.Id && reply.Version == evidence.Version && reply.RequestId == id
            && reply.Error is null && reply.Data is not null, "invalid_result");
        if (reply.Data is AtcDescription description)
            AtcValidation.Require(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(description.Declaration, AtcContract.Json),
                JsonSerializer.SerializeToElement(declaration, AtcContract.Json)), "connection_changed");
        return reply.Data;
    }

    private static AtcTargetRef Target(WorkProjectContextBinding binding) => new(binding.Contract!.ProviderId, binding.Contract.TargetId,
        binding.Contract.BindingId, binding.Contract.Revision);
    private static AtcChange Change(ExternalTaskOperationInput input) => input.Operation switch
    {
        ExternalTaskOperationKind.Publish => new AtcCreate(input.Title!, input.Body),
        ExternalTaskOperationKind.Update => new AtcUpdate(input.TaskId!, input.ExpectedRevision!, input.Title, input.Body, input.Labels),
        ExternalTaskOperationKind.Comment => new AtcAddComment(input.TaskId!, input.ExpectedRevision!, input.Comment!),
        ExternalTaskOperationKind.Relate => new AtcAddRelation(input.TaskId!, input.ExpectedRevision!, new(RelationKind(input.Relation!.Kind),
            new(input.Relation.Item.ProviderId, input.Relation.Item.TargetId, input.Relation.Item.ItemId))),
        ExternalTaskOperationKind.Transition => new AtcApplyTransition(input.TaskId!, input.ExpectedRevision!, input.TransitionId!),
        _ => throw new AtcException("unsupported_operation")
    };
    private static string Operation(ExternalTaskOperationKind operation) => operation switch
    {
        ExternalTaskOperationKind.Read => "read",
        ExternalTaskOperationKind.Update => "update",
        ExternalTaskOperationKind.Publish => "create",
        ExternalTaskOperationKind.Comment => "comment",
        ExternalTaskOperationKind.Relate => "relate",
        ExternalTaskOperationKind.Transition => "transition",
        _ => throw new AtcException("unsupported_operation")
    };
    private static ExternalTaskOperationKind WorkOperation(string operation) => operation switch
    {
        "read" or "list" => ExternalTaskOperationKind.Read,
        "update" => ExternalTaskOperationKind.Update,
        "create" => ExternalTaskOperationKind.Publish,
        "comment" => ExternalTaskOperationKind.Comment,
        "relate" => ExternalTaskOperationKind.Relate,
        "transition" => ExternalTaskOperationKind.Transition,
        _ => throw new AtcException("unsupported_operation")
    };
    private static WorkTrackingRelationKind RelationKind(AtcRelationKind value) => Enum.Parse<WorkTrackingRelationKind>(value.ToString());
    private static AtcRelationKind RelationKind(WorkTrackingRelationKind value) => Enum.Parse<AtcRelationKind>(value.ToString());
    private static WorkTrackingConcurrency Concurrency(AtcConcurrency value) => Enum.Parse<WorkTrackingConcurrency>(value.ToString());
    private static WorkTrackingRecovery Recovery(AtcRecoveryScope value) => Enum.Parse<WorkTrackingRecovery>(value.ToString());
    private static AtcCapabilities Capabilities(WorkTaskProviderRoute route)
    {
        var caps = route.Tracking!.Capabilities;
        return new(caps.Operations, caps.UpdateFields, caps.RelationKinds.Select(RelationKind).ToArray(),
            Enum.Parse<AtcConcurrency>(caps.Concurrency.ToString()), Enum.Parse<AtcRecoveryScope>(caps.Recovery.ToString()),
            caps.ReceiptWindowSeconds, new(caps.MaximumPageSize, caps.MaximumResultBytes));
    }
    private static ExternalTaskSnapshot Snapshot(AtcItem item) => new("", item.Identity.ItemId, item.Revision, item.Title,
        item.Body, item.State.Id, null, item.Classifications, [])
    {
        CompletionReason = item.State.CompletionReason,
        Tracking = new(new(item.Identity.ProviderId, item.Identity.TargetId, item.Identity.ItemId), item.RelationsComplete,
            item.Relations.Select(relation => new WorkTrackingRelation(RelationKind(relation.Kind),
                new(relation.Item.ProviderId, relation.Item.TargetId, relation.Item.ItemId))).ToArray(),
            item.Comments.Select(comment => new WorkTrackingComment(comment.Id, comment.Text)).ToArray(),
            item.Transitions.Select(transition => new WorkTrackingTransition(transition.Id, transition.DestinationState)).ToArray())
    };
    private static bool Expected(Exception error) => error is AtcException or JsonException or ToolExecutionDeniedException
        or ToolInputValidationException or ToolResolutionException or ToolDefinitionInvocationException or McpException or HttpRequestException;
    private static Result<WorkTaskProviderRoute> Failure(string code) => Result<WorkTaskProviderRoute>.Failure(code);
}
