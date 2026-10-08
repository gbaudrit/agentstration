using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;

namespace Agentstration.Knowledge;

public sealed class KnowledgeRetrievalException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public enum KnowledgeRetrievalInvocationOrigin { Api, Tool, Mcp, Flow, Agent }

public sealed record KnowledgeRetrievalExecutionContext(
    string? AgentId = null,
    string? AgentRevisionId = null,
    string? RuntimeRunId = null,
    string? FlowRunId = null,
    string? FlowStepId = null,
    string? ToolCallId = null,
    string? ToolInvocationId = null);

public sealed record KnowledgeRetrievalFlowRequest(
    Guid TenantId,
    Guid WorkspaceId,
    Guid PrincipalId,
    ResolvedKnowledgeFlowBinding Flow,
    string RunId,
    string CallerId,
    string CorrelationId,
    KnowledgeRetrievalInvocationOrigin Origin,
    JsonElement Input,
    KnowledgeRetrievalExecutionContext? ExecutionContext = null);

public sealed record KnowledgeRetrievalFlowResult(
    string RunId,
    JsonElement Output);

public interface IKnowledgeRetrievalFlowGateway
{
    Task<KnowledgeRetrievalFlowResult> ExecuteAsync(
        KnowledgeRetrievalFlowRequest request,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeRetrievalService(
    KnowledgeSourceManagementService sources,
    KnowledgeSnapshotService snapshots,
    IKnowledgeRetrievalFlowGateway flowRuns,
    ICurrentRequestContext requestContext,
    IAuthorizationService authorization,
    ISecurityAuditWriter audit)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public const int MaximumSearchQueryCharacters = 4_000;
    public const int MaximumQuestionCharacters = 16_000;
    public const int MaximumFilters = 20;
    public const int MaximumResults = 100;
    public const int MaximumOutputCharacters = 32_768;
    public const int MaximumReadBytes = 1_048_576;
    public const int MaximumContinuationCharacters = 4_096;
    public const int MaximumCitations = 200;

    public Task<KnowledgeRetrievalResult> SearchAsync(
        KnowledgeSourceId sourceId,
        SearchKnowledgeRequest request,
        CancellationToken cancellationToken,
        KnowledgeRetrievalInvocationOrigin origin = KnowledgeRetrievalInvocationOrigin.Api,
        string? callerId = null,
        KnowledgeRetrievalExecutionContext? executionContext = null)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > MaximumSearchQueryCharacters)
            throw Error("knowledge_search_query_invalid",
                $"A search query of at most {MaximumSearchQueryCharacters} characters is required.");
        if (request.Limit is < 1 or > MaximumResults)
            throw Error("knowledge_search_limit_invalid", $"Search limit must be between 1 and {MaximumResults}.");
        if (request.Filters.Count > MaximumFilters
            || request.Filters.Any(value => string.IsNullOrWhiteSpace(value.Key) || value.Key.Length > 200
                || value.Value is null || value.Value.Length > 1_000))
            throw Error("knowledge_search_filters_invalid",
                $"Search accepts at most {MaximumFilters} bounded filters.");
        ValidateContinuation(request.ContinuationToken);
        var payload = JsonSerializer.SerializeToElement(new
        {
            query = request.Query.Trim(),
            filters = request.Filters,
            limit = request.Limit,
            continuationToken = request.ContinuationToken
        }, JsonOptions);
        return ExecuteAsync(sourceId, KnowledgeSourceOperation.Search, payload, request.SnapshotName,
            request.CorrelationId, request.Limit, null, cancellationToken, origin, callerId, executionContext);
    }

    public Task<KnowledgeRetrievalResult> QueryAsync(
        KnowledgeSourceId sourceId,
        QueryKnowledgeRequest request,
        CancellationToken cancellationToken,
        KnowledgeRetrievalInvocationOrigin origin = KnowledgeRetrievalInvocationOrigin.Api,
        string? callerId = null,
        KnowledgeRetrievalExecutionContext? executionContext = null)
    {
        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > MaximumQuestionCharacters)
            throw Error("knowledge_query_question_invalid",
                $"A question of at most {MaximumQuestionCharacters} characters is required.");
        if (request.MaximumItems is < 1 or > MaximumResults)
            throw Error("knowledge_query_item_limit_invalid", $"Query item limit must be between 1 and {MaximumResults}.");
        if (request.MaximumOutputCharacters is < 1 or > MaximumOutputCharacters)
            throw Error("knowledge_query_output_limit_invalid",
                $"Query output limit must be between 1 and {MaximumOutputCharacters} characters.");
        var payload = JsonSerializer.SerializeToElement(new
        {
            question = request.Question.Trim(),
            maximumItems = request.MaximumItems,
            maximumOutputCharacters = request.MaximumOutputCharacters
        }, JsonOptions);
        return ExecuteAsync(sourceId, KnowledgeSourceOperation.Query, payload, request.SnapshotName,
            request.CorrelationId, request.MaximumItems, null, cancellationToken, origin, callerId, executionContext);
    }

    public Task<KnowledgeRetrievalResult> ReadAsync(
        KnowledgeSourceId sourceId,
        ReadKnowledgeRequest request,
        CancellationToken cancellationToken,
        KnowledgeRetrievalInvocationOrigin origin = KnowledgeRetrievalInvocationOrigin.Api,
        string? callerId = null,
        KnowledgeRetrievalExecutionContext? executionContext = null)
    {
        if (string.IsNullOrWhiteSpace(request.ArtifactId) || request.ArtifactId.Length > 200)
            throw Error("knowledge_read_artifact_invalid", "A bounded artifact identity is required.");
        if (request.Offset < 0 || request.Length is < 1 or > MaximumReadBytes)
            throw Error("knowledge_read_range_invalid",
                $"Read offset must be non-negative and length must be between 1 and {MaximumReadBytes} bytes.");
        var payload = JsonSerializer.SerializeToElement(new
        {
            artifactId = request.ArtifactId.Trim(),
            offset = request.Offset,
            length = request.Length
        }, JsonOptions);
        return ExecuteAsync(sourceId, KnowledgeSourceOperation.Read, payload, request.SnapshotName,
            request.CorrelationId, 1, request.ArtifactId.Trim(), cancellationToken, origin, callerId, executionContext);
    }

    public Task<KnowledgeRetrievalResult> ExecuteAsync(
        KnowledgeSourceId sourceId,
        KnowledgeSourceOperation operation,
        JsonElement request,
        string? snapshotName,
        string? correlationId,
        int maximumItems,
        string? requiredArtifactId,
        CancellationToken cancellationToken,
        KnowledgeRetrievalInvocationOrigin origin = KnowledgeRetrievalInvocationOrigin.Api,
        string? callerId = null,
        KnowledgeRetrievalExecutionContext? executionContext = null) => ExecuteCoreAsync(sourceId, operation, request, snapshotName, correlationId,
            maximumItems, requiredArtifactId, origin, callerId, executionContext, cancellationToken);

    private async Task<KnowledgeRetrievalResult> ExecuteCoreAsync(
        KnowledgeSourceId sourceId,
        KnowledgeSourceOperation operation,
        JsonElement request,
        string? snapshotName,
        string? correlationId,
        int maximumItems,
        string? requiredArtifactId,
        KnowledgeRetrievalInvocationOrigin origin,
        string? callerId,
        KnowledgeRetrievalExecutionContext? executionContext,
        CancellationToken cancellationToken)
    {
        var context = RequireContext();
        await authorization.EnsurePermissionAsync(context, AuthorizationPermissions.RunsExecute, cancellationToken);
        try
        {
            var source = await sources.GetAsync(sourceId, cancellationToken)
                ?? throw Error("knowledge_retrieval_source_not_found", $"KnowledgeSource '{sourceId}' was not found.");
            if (!source.Value.Definition.Enabled)
                throw Error("knowledge_retrieval_source_disabled", $"KnowledgeSource '{source.Value.Address}' is disabled.");
            var scopeRef = source.Value.ScopeRef
                ?? throw Error("knowledge_retrieval_source_scope_invalid", "The KnowledgeSource has no Workspace scope.");
            if (scopeRef != ResourceScopeRef.Workspace(context.WorkspaceId))
                throw Error("knowledge_retrieval_source_scope_mismatch",
                    "The KnowledgeSource is not owned by the current Workspace.");
            KnowledgeSnapshotResource snapshot;
            try { snapshot = await snapshots.ResolveForRetrievalAsync(sourceId, snapshotName, cancellationToken); }
            catch (KnowledgeSnapshotException exception)
            {
                throw Error(exception.Code, exception.Message, exception);
            }
            ResolvedKnowledgeFlowBinding flow;
            var profile = snapshot.Profile;
            try
            {
                flow = snapshot.RetrievalFlow
                    ?? profile?.RetrievalFlow
                    ?? (await sources.ResolveCompositionAsync(source.Value, cancellationToken)).Retrieval;
            }
            catch (Exception exception) when (exception is KnowledgeSourceValidationException or KnowledgeSourceProfileValidationException)
            {
                throw Error("knowledge_retrieval_flow_unavailable", exception.Message, exception);
            }
            ValidateFlow(flow, operation);
            var artifactIds = snapshot.Artifacts.Select(value => value.ArtifactId).ToHashSet(StringComparer.Ordinal);
            if (requiredArtifactId is not null && !artifactIds.Contains(requiredArtifactId))
                throw Error("knowledge_read_artifact_outside_snapshot",
                    $"Artifact '{requiredArtifactId}' does not belong to Knowledge Snapshot '{snapshot.Name}'.");
            if (requiredArtifactId is not null)
            {
                var artifact = snapshot.Artifacts.Single(value => value.ArtifactId == requiredArtifactId);
                var offset = request.GetProperty("offset").GetInt64();
                if (offset > artifact.Length || request.GetProperty("length").GetInt64() > artifact.Length - offset)
                    throw Error("knowledge_read_range_outside_artifact",
                        "The requested range exceeds the selected Snapshot artifact length.");
            }

            var retrievalId = $"retrieval-{Guid.NewGuid():N}";
            var effectiveCorrelationId = string.IsNullOrWhiteSpace(correlationId) ? retrievalId : correlationId.Trim();
            if (effectiveCorrelationId.Length > 200)
                throw Error("knowledge_retrieval_correlation_invalid", "Correlation identifiers cannot exceed 200 characters.");
            var input = JsonSerializer.SerializeToElement(new KnowledgeRetrievalInput
            {
                KnowledgeSourceId = ToolResourceIdentity.CatalogId(source.Value.Namespace, source.Value.Name),
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceGeneration = source.Value.Generation,
                Profile = profile,
                Operation = operation.ToString().ToLowerInvariant(),
                Snapshot = new()
                {
                    Name = snapshot.Name,
                    Uid = snapshot.Uid,
                    Artifacts = snapshot.Artifacts
                },
                Request = request.Clone(),
                Caller = new()
                {
                    PrincipalId = context.PrincipalId,
                    TenantId = context.TenantId,
                    WorkspaceId = context.WorkspaceId,
                    AgentId = executionContext?.AgentId,
                    AgentRevisionId = executionContext?.AgentRevisionId,
                    RuntimeRunId = executionContext?.RuntimeRunId,
                    FlowRunId = executionContext?.FlowRunId,
                    FlowStepId = executionContext?.FlowStepId,
                    ToolCallId = executionContext?.ToolCallId,
                    ToolInvocationId = executionContext?.ToolInvocationId
                },
                CorrelationId = effectiveCorrelationId,
                RetrievalId = retrievalId
            }, JsonOptions);
            KnowledgeRetrievalFlowResult completed;
            try
            {
                completed = await flowRuns.ExecuteAsync(new(
                    context.TenantId,
                    context.WorkspaceId,
                    context.PrincipalId,
                    flow,
                    retrievalId,
                    callerId ?? context.PrincipalId.ToString("D"),
                    effectiveCorrelationId,
                    origin,
                    input,
                    executionContext), cancellationToken);
            }
            catch (KnowledgeRetrievalException) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Error("knowledge_retrieval_execution_failed", exception.Message, exception);
            }

            KnowledgeRetrievalFlowOutput output;
            try
            {
                output = completed.Output.Deserialize<KnowledgeRetrievalFlowOutput>(JsonOptions)
                    ?? throw new JsonException("The Flow output was empty.");
            }
            catch (JsonException exception)
            {
                throw Error("knowledge_retrieval_output_invalid",
                    $"The retrieval Flow returned an invalid output: {exception.Message}", exception);
            }
            ValidateOutput(operation, output, artifactIds, maximumItems, requiredArtifactId);
            var result = new KnowledgeRetrievalResult
            {
                Operation = operation,
                KnowledgeSourceId = ToolResourceIdentity.CatalogId(source.Value.Namespace, source.Value.Name),
                KnowledgeSourceUid = source.Value.Uid,
                KnowledgeSourceGeneration = source.Value.Generation,
                SnapshotName = snapshot.Name,
                SnapshotUid = snapshot.Uid,
                RetrievalFlow = flow,
                Profile = profile,
                FlowRunId = completed.RunId,
                CorrelationId = effectiveCorrelationId,
                Items = output.Items ?? [],
                Citations = output.Citations ?? [],
                Answer = output.Answer,
                ContinuationToken = output.ContinuationToken
            };
            await audit.WriteAsync(new(SecurityAuditActions.KnowledgeRetrievalCompleted,
                ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                WorkspaceId: context.WorkspaceId, ReasonCode: operation.ToString()), cancellationToken);
            return result;
        }
        catch (KnowledgeRetrievalException exception)
        {
            await audit.WriteAsync(new(SecurityAuditActions.KnowledgeRetrievalFailed,
                ActorPrincipalId: context.PrincipalId, TenantId: context.TenantId,
                WorkspaceId: context.WorkspaceId, ReasonCode: exception.Code), cancellationToken);
            throw;
        }
    }

    private static void ValidateFlow(ResolvedKnowledgeFlowBinding flow, KnowledgeSourceOperation operation)
    {
        if (!string.Equals(flow.Contract, KnowledgeFlowContracts.Retrieval, StringComparison.Ordinal))
            throw Error("knowledge_retrieval_contract_required",
                $"The bound Flow must declare '{KnowledgeFlowContracts.Retrieval}'.");
        var capability = KnowledgeFlowContracts.Capability(operation);
        if (flow.Capabilities is null || !flow.Capabilities.Contains(capability, StringComparer.Ordinal))
            throw Error("knowledge_retrieval_capability_unavailable",
                $"The bound Flow does not declare capability '{capability}'.");
    }

    private static void ValidateOutput(
        KnowledgeSourceOperation operation,
        KnowledgeRetrievalFlowOutput output,
        IReadOnlySet<string> artifactIds,
        int maximumItems,
        string? requiredArtifactId)
    {
        var items = output.Items ?? [];
        var citations = output.Citations ?? [];
        if (items.Count > maximumItems)
            throw Error("knowledge_retrieval_result_limit_exceeded",
                $"The retrieval Flow returned more than {maximumItems} items.");
        if (citations.Count > MaximumCitations)
            throw Error("knowledge_retrieval_citation_limit_exceeded",
                $"The retrieval Flow returned more than {MaximumCitations} citations.");
        if (output.Answer?.Length > MaximumOutputCharacters
            || output.ContinuationToken?.Length > MaximumContinuationCharacters
            || items.Any(value => string.IsNullOrWhiteSpace(value.Id) || value.Id.Length > 256
                || string.IsNullOrWhiteSpace(value.ArtifactId) || !artifactIds.Contains(value.ArtifactId)
                || value.Content?.Length > MaximumOutputCharacters
                || value.MediaType?.Length > 160
                || value.Metadata.Count > 50)
            || citations.Any(value => string.IsNullOrWhiteSpace(value.ArtifactId)
                || !artifactIds.Contains(value.ArtifactId)
                || value.Locator?.Length > 1_000
                || value.Excerpt?.Length > 4_000
                || value.Start < 0
                || value.End < value.Start))
            throw Error("knowledge_retrieval_output_outside_snapshot",
                "The retrieval Flow output is unbounded or references content outside the selected Knowledge Snapshot.");
        if (operation == KnowledgeSourceOperation.Query
            && !string.IsNullOrWhiteSpace(output.Answer)
            && citations.Count == 0)
            throw Error("knowledge_query_citations_required", "A derived query answer requires at least one citation.");
        if (operation == KnowledgeSourceOperation.Read
            && (items.Count == 0 || items.Any(value => !string.Equals(value.ArtifactId, requiredArtifactId, StringComparison.Ordinal))
                || citations.Any(value => !string.Equals(value.ArtifactId, requiredArtifactId, StringComparison.Ordinal))))
            throw Error("knowledge_read_output_invalid",
                "Read output must contain only content from the requested Snapshot artifact.");
    }

    private static void ValidateContinuation(string? value)
    {
        if (value?.Length > MaximumContinuationCharacters)
            throw Error("knowledge_search_continuation_invalid",
                $"Continuation metadata cannot exceed {MaximumContinuationCharacters} characters.");
    }

    private RequestContext RequireContext() => requestContext.IsInitialized
        ? requestContext.Current
        : throw Error("knowledge_retrieval_context_required", "A Workspace request context is required.");

    private static KnowledgeRetrievalException Error(string code, string message, Exception? innerException = null) =>
        new(code, message, innerException);
}
