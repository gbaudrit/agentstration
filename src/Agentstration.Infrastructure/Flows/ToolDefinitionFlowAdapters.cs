using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Tools;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Flows;

public sealed class ToolDefinitionFlowResolver(FlowService flows) : IToolDefinitionFlowResolver
{
    public async Task<ResolvedToolDefinitionFlowContract> ResolveAsync(
        ResourceScopeRef scopeRef,
        ResourceNamespace ownerNamespace,
        ToolDefinitionFlowTarget target,
        CancellationToken cancellationToken)
    {
        var flowNamespace = target.Namespace ?? ownerNamespace;
        var reference = new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace, target.ScopeRef);
        try
        {
            var resolved = await flows.ResolveAsync(scopeRef, reference, ownerNamespace, cancellationToken);
            return Contract(resolved);
        }
        catch (FlowReferenceOutsideScopeException)
        {
            throw new ResourceReferenceOutsideScopeException(new(target.Name, target.ScopeRef, flowNamespace), scopeRef);
        }
    }

    internal static ResolvedToolDefinitionFlowContract Contract(FlowVersion version)
    {
        var ambiguous = false;
        var output = version.Graph?.ResolveSingleSuccessOutputSchema(out ambiguous);
        if (ambiguous)
            throw new ToolDefinitionValidationException(
                "tool_definition_flow_output_schema_ambiguous",
                $"Flow version '{version.FlowId}:{version.Version}' exposes success outputs with different contracts.");
        return new(
            version.FlowId.Value,
            version.FlowId.Namespace,
            version.Version,
            version.Graph?.ResolveInputSchema()?.Clone(),
            output?.Clone())
        {
            ScopeRef = version.ScopeRef
        };
    }
}

public sealed class ToolDefinitionFlowActivationGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowVersionActivationGuard
{
    public Task ValidateActivationAsync(WorkspaceId workspaceId, FlowVersion version, CancellationToken cancellationToken) =>
        ValidateActivationAsync(ResourceScopeRef.Workspace(workspaceId.Value), version, cancellationToken);

    public async Task ValidateActivationAsync(ResourceScopeRef ownerScopeRef, FlowVersion version, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var drafts = await store.ListAllAsync<ToolDefinitionResource>(ToolResourceKinds.ToolDefinition, cancellationToken);
        var publications = await store.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken);
        var definitions = drafts.Concat(publications.Select(value => new StoredResource<ToolDefinitionResource>(value.Value.ToDefinition(), value.ETag, value.UpdatedAt)));
        var matchingDefinitions = definitions.Where(value =>
                     value.Value.Definition is FlowToolDefinitionProperties
                     && (((FlowToolDefinitionProperties)value.Value.Definition).Flow.ScopeRef ?? value.Value.ScopeRef) == ownerScopeRef
                     && value.Value.Definition.Enabled
                     && ((FlowToolDefinitionProperties)value.Value.Definition).Flow.UseActiveVersion
                     && string.Equals(((FlowToolDefinitionProperties)value.Value.Definition).Flow.Name, version.FlowId.Value, StringComparison.Ordinal)
                     && (((FlowToolDefinitionProperties)value.Value.Definition).Flow.Namespace ?? value.Value.Namespace) == version.FlowId.Namespace)
            .ToArray();
        if (matchingDefinitions.Length == 0) return;

        ResolvedToolDefinitionFlowContract contract;
        try { contract = ToolDefinitionFlowResolver.Contract(version); }
        catch (ToolDefinitionValidationException exception)
        {
            throw new FlowValidationException("flow_tool_definition_contract_incompatible",
                $"Flow version '{version.FlowId}:{version.Version}' is incompatible with enabled ToolDefinition '{matchingDefinitions[0].Value.Address}': {exception.Message}");
        }

        foreach (var stored in matchingDefinitions)
        {
            try { ToolDefinitionService.ValidateContract(stored.Value.Definition, contract); }
            catch (ToolDefinitionValidationException exception)
            {
                throw new FlowValidationException("flow_tool_definition_contract_incompatible", $"Flow version '{version.FlowId}:{version.Version}' is incompatible with enabled ToolDefinition '{stored.Value.Address}': {exception.Message}");
            }
        }
    }
}

public sealed class ToolDefinitionFlowDeletionGuard(
    IResourceStore store,
    IRequestContextScopeFactory requestScopes) : IFlowDeletionGuard
{
    public Task ValidateDeleteAsync(WorkspaceId workspaceId, FlowId flowId, CancellationToken cancellationToken) =>
        ValidateDeleteAsync(ResourceScopeRef.Workspace(workspaceId.Value), flowId, cancellationToken);

    public async Task ValidateDeleteAsync(ResourceScopeRef ownerScopeRef, FlowId flowId, CancellationToken cancellationToken)
    {
        using var requestScope = requestScopes.PushSystem();
        var drafts = await store.ListAllAsync<ToolDefinitionResource>(ToolResourceKinds.ToolDefinition, cancellationToken);
        var publications = await store.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken);
        var definitions = drafts.Concat(publications.Select(value => new StoredResource<ToolDefinitionResource>(value.Value.ToDefinition(), value.ETag, value.UpdatedAt)));
        var usage = definitions.Select(value => value.Value).FirstOrDefault(value =>
            value.Definition is FlowToolDefinitionProperties
            && (((FlowToolDefinitionProperties)value.Definition).Flow.ScopeRef ?? value.ScopeRef) == ownerScopeRef
            && string.Equals(((FlowToolDefinitionProperties)value.Definition).Flow.Name, flowId.Value, StringComparison.Ordinal)
            && (((FlowToolDefinitionProperties)value.Definition).Flow.Namespace ?? value.Namespace) == flowId.Namespace);
        if (usage is not null)
            throw new FlowValidationException("flow_in_use_by_tool_definition", $"Flow '{flowId}' is referenced by ToolDefinition '{usage.Address}'.");
    }
}

public sealed class ToolDefinitionExecutor(
    ToolDefinitionService definitions,
    IToolDefinitionFlowResolver flowResolver,
    RootFlowSubmissionService submissions,
    FlowRunService runs,
    KnowledgeRetrievalService? knowledgeRetrieval = null,
    ToolDefinitionAcquisitionExecutor? acquisitions = null,
    ToolRelayExecutor? relays = null,
    IEnumerable<IWorkExecutionScopeAccessor>? executionScopeAccessors = null,
    IResourceReferenceResolver? references = null,
    Agentstration.Secrets.Abstractions.ISecretInputResolver? secretInputs = null) : IToolDefinitionExecutor
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonElement?> SimulateAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        var stored = await ValidateAsync(invocation, cancellationToken);
        if (stored.Value.Definition is RelayToolDefinitionProperties relay)
            return (await (relays ?? throw new ToolDefinitionInvocationException("tool_definition_relay_unavailable", "Relay execution is unavailable."))
                .SimulateAsync(stored.Value, relay, invocation, cancellationToken)).Output;
        if (stored.Value.Definition is FlowToolDefinitionProperties flow)
        {
            var contract = await flowResolver.ResolveAsync(stored.Value.ScopeRef!.Value, stored.Value.Namespace, flow.Flow, cancellationToken);
            var input = MergeArguments(invocation.Arguments, stored.Value.Definition.FixedArguments);
            using var resolved = secretInputs is null ? null : await secretInputs.ResolveAsync(input,
                invocation.ExecutionContext?.SecretInputs ?? [], cancellationToken);
            ToolInputSchemaValidator.Validate(contract.InputSchema, resolved?.Value ?? input, invocation.ExecutionContext?.SecretInputs);
        }
        return JsonSerializer.SerializeToElement(new
        {
            dryRun = true,
            toolDefinition = stored.Value.Address.ToString(),
            inputAccepted = true
        });
    }

    public async Task<ToolDefinitionInvocationResult> ExecuteAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        var stored = await ValidateAsync(invocation, cancellationToken);

        if (stored.Value.Definition is AcquisitionToolDefinitionProperties { Acquisition: var acquisition })
        {
            if (acquisitions is null)
                throw new ToolDefinitionInvocationException("tool_definition_acquisition_unavailable",
                    "Acquisition Tools are unavailable.");
            return await acquisitions.ExecuteAsync(stored.Value, acquisition, invocation, cancellationToken);
        }

        if (stored.Value.Definition is RelayToolDefinitionProperties relay)
            return await (relays ?? throw new ToolDefinitionInvocationException("tool_definition_relay_unavailable", "Relay execution is unavailable."))
                .ExecuteAsync(stored.Value, relay, invocation, cancellationToken);

        if (knowledgeRetrieval is not null
            && stored.Value.Metadata.Annotations.TryGetValue("agentstration.io/knowledge-source", out var sourceName)
            && stored.Value.Metadata.Annotations.TryGetValue("agentstration.io/knowledge-operation", out var operationName)
            && Enum.TryParse<KnowledgeSourceOperation>(operationName, true, out var operation))
            return await ExecuteKnowledgeRetrievalAsync(stored.Value, invocation, sourceName, operation,
                knowledgeRetrieval, secretInputs, cancellationToken);

        if (invocation.ExecutionContext?.TransientPayloads == true)
            throw new ToolDefinitionInvocationException("direct_work_item_payload_unsupported",
                "Direct payloads cannot enter a durable Work Item; compose the child Flow directly.");
        var flowInput = MergeArguments(invocation.Arguments, stored.Value.Definition.FixedArguments);
        var references = invocation.ExecutionContext?.SecretInputs ?? [];
        if (invocation.ExecutionContext?.HasSensitiveArguments == true && references.Count == 0 && invocation.ExecutionContext.TransientPayloads != true)
            throw new ToolDefinitionInvocationException("tool_definition_protected_input_reference_required",
                "Protected inputs require durable Secret references.");
        var flowContract = await flowResolver.ResolveAsync(stored.Value.ScopeRef!.Value, stored.Value.Namespace,
            ((FlowToolDefinitionProperties)stored.Value.Definition).Flow, cancellationToken);
        try { FlowRunService.ValidateInput(flowContract.InputSchema, flowInput, references); }
        catch (FlowValidationException exception)
        {
            throw new ToolDefinitionInvocationException("tool_definition_fixed_input_invalid", exception.Message, exception);
        }

        var target = ((FlowToolDefinitionProperties)stored.Value.Definition).Flow;
        var flowNamespace = target.Namespace ?? stored.Value.Namespace;
        var scope = ExecutionScope(invocation);
        var origin = invocation.CallerKind switch
        {
            ToolDefinitionCallerKind.Agent => FlowInvocationOrigin.Agent,
            ToolDefinitionCallerKind.Console => FlowInvocationOrigin.Console,
            _ => FlowInvocationOrigin.Mcp
        };
        var callerId = invocation.CallerId ?? invocation.PrincipalId.ToString("D");
        RootFlowSubmission submission;
        try
        {
            submission = await submissions.SubmitAsync(new SubmitRootFlowCommand(
                invocation.WorkspaceId,
                new FlowReference(new FlowId(target.Name, flowNamespace), target.Version, target.UseActiveVersion, flowNamespace, target.ScopeRef),
                flowInput,
                origin,
                callerId,
                invocation.CallerKind == ToolDefinitionCallerKind.Flow ? FlowRunTrigger.Flow : FlowRunTrigger.Api,
                $"tool:{stored.Value.Uid:N}:{stored.Value.Definition.Version}:{invocation.CallId}",
                invocation.CallId,
                invocation.CorrelationId,
                Type: "tool-definition",
                Instruction: $"Execute ToolDefinition '{stored.Value.Address}'.",
                Title: stored.Value.Definition.DisplayName,
                Metadata: new Dictionary<string, string>
                {
                    ["toolDefinition.uid"] = stored.Value.Uid.ToString("D"),
                    ["toolDefinition.name"] = stored.Value.Name,
                    ["toolDefinition.namespace"] = stored.Value.Namespace.Value,
                    ["toolDefinition.callId"] = invocation.CallId,
                    ["toolDefinition.version"] = stored.Value.Definition.Version
                })
            { SecretInputs = references }, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ToolDefinitionInvocationException("tool_definition_submission_failed", exception.Message, exception);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(stored.Value.Definition.InvocationTimeoutSeconds));
        StoredFlowRun completed;
        try
        {
            completed = await AwaitCompletionAsync(submission.FlowRun.Run.Id, scope, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolDefinitionInvocationException("tool_definition_timed_out", $"ToolDefinition '{stored.Value.Address}' exceeded its execution timeout.");
        }

        if (completed.Value.Status != FlowRunStatus.Succeeded)
            throw new ToolDefinitionInvocationException(
                completed.Value.Status == FlowRunStatus.WaitingForInput ? "tool_definition_input_required" : "tool_definition_execution_failed",
                completed.Value.Error?.Message ?? $"ToolDefinition Flow Run ended with status '{completed.Value.Status}'.");
        var output = completed.Value.Output?.Clone();
        if (output is null && stored.Value.Definition.OutputSchema is not null)
            throw new ToolDefinitionInvocationException("tool_definition_output_invalid", "The Flow returned no output for a ToolDefinition with an output schema.");
        if (output is not null)
        {
            try { FlowRunService.ValidateInput(stored.Value.Definition.OutputSchema, output.Value, references); }
            catch (FlowValidationException exception)
            {
                throw new ToolDefinitionInvocationException("tool_definition_output_invalid", exception.Message, exception);
            }
        }

        return new(output, new ToolDefinitionOperationReceipt(
            submission.WorkItem.Value.Id.Value.ToString("D"),
            completed.Value.Id,
            completed.Value.FlowId.Value,
            completed.Value.FlowId.Namespace,
            completed.Value.FlowVersion,
            completed.Value.CorrelationId ?? string.Empty,
            submission.Recovered));
    }

    private static async Task<ToolDefinitionInvocationResult> ExecuteKnowledgeRetrievalAsync(
        ToolDefinitionResource definition,
        ToolDefinitionInvocation invocation,
        string sourceName,
        KnowledgeSourceOperation operation,
        KnowledgeRetrievalService retrieval,
        Agentstration.Secrets.Abstractions.ISecretInputResolver? secretInputs,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(definition.Definition.InvocationTimeoutSeconds));
        try
        {
            if (!invocation.Arguments.TryGetProperty("request", out var request)
                || request.ValueKind != JsonValueKind.Object)
                throw new ToolDefinitionInvocationException("knowledge_retrieval_request_invalid",
                    "A Knowledge retrieval request object is required.");
            var symbolicRequest = request.Clone();
            using var resolvedInputs = secretInputs is null ? null : await secretInputs.ResolveAsync(invocation.Arguments,
                invocation.ExecutionContext?.SecretInputs ?? [], timeout.Token);
            if (resolvedInputs is not null) request = resolvedInputs.Value.GetProperty("request");
            var origin = invocation.CallerKind switch
            {
                ToolDefinitionCallerKind.Agent => KnowledgeRetrievalInvocationOrigin.Agent,
                ToolDefinitionCallerKind.Flow => KnowledgeRetrievalInvocationOrigin.Flow,
                _ => KnowledgeRetrievalInvocationOrigin.Mcp
            };
            var sourceScope = definition.Metadata.Annotations.TryGetValue(
                "agentstration.io/knowledge-source-scope", out var serializedScope)
                ? ResourceScopeRef.Parse(serializedScope)
                : (ResourceScopeRef?)null;
            var sourceId = new KnowledgeSourceId(sourceName, definition.Namespace, sourceScope);
            var executionContext = new KnowledgeRetrievalExecutionContext(
                invocation.ExecutionContext?.AgentId,
                invocation.ExecutionContext?.AgentRevisionId,
                invocation.ExecutionContext?.RuntimeRunId,
                invocation.ExecutionContext?.FlowRunId,
                invocation.ExecutionContext?.FlowStepId,
                invocation.CallId,
                invocation.ExecutionContext?.InvocationId)
            {
                SecretInputs = invocation.ExecutionContext?.SecretInputs ?? [],
                TransientPayloads = invocation.ExecutionContext?.TransientPayloads == true,
                SymbolicRequest = symbolicRequest,
                ProtectState = value => resolvedInputs?.Protect(value) ?? value
            };
            var result = operation switch
            {
                KnowledgeSourceOperation.Search => await retrieval.SearchAsync(sourceId,
                    request.Deserialize<SearchKnowledgeRequest>(WebJsonOptions)
                    ?? throw new JsonException("Search request was empty."), timeout.Token, origin, invocation.CallerId,
                    executionContext),
                KnowledgeSourceOperation.Query => await retrieval.QueryAsync(sourceId,
                    request.Deserialize<QueryKnowledgeRequest>(WebJsonOptions)
                    ?? throw new JsonException("Query request was empty."), timeout.Token, origin, invocation.CallerId,
                    executionContext),
                KnowledgeSourceOperation.Read => await retrieval.ReadAsync(sourceId,
                    request.Deserialize<ReadKnowledgeRequest>(WebJsonOptions)
                    ?? throw new JsonException("Read request was empty."), timeout.Token, origin, invocation.CallerId,
                    executionContext),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            var output = JsonSerializer.SerializeToElement(new KnowledgeRetrievalFlowOutput
            {
                Items = result.Items,
                Citations = result.Citations,
                Answer = result.Answer,
                ContinuationToken = result.ContinuationToken
            }, WebJsonOptions);
            return new(output, new ToolDefinitionOperationReceipt(
                $"knowledge-retrieval:{result.FlowRunId}",
                result.FlowRunId,
                result.RetrievalFlow.Name,
                result.RetrievalFlow.Namespace,
                result.RetrievalFlow.Version,
                result.CorrelationId,
                false,
                new ToolDefinitionKnowledgeReceipt(
                    result.KnowledgeSourceId,
                    result.KnowledgeSourceUid,
                    result.KnowledgeSourceGeneration,
                    result.SnapshotName,
                    result.SnapshotUid,
                    result.Items.Select(value => value.ArtifactId)
                        .Concat(result.Citations.Select(value => value.ArtifactId))
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray())));
        }
        catch (ToolDefinitionInvocationException) { throw; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolDefinitionInvocationException("tool_definition_timed_out",
                $"ToolDefinition '{definition.Address}' exceeded its execution timeout.");
        }
        catch (KnowledgeRetrievalException exception)
        {
            throw new ToolDefinitionInvocationException(exception.Code, exception.Message, exception);
        }
        catch (JsonException exception)
        {
            throw new ToolDefinitionInvocationException("knowledge_retrieval_request_invalid",
                $"The Knowledge retrieval request is invalid: {exception.Message}", exception);
        }
    }

    private static JsonElement MergeArguments(JsonElement arguments, JsonElement? fixedArguments)
    {
        if (fixedArguments is null) return arguments.Clone();
        var merged = JsonNode.Parse(arguments.GetRawText())?.AsObject()
            ?? throw new ToolDefinitionInvocationException("tool_definition_input_invalid", "ToolDefinition input must be an object.");
        foreach (var property in fixedArguments.Value.EnumerateObject())
            merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return JsonSerializer.SerializeToElement(merged);
    }

    private async Task<StoredResource<ToolDefinitionResource>> ValidateAsync(ToolDefinitionInvocation invocation, CancellationToken cancellationToken)
    {
        var scope = ExecutionScope(invocation);
        if (invocation.ExecutionContext is { HasSensitiveArguments: true, SecretInputs.Count: 0, TransientPayloads: false })
            throw new ToolDefinitionInvocationException("tool_definition_protected_input_reference_required",
                "Protected inputs require durable Secret references.");
        var stored = await definitions.GetInvocationAsync(invocation.ToolName, invocation.Namespace, cancellationToken, invocation.DefinitionScopeRef)
            ?? (acquisitions is null ? null
                : await acquisitions.FindInOperationalWorkspaceAsync(definitions, invocation, cancellationToken))
            ?? throw new ToolDefinitionInvocationException("tool_definition_not_found", $"ToolDefinition '{invocation.Namespace}/{invocation.ToolName}' was not found.");
        if (!stored.Value.Definition.Enabled)
            throw new ToolDefinitionInvocationException("tool_definition_disabled", $"ToolDefinition '{stored.Value.Address}' is disabled.");
        if (stored.Value.ScopeRef != scope.AccessScopeRef
            && acquisitions?.OwnsAdministrativeInvocation(stored.Value, invocation) != true)
        {
            try
            {
                if (references is null || await references.ResolveAsync<ToolDefinitionVersionResource>(
                    new(invocation.ToolName, stored.Value.ScopeRef, invocation.Namespace), invocation.Namespace,
                    ToolResourceKinds.ToolDefinitionVersion, scope.AccessScopeRef, cancellationToken) is null)
                    throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch", "The ToolDefinition is outside the logical invocation scope.");
            }
            catch (ResourceReferenceOutsideScopeException exception)
            {
                throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch", "The ToolDefinition is outside the logical invocation scope.", exception);
            }
        }
        if (invocation.Arguments.GetRawText().Length > 65_536)
            throw new ToolDefinitionInvocationException("tool_definition_input_too_large", "ToolDefinition input cannot exceed 65536 JSON characters.");
        if (stored.Value.Definition is AcquisitionToolDefinitionProperties)
        {
            try { ToolInputSchemaValidator.Validate(stored.Value.Definition.InputSchema, invocation.Arguments); }
            catch (ToolInputValidationException exception)
            {
                throw new ToolDefinitionInvocationException("tool_definition_input_invalid", exception.Message, exception);
            }
            return stored;
        }
        if (stored.Value.Definition is RelayToolDefinitionProperties relay)
        {
            try { ToolRelayArguments.ValidateCaller(invocation.Arguments, relay.Bindings); }
            catch (ToolDefinitionValidationException exception) { throw new ToolDefinitionInvocationException(exception.Code, exception.Message); }
            ToolInputSchemaValidator.Validate(relay.InputSchema, invocation.Arguments, invocation.ExecutionContext?.SecretInputs);
            return stored;
        }
        if (stored.Value.Definition.FixedArguments is { ValueKind: JsonValueKind.Object } fixedArguments)
            foreach (var property in fixedArguments.EnumerateObject())
                if (invocation.Arguments.TryGetProperty(property.Name, out _))
                    throw new ToolDefinitionInvocationException("tool_definition_fixed_argument_override",
                        $"ToolDefinition argument '{property.Name}' is fixed and cannot be supplied by the caller.");
        try
        {
            FlowRunService.ValidateInput(stored.Value.Definition.InputSchema, invocation.Arguments, invocation.ExecutionContext?.SecretInputs);
        }
        catch (FlowValidationException exception)
        {
            throw new ToolDefinitionInvocationException("tool_definition_input_invalid", exception.Message, exception);
        }

        return stored;
    }

    private FlowRunScope ExecutionScope(ToolDefinitionInvocation invocation)
    {
        var candidates = (executionScopeAccessors ?? []).Select(accessor => accessor.Current).OfType<FlowRunScope>()
            .Where(scope => scope.PrincipalId == invocation.PrincipalId && scope.TenantId == invocation.TenantId && scope.WorkspaceId == invocation.WorkspaceId).ToArray();
        return candidates.FirstOrDefault(scope => scope.LogicalScopeRef is not null) ?? candidates.FirstOrDefault()
            ?? throw new ToolDefinitionInvocationException("tool_definition_scope_mismatch", "A ToolDefinition invocation requires its trusted execution scope.");
    }

    private async Task<StoredFlowRun> AwaitCompletionAsync(string runId, FlowRunScope scope, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = await runs.GetAsync(runId, scope, cancellationToken)
                ?? throw new ToolDefinitionInvocationException("tool_definition_flow_run_missing", "The ToolDefinition Flow Run could not be reloaded.");
            if (stored.Value.Status.IsTerminal() || stored.Value.Status == FlowRunStatus.WaitingForInput) return stored;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }
}
