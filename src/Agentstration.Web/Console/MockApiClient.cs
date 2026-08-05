using System.Runtime.CompilerServices;
using System.Net;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Console;

public sealed class MockApiClient(TimeProvider timeProvider) : IManagementApiClient, IRuntimeApiClient, IWorkApiClient, IFlowApiClient, IAgentstrationEventStream
{
    private readonly Dictionary<string, ResourceSnapshot<AgentResource>> agents = CreateAgents();
    private readonly IReadOnlyList<AgentTypeResource> agentTypes = CreateAgentTypes();
    private readonly Dictionary<string, RuntimeRun> runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RuntimeRunEvent>> runEvents = new(StringComparer.Ordinal);
    private DateTimeOffset Now => timeProvider.GetUtcNow();

    public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken) =>
        GetAgentsAsync("default", cancellationToken);

    public Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(string resourceGroup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = agents.Values.Select(snapshot => snapshot.Value)
            .Where(agent => string.Equals(agent.ResourceGroup, resourceGroup, StringComparison.Ordinal))
            .OrderBy(agent => agent.Name, StringComparer.Ordinal)
            .Select(agent => new AgentSummary(agent.Id, agent.Properties.DisplayName, agent.Properties.AgentType.ResourceId, agent.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), agent.Status.ProvisioningState.ToString(), agent.Properties.Tools.Select(tool => tool.ResourceId).ToArray(), "Not reported", DateTimeOffset.MinValue))
            .ToArray();
        return Task.FromResult<IReadOnlyList<AgentSummary>>(values);
    }

    public Task<IReadOnlyList<AgentTypeResource>> GetAgentTypesAsync(string resourceGroup, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<AgentTypeResource>>(agentTypes.Where(type => string.Equals(type.ResourceGroup, resourceGroup, StringComparison.Ordinal)).ToArray(), cancellationToken);

    public Task<ResourceSnapshot<AgentResource>> GetAgentAsync(string resourceGroup, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return agents.TryGetValue(Key(resourceGroup, name), out var snapshot)
            ? Task.FromResult(snapshot)
            : Task.FromException<ResourceSnapshot<AgentResource>>(Error(HttpStatusCode.NotFound, "resource_not_found", $"Agent '{name}' was not found."));
    }

    public Task<ResourceSnapshot<AgentResource>> PutAgentAsync(AgentResourceRequest request, string? etag, bool createOnly, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(request.ResourceGroup, request.Name);
        var exists = agents.TryGetValue(key, out var current);
        if (createOnly && exists) return Task.FromException<ResourceSnapshot<AgentResource>>(Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "The agent already exists."));
        if (!createOnly && (!exists || !string.Equals(current!.ETag, etag, StringComparison.Ordinal)))
            return Task.FromException<ResourceSnapshot<AgentResource>>(Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "The agent was modified by another user."));

        var newEtag = $"\"{Guid.NewGuid():N}\"";
        var generation = exists ? current!.Value.Generation + 1 : 1;
        var resource = new AgentResource
        {
            Id = ResourceIdentifier.Create(request.ResourceGroup, AgentstrationProviderNamespaces.Agents, "agents", request.Name).Value,
            Name = request.Name,
            Type = request.Type,
            ApiVersion = request.ApiVersion,
            ResourceGroup = request.ResourceGroup,
            Location = request.Location,
            Tags = request.Tags ?? new Dictionary<string, string>(),
            Properties = request.Properties,
            Generation = generation,
            ETag = newEtag,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted, ResourceVersion = newEtag }
        };
        var snapshot = new ResourceSnapshot<AgentResource>(resource, newEtag);
        agents[key] = snapshot;
        return Task.FromResult(snapshot);
    }

    public Task DeleteAgentAsync(string resourceGroup, string name, string etag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Key(resourceGroup, name);
        if (!agents.TryGetValue(key, out var current)) return Task.FromException(Error(HttpStatusCode.NotFound, "resource_not_found", $"Agent '{name}' was not found."));
        if (!string.Equals(current.ETag, etag, StringComparison.Ordinal)) return Task.FromException(Error(HttpStatusCode.PreconditionFailed, "precondition_failed", "The agent was modified by another user."));
        agents.Remove(key);
        return Task.CompletedTask;
    }

    public Task<ManagementSummary> GetSummaryAsync(CancellationToken cancellationToken) => Result(new ManagementSummary(agentTypes.Count, agents.Count, agents.Values.Sum(item => checked((int)item.Value.Generation)), 0, "Managed"), cancellationToken);

    public Task<IReadOnlyList<RuntimeInstanceSummary>> GetInstancesAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<RuntimeInstanceSummary>>(
    [
        new("runtime-local-01", ".NET Expert · SQL Expert", "Ready", "InProcess", "local / pid 4128", "2 active runs", 18.2, 284),
        new("runtime-local-02", "Triage Router", "Degraded", "SharedHost", "local / pid 4128", "Waiting", 4.7, 126, "Model response latency above threshold")
    ], cancellationToken);

    public Task<RuntimeRun> CreateRunAsync(CreateRuntimeRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = $"run-{Guid.NewGuid():N}";
        var run = new RuntimeRun
        {
            Id = id,
            Name = id,
            ResourceGroup = ResourceIdentifier.Parse(request.Agent.ResourceId).ResourceGroup,
            Properties = new RuntimeRunProperties
            {
                Agent = request.Agent,
                Input = request.Input,
                Execution = request.Execution,
                Origin = request.Origin,
                Initiator = request.Initiator ?? "local-user"
            },
            Status = new RuntimeRunStatus
            {
                State = RuntimeRunState.Pending,
                CreatedAt = Now,
                ModelProfile = "reasoning-default"
            },
            ETag = $"\"mock-{Guid.NewGuid():N}\""
        };
        runs[id] = run;
        runEvents[id] = [];
        AddRunEvent(id, RuntimeRunEventKind.RunCreated, "Run created", state: RuntimeRunState.Pending);
        return Task.FromResult(run);
    }

    public Task<RuntimeRun> GetRunAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return runs.TryGetValue(runId, out var run)
            ? Task.FromResult(run)
            : Task.FromException<RuntimeRun>(Error(HttpStatusCode.NotFound, "run_not_found", $"Runtime run '{runId}' was not found."));
    }

    public Task<IReadOnlyList<RuntimeRun>> GetRunsAsync(string? agentResourceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = runs.Values
            .Where(run => string.IsNullOrWhiteSpace(agentResourceId) || string.Equals(run.Properties.Agent.ResourceId, agentResourceId, StringComparison.Ordinal))
            .OrderByDescending(run => run.Status.CreatedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<RuntimeRun>>(values);
    }

    public async IAsyncEnumerable<RuntimeRunEvent> ObserveRunAsync(string runId, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!runs.TryGetValue(runId, out var run)) throw Error(HttpStatusCode.NotFound, "run_not_found", $"Runtime run '{runId}' was not found.");
        foreach (var existing in runEvents[runId].Where(item => item.Sequence > afterSequence).ToArray()) yield return existing;
        if (run.Status.State.IsTerminal()) yield break;

        run = run with { Status = run.Status with { State = RuntimeRunState.Running, StartedAt = Now } };
        runs[runId] = run;
        yield return AddRunEvent(runId, RuntimeRunEventKind.StatusChanged, "Run started", state: RuntimeRunState.Running);
        foreach (var step in new[] { "Agent definition resolved", "Model profile resolved", "Prompt composed", "Model invoked" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return AddRunEvent(runId, RuntimeRunEventKind.StepCompleted, step, step: step);
            await Task.Yield();
        }
        var prompt = run.Properties.Input.Messages.Last(message => message.Role == RuntimeMessageRole.User).Content;
        var chunks = new[] { "Simulated agent response: ", prompt };
        var response = string.Empty;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            response += chunk;
            yield return AddRunEvent(runId, RuntimeRunEventKind.ResponseDelta, content: chunk);
            await Task.Yield();
        }
        run = run with { Status = run.Status with { State = RuntimeRunState.Succeeded, Response = response, CompletedAt = Now } };
        runs[runId] = run;
        yield return AddRunEvent(runId, RuntimeRunEventKind.StatusChanged, "Run succeeded", state: RuntimeRunState.Succeeded);
        yield return AddRunEvent(runId, RuntimeRunEventKind.RunCompleted, "Response completed", state: RuntimeRunState.Succeeded);
    }

    public Task<RuntimeRun> CancelRunAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!runs.TryGetValue(runId, out var run)) return Task.FromException<RuntimeRun>(Error(HttpStatusCode.NotFound, "run_not_found", $"Runtime run '{runId}' was not found."));
        if (!run.Status.State.IsTerminal())
        {
            run = run with { Status = run.Status with { State = RuntimeRunState.Cancelled, Error = "Cancelled by the caller.", CompletedAt = Now } };
            runs[runId] = run;
            AddRunEvent(runId, RuntimeRunEventKind.RunCompleted, "Run cancelled", state: RuntimeRunState.Cancelled);
        }
        return Task.FromResult(run);
    }

    public async Task<RuntimeRun> RetryRunAsync(string runId, CancellationToken cancellationToken)
    {
        var source = await GetRunAsync(runId, cancellationToken);
        return await CreateRunAsync(new CreateRuntimeRunRequest
        {
            Agent = source.Properties.Agent,
            Input = source.Properties.Input,
            Execution = source.Properties.Execution,
            Origin = source.Properties.Origin,
            Initiator = source.Properties.Initiator
        }, cancellationToken);
    }

    public Task<IReadOnlyList<WorkSummary>> GetWorkItemsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<WorkSummary>>(
    [
        new(Guid.Parse("956124c8-b579-4b19-9097-450687a70ce1"), "Review data access boundaries", "review", "Running", "High", ".NET Expert", Now.AddMinutes(-34), Now.AddMinutes(-2)),
        new(Guid.Parse("bf88eb82-cda9-41ef-9b9d-c73f90b7b810"), "Optimize customer query", "question", "Queued", "Normal", "SQL Expert", Now.AddMinutes(-12), Now.AddMinutes(-10)),
        new(Guid.Parse("4022fd69-82a4-4d18-8276-bc12b8c23dd5"), "Classify incoming incident", "triage", "NeedsInput", "Critical", "Triage Router", Now.AddHours(-1), Now.AddMinutes(-18)),
        new(Guid.Parse("86b42b89-c322-4cd8-a013-fe8ceec49b68"), "Generate release summary", "content", "Completed", "Low", ".NET Expert", Now.AddHours(-3), Now.AddHours(-2))
    ], cancellationToken);

    public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<FlowSummary>>(
    [
        new("engineering-review", "Engineering review", "Workflow", "2.1.0", "Active", 5, 2, Now.AddDays(-1)),
        new("smart-triage", "Smart triage", "Routing", "1.4.0", "Active", 4, 1, Now.AddHours(-5)),
        new("approval-gate", "Human approval gate", "Composite", "1.0.0", "Draft", 3, 0, Now.AddDays(-3))
    ], cancellationToken);

    public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) =>
        Task.FromException<FlowResponse>(new KeyNotFoundException($"Simulated Flow '{flowId}' has no definition payload."));
    public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<FlowVersionResponse>>([], cancellationToken);
    public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<FlowRun>>([], cancellationToken);
    public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromException<FlowRun>(new KeyNotFoundException($"Simulated Flow Run '{runId}' was not found."));
    public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) =>
        Result<IReadOnlyList<FlowRunEvent>>([], cancellationToken);
    public Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) =>
        Task.FromException<FlowRun>(new NotSupportedException("Simulated Flow Runs are not supported."));
    public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromException<FlowRun>(new NotSupportedException("Simulated Flow Runs are not supported."));
    public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
    public Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken) => UnsupportedDraft();
    public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) => UnsupportedDraft();
    public Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => UnsupportedDraft();
    public Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken) => Task.FromResult(new FlowValidationResponse(false, []));
    public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) => Task.FromException<FlowSourceResponse>(new NotSupportedException("Simulated Flow Drafts are not supported."));
    public Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => UnsupportedDraft();
    public Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => Task.FromException<FlowVersionResponse>(new NotSupportedException("Simulated Flow Drafts are not supported."));
    public Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => Task.FromException<FlowRun>(new NotSupportedException("Simulated Flow Drafts are not supported."));
    public Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken) => UnsupportedDraft();
    private static Task<FlowDraftResponse> UnsupportedDraft() => Task.FromException<FlowDraftResponse>(new NotSupportedException("Simulated Flow Drafts are not supported."));

    public Task<IReadOnlyList<ExecutionSummary>> GetExecutionsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<ExecutionSummary>>(
    [
        new("run-7f8a", ".NET Expert", "Engineering review", Guid.Parse("956124c8-b579-4b19-9097-450687a70ce1"), "Running", Now.AddMinutes(-8), null, null, null),
        new("run-51c2", "Triage Router", "Smart triage", Guid.Parse("4022fd69-82a4-4d18-8276-bc12b8c23dd5"), "Waiting", Now.AddMinutes(-18), TimeSpan.FromSeconds(42), "Human input requested", null),
        new("run-a944", "SQL Expert", null, Guid.Parse("86b42b89-c322-4cd8-a013-fe8ceec49b68"), "Completed", Now.AddHours(-2), TimeSpan.FromMinutes(1.4), "Completed successfully", null),
        new("run-113d", "Triage Router", "Smart triage", null, "Failed", Now.AddHours(-4), TimeSpan.FromSeconds(12), null, "Runtime connection interrupted")
    ], cancellationToken);

    public Task<IReadOnlyList<EventListItem>> GetRecentEventsAsync(CancellationToken cancellationToken) => Result<IReadOnlyList<EventListItem>>(
    [
        new(Now.AddSeconds(-18), "Information", "Runtime", "ExecutionStarted", "Engineering review started", "run-7f8a"),
        new(Now.AddMinutes(-2), "Warning", "Runtime", "HealthChanged", "Triage runtime is degraded", "runtime-local-02"),
        new(Now.AddMinutes(-10), "Information", "Work", "WorkItemQueued", "Optimize customer query queued", "bf88eb82"),
        new(Now.AddHours(-1), "Error", "Flow", "StepFailed", "Routing step could not reach its runtime", "run-113d")
    ], cancellationToken);

    public async IAsyncEnumerable<EventListItem> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    private static Task<T> Result<T>(T value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value);
    }

    private static string Key(string resourceGroup, string name) => $"{resourceGroup}/{name}";

    private static AgentstrationApiException Error(HttpStatusCode statusCode, string title, string message) =>
        new(message, Guid.NewGuid().ToString("N"), statusCode, title);

    private RuntimeRunEvent AddRunEvent(string runId, RuntimeRunEventKind kind, string? message = null, string? step = null, string? content = null, RuntimeRunState? state = null)
    {
        var values = runEvents[runId];
        var runEvent = new RuntimeRunEvent
        {
            Sequence = values.Count + 1,
            EventId = Guid.NewGuid(),
            RunId = runId,
            Kind = kind,
            Timestamp = Now,
            Message = message,
            Step = step,
            Content = content,
            State = state
        };
        values.Add(runEvent);
        return runEvent;
    }

    private static IReadOnlyList<AgentTypeResource> CreateAgentTypes()
    {
        var id = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value;
        return
        [
            new AgentTypeResource
            {
                Id = id,
                Name = "readonly-expert",
                Type = AgentstrationResourceTypes.AgentTypes,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = "default",
                Location = "local",
                Generation = 1,
                Properties = new AgentTypeDefinition
                {
                    Key = "readonly-expert",
                    Version = 1,
                    Handler = "prompt-agent",
                    BaseInstructions = "Act as a specialized read-only expert.",
                    DefaultModelProfileId = "reasoning-default",
                    Policy = new AgentTypePolicy { AllowAdditionalInstructions = true, AllowModelOverride = true, AllowAdditionalTools = true, MaximumAdditionalInstructionsLength = 10_000 }
                }
            }
        ];
    }

    private static Dictionary<string, ResourceSnapshot<AgentResource>> CreateAgents()
    {
        var type = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agentTypes", "readonly-expert").Value;
        var model = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Models, "modelProfiles", "reasoning-default").Value;
        var definitions = new[]
        {
            (Name: "dotnet-expert", DisplayName: ".NET Expert", Description: "Specialized in .NET and ASP.NET Core.", Instructions: "Focus on safe, practical .NET guidance."),
            (Name: "sql-expert", DisplayName: "SQL Expert", Description: "Specialized in SQL query performance.", Instructions: "Focus on SQL Server and read-only diagnostics.")
        };
        return definitions.ToDictionary(item => Key("default", item.Name), item =>
        {
            var etag = $"\"mock-{item.Name}\"";
            var resource = new AgentResource
            {
                Id = ResourceIdentifier.Create("default", AgentstrationProviderNamespaces.Agents, "agents", item.Name).Value,
                Name = item.Name,
                Type = AgentstrationResourceTypes.Agents,
                ApiVersion = ManagementApiVersions.V20260801,
                ResourceGroup = "default",
                Location = "local",
                Generation = 1,
                ETag = etag,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Accepted, ResourceVersion = etag },
                Properties = new AgentProperties
                {
                    DisplayName = item.DisplayName,
                    Description = item.Description,
                    AgentType = new AgentTypeReference(type, 1),
                    AdditionalInstructions = item.Instructions,
                    ModelProfile = new ResourceReference(model)
                }
            };
            return new ResourceSnapshot<AgentResource>(resource, etag);
        }, StringComparer.Ordinal);
    }
}
