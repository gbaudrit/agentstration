using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ModelProviders;
using Agentstration.Runtime.Abstractions;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.AI;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeWorkerOperationGateway(
    IChatClientResolver chatClients,
    IToolExecutionPipeline toolExecution,
    IArtifactStore artifacts,
    IRuntimeExecutionStateStore executionStates,
    IFlowRepository flowRunRepository,
    IFlowToolExecutor flowToolExecutor,
    IFlowToolSetResolver flowToolSetResolver,
    IFlowStepArtifactCapture flowArtifactCapture,
    FlowRunService flowRuns,
    TimeProvider timeProvider,
    IRequestContextScopeFactory requestScopes) : IRuntimeWorkerOperationGateway
{
    private const string ArtifactRuntimeType = "awp-artifact-v1";

    public async Task<RuntimeGovernedModelResponse> InvokeModelAsync(
        RuntimeGovernedModelRequest request,
        CancellationToken cancellationToken)
    {
        using var requestScope = EnterScope(request.TenantId, request.WorkspaceId, request.PrincipalId);
        var client = await chatClients.ResolveAsync(request.Agent.ModelProfileNamespace,
            request.Agent.ModelProfileName, cancellationToken);
        var messages = request.Messages.Select(value => new ChatMessage(ToRole(value.Role),
            value.Contents.Select(ToContent).ToList())).ToArray();
        var options = new ChatOptions
        {
            Instructions = request.Agent.Instructions,
            Tools = request.Agent.Tools.Select(tool => (AITool)AIFunctionFactory.CreateDeclaration(
                tool.Name, tool.Description, tool.InputSchema)).ToList()
        };
        if (request.Options.Parameters.TryGetValue("temperature", out var temperature) && temperature.TryGetSingle(out var temperatureValue))
            options.Temperature = temperatureValue;
        if (request.Options.Parameters.TryGetValue("maxOutputTokens", out var tokens) && tokens.TryGetInt32(out var tokenValue))
            options.MaxOutputTokens = tokenValue;
        var response = await client.GetResponseAsync(messages, options, cancellationToken);
        var contents = response.Messages.SelectMany(value => value.Contents).Select(ToContent).OfType<RuntimeGovernedModelContent>().ToArray();
        return new RuntimeGovernedModelResponse(
            contents,
            response.ModelId,
            response.FinishReason?.ToString(),
            ToInt32(response.Usage?.InputTokenCount),
            ToInt32(response.Usage?.OutputTokenCount));
    }

    public async Task<JsonElement?> InvokeToolAsync(RuntimeGovernedToolRequest request, CancellationToken cancellationToken)
    {
        using var requestScope = EnterScope(request.TenantId, request.WorkspaceId, request.PrincipalId);
        return await toolExecution.ExecuteAsync(new ToolExecutionContext
        {
            OwnerKind = request.StepExecutionId is null ? ToolExecutionOwnerKind.RuntimeRun : ToolExecutionOwnerKind.FlowRun,
            ToolCallId = request.ToolCallId.ToString("D"),
            InvocationId = $"awp:{request.TurnAttemptId:D}:{request.ToolCallId:D}",
            ToolId = request.Tool.Id,
            ToolNamespace = request.Tool.Namespace,
            ToolName = request.Tool.Name,
            ToolProviderId = request.Tool.ProviderId,
            ToolProviderNamespace = request.Tool.ProviderNamespace,
            ExternalToolId = request.Tool.ExternalId,
            TenantId = request.TenantId,
            WorkspaceId = request.WorkspaceId,
            PrincipalId = request.PrincipalId,
            RunId = request.RunId,
            FlowStepId = request.StepExecutionId?.ToString("D"),
            AgentId = request.Agent.AgentId.ToString("D"),
            AgentVersion = request.Agent.Generation,
            AgentGeneration = request.Agent.Generation,
            AgentRevisionId = request.Agent.RevisionId,
            PersistArguments = request.PersistArguments,
            Arguments = request.Arguments?.Clone()
        }, cancellationToken);
    }

    public async Task<RuntimeGovernedArtifact> StoreArtifactAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        string name,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        if (content.Length > 1_048_576)
            throw new RuntimeExecutionMaterialException("artifact_too_large", "An AWP artifact cannot exceed 1 MiB.");
        var artifactId = Guid.NewGuid();
        await using var stream = new MemoryStream(content, writable: false);
        var reference = await artifacts.SaveAsync(workspaceId, new ArtifactContent(name, contentType, stream), cancellationToken);
        var payload = JsonSerializer.SerializeToElement(new ArtifactState(artifactId, name, reference));
        await executionStates.StoreAsync(new RuntimeExecutionState(workspaceId, AssignmentRunId(assignmentId),
            ArtifactRuntimeType, artifactId.ToString("D"), payload, timeProvider.GetUtcNow()), cancellationToken);
        return new RuntimeGovernedArtifact(artifactId, name, reference.ContentType, reference.Length, content);
    }

    public async Task<RuntimeGovernedFlowToolResult> InvokeFlowToolAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        string flowRunId,
        string stepDefinitionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var stored = await flowRunRepository.GetRunAsync(workspaceId, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        using var requestScope = EnterScope(run.Scope.TenantId, run.Scope.WorkspaceId, run.Scope.PrincipalId);
        var step = run.DefinitionSnapshot.Graph?.Steps.SingleOrDefault(value =>
            string.Equals(value.Name, stepDefinitionId, StringComparison.Ordinal))
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid",
                "The Flow Tool StepDefinition is not present in the assigned Flow snapshot.");
        var attempt = Math.Max(1, run.Steps.Single(value => value.StepName == stepDefinitionId).Attempt);
        FlowToolReference tool;
        RuntimeGovernedFlowToolRoute? route = null;
        switch (step)
        {
            case ToolFlowStepDefinition direct:
                tool = direct.Tool;
                break;
            case ToolRouteFlowStepDefinition routed:
                var resolved = await flowToolSetResolver.ResolveAsync(workspaceId, run.FlowId.Namespace,
                    routed, cancellationToken);
                tool = resolved.Tool;
                route = new(resolved.ToolSetName, resolved.ToolSetNamespace, resolved.ToolSetVersion,
                    resolved.Capability, resolved.Route);
                break;
            default:
                throw new RuntimeExecutionMaterialException("flow_tool_step_invalid",
                    "The assigned StepDefinition is not a Tool or ToolRoute step.");
        }

        var result = await flowToolExecutor.ExecuteAsync(new(
            run.Scope,
            run.Id,
            run.FlowId,
            step.Name,
            attempt,
            run.CorrelationId ?? run.Id,
            tool,
            arguments), cancellationToken);
        return new(
            result.Output?.Clone(),
            result.ToolName,
            result.ToolNamespace,
            result.ToolUid,
            result.ToolGeneration,
            result.ProviderName,
            result.ProviderNamespace,
            result.ProviderType,
            result.ExternalToolId,
            route);
    }

    public async Task<RuntimeGovernedFlowArtifact> CaptureFlowArtifactAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        string flowRunId,
        string stepDefinitionId,
        string? fileName,
        string mediaType,
        JsonElement content,
        IReadOnlyDictionary<string, string> provenance,
        CancellationToken cancellationToken)
    {
        var (run, step) = await GetFlowStepAsync(workspaceId, flowRunId, stepDefinitionId, cancellationToken);
        var declaration = step.ArtifactOutput
            ?? throw new RuntimeExecutionMaterialException("flow_step_artifact_not_declared",
                "The assigned Flow step does not declare an Artifact output.");
        using var requestScope = EnterScope(run.Scope.TenantId, run.Scope.WorkspaceId, run.Scope.PrincipalId);
        var trustedProvenance = new Dictionary<string, string>(provenance, StringComparer.Ordinal)
        {
            ["flowName"] = run.FlowId.Value,
            ["flowNamespace"] = run.FlowId.Namespace.Value,
            ["flowVersion"] = run.FlowVersion,
            ["rootFlowRunId"] = run.RootFlowRunId ?? run.Id
        };
        if (run.ParentFlowRunId is not null) trustedProvenance["parentFlowRunId"] = run.ParentFlowRunId;
        var attempt = Math.Max(1, run.Steps.Single(value => value.StepName == stepDefinitionId).Attempt);
        trustedProvenance["stepAttempt"] = attempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var artifact = await flowArtifactCapture.CaptureAsync(new(
            run.Scope,
            run.Id,
            run.RootFlowRunId ?? run.Id,
            run.ParentFlowRunId,
            step.Name,
            attempt,
            run.CorrelationId,
            declaration with { FileName = fileName, MediaType = mediaType },
            content,
            trustedProvenance), cancellationToken);
        return new(artifact.ArtifactId, artifact.FileName, artifact.MediaType, artifact.Kind,
            artifact.StorageFlowRunId, artifact.LocalArtifactId);
    }

    public async Task CleanupFlowArtifactAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        string flowRunId,
        string stepDefinitionId,
        RuntimeGovernedFlowArtifact artifact,
        CancellationToken cancellationToken)
    {
        var (run, step) = await GetFlowStepAsync(workspaceId, flowRunId, stepDefinitionId, cancellationToken);
        if (step.ArtifactOutput is null)
            throw new RuntimeExecutionMaterialException("flow_step_artifact_not_declared",
                "The assigned Flow step does not declare an Artifact output.");
        using var requestScope = EnterScope(run.Scope.TenantId, run.Scope.WorkspaceId, run.Scope.PrincipalId);
        await flowArtifactCapture.CleanupAsync(run.Scope, run.Id, step.Name,
            new(artifact.ArtifactId, artifact.FileName, artifact.MediaType, artifact.Kind,
                artifact.StorageFlowRunId, artifact.LocalArtifactId), cancellationToken);
    }

    public async Task<RuntimeGovernedArtifact?> GetArtifactAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        RuntimeAssignmentId assignmentId,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        var state = await executionStates.GetAsync(workspaceId, AssignmentRunId(assignmentId), ArtifactRuntimeType,
            artifactId.ToString("D"), cancellationToken);
        if (state is null) return null;
        var artifact = state.Payload.Deserialize<ArtifactState>()
            ?? throw new InvalidOperationException("The stored AWP artifact reference is invalid.");
        await using var stream = await artifacts.OpenReadAsync(workspaceId, artifact.Reference, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > 1_048_576)
            throw new RuntimeExecutionMaterialException("artifact_too_large", "The stored AWP artifact exceeds 1 MiB.");
        return new RuntimeGovernedArtifact(artifact.ArtifactId, artifact.Name, artifact.Reference.ContentType,
            artifact.Reference.Length, buffer.ToArray());
    }

    public async Task<RuntimeGovernedChildFlow> CreateOrGetChildFlowAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        string parentRunId,
        string stepDefinitionId,
        JsonElement input,
        string purpose,
        int? iteration,
        CancellationToken cancellationToken)
    {
        var child = await flowRuns.CreateOrGetAssignedChildAsync(workspaceId, parentRunId,
            stepDefinitionId, input, purpose, iteration, cancellationToken);
        return new RuntimeGovernedChildFlow(child.Value.Id, child.Value.Status.ToString(), child.Value.Output?.Clone());
    }

    private static ChatRole ToRole(RuntimeMessageRole role) => role switch
    {
        RuntimeMessageRole.System => ChatRole.System,
        RuntimeMessageRole.Developer => ChatRole.System,
        RuntimeMessageRole.Assistant => ChatRole.Assistant,
        RuntimeMessageRole.Tool => ChatRole.Tool,
        _ => ChatRole.User
    };

    private static AIContent ToContent(RuntimeGovernedModelContent content) => content switch
    {
        RuntimeGovernedModelText text => new TextContent(text.Text),
        RuntimeGovernedModelToolCall call => new FunctionCallContent(call.CallId, call.Name,
            call.Arguments.Deserialize<Dictionary<string, object?>>() ?? new Dictionary<string, object?>()),
        RuntimeGovernedModelToolResult result => new FunctionResultContent(result.CallId,
            result.Result.Deserialize<object?>()),
        _ => throw new ArgumentOutOfRangeException(nameof(content))
    };

    private static RuntimeGovernedModelContent? ToContent(AIContent content) => content switch
    {
        TextContent text => new RuntimeGovernedModelText(text.Text),
        FunctionCallContent call => new RuntimeGovernedModelToolCall(call.CallId, call.Name,
            JsonSerializer.SerializeToElement(call.Arguments)),
        FunctionResultContent result => new RuntimeGovernedModelToolResult(result.CallId,
            JsonSerializer.SerializeToElement(result.Result)),
        _ => null
    };

    private static int? ToInt32(long? value) => value is null || value < 0 ? null : (int)Math.Min(value.Value, int.MaxValue);
    private static string AssignmentRunId(RuntimeAssignmentId assignmentId) => $"awp-assignment:{assignmentId.Value:N}";

    private async Task<(FlowRun Run, FlowStepDefinition Step)> GetFlowStepAsync(
        Agentstration.Resources.WorkspaceId workspaceId,
        string flowRunId,
        string stepDefinitionId,
        CancellationToken cancellationToken)
    {
        var stored = await flowRunRepository.GetRunAsync(workspaceId, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var step = stored.Value.DefinitionSnapshot.Graph?.Steps.SingleOrDefault(value =>
            string.Equals(value.Name, stepDefinitionId, StringComparison.Ordinal))
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid",
                "The StepDefinition is not present in the assigned Flow snapshot.");
        return (stored.Value, step);
    }
    private IDisposable EnterScope(Guid tenantId, Agentstration.Resources.WorkspaceId workspaceId, Guid principalId) =>
        requestScopes.Push(new RequestContext(principalId, tenantId, workspaceId.Value));

    private sealed record ArtifactState(Guid ArtifactId, string Name, ArtifactReference Reference);
}
