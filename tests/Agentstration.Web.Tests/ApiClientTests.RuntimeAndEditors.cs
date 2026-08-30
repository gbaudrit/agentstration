using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Configuration;
using Agentstration.Web.Console;
using Agentstration.Web.Features.Flows.Designer;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.Security;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public void AgentEditorMapsCanonicalReferencesTagsAndTools()
    {
        var model = new AgentEditorModel
        {
            Name = "web-agent",
            DisplayName = "Web Agent",
            Instructions = "Help with web development.",
            ModelProfileName = "reasoning-default",
            ModelProfileNamespace = "shared.models",
            RuntimeProfileName = "maf-shared",
            RuntimeProfileNamespace = "shared.platform",
            ToolNames = "search",
            Tags = "domain=web\nowner=platform"
        };

        var request = model.ToRequest();

        Assert.AreEqual("reasoning-default", request.Definition.ModelProfile.Name);
        Assert.AreEqual(new ResourceNamespace("shared.models"), request.Definition.ModelProfile.Namespace);
        Assert.AreEqual("maf-shared", request.Definition.RuntimeProfile.Name);
        Assert.AreEqual(new ResourceNamespace("shared.platform"), request.Definition.RuntimeProfile.Namespace);
        Assert.HasCount(1, request.Definition.Tools);
        Assert.AreEqual("web", request.Metadata.Tags["domain"]);
    }

    [TestMethod]
    public void AgentEditorRawYamlRoundTripsEditableDefinition()
    {
        var expected = new AgentEditorModel
        {
            Name = "welcome",
            DisplayName = "Welcome agent",
            Description = "Routes requests.",
            Instructions = "Help and route the user.",
            ModelProfileName = "reasoning",
            RuntimeProfileName = "maf-shared",
            ToolNames = "search",
            Tags = "role=welcome"
        };

        var yaml = ResourceManifestSerializer.ToYaml(expected.ToRequest());
        var parsed = ResourceManifestSerializer.FromYaml<AgentResourceRequest>(yaml);
        var actual = AgentEditorModel.FromRequest(parsed);
        actual.SourceDefinition = parsed.Definition with { Behaviors = ["handoff"], Middleware = ["audit"] };
        var roundTripped = actual.ToRequest();

        Assert.AreEqual(expected.DisplayName, actual.DisplayName);
        Assert.AreEqual(expected.Description, actual.Description);
        Assert.AreEqual(expected.Instructions, actual.Instructions);
        Assert.AreEqual(expected.ToolNames, actual.ToolNames);
        Assert.AreEqual(expected.Tags, actual.Tags);
        CollectionAssert.AreEqual(new[] { "handoff" }, roundTripped.Definition.Behaviors.ToArray());
        CollectionAssert.AreEqual(new[] { "audit" }, roundTripped.Definition.Middleware.ToArray());
    }

    [TestMethod]
    public void AgentRunnerBuildsVersionedRuntimePayloadAndValidatesJson()
    {
        var agent = CreateAgentResource("web-agent") with { Generation = 7 };
        var model = new AgentRunnerModel
        {
            Prompt = "Optimize this query",
            Context = "{\"engine\":\"sqlserver\"}",
            RuntimeParameters = "{\"temperature\":0.2}",
            Streaming = RuntimeStreamingMode.Enabled,
            ToolArgumentRetention = ToolArgumentRetentionMode.Retain,
            TimeoutSeconds = 90
        };

        var request = model.ToRequest(agent);

        Assert.AreEqual(agent.Metadata.Name, request.Agent.ResourceId);
        Assert.AreEqual(7L, request.Agent.Version);
        Assert.AreEqual(RuntimeRunOrigin.Console, request.Origin);
        Assert.AreEqual(90, request.Execution.TimeoutSeconds);
        Assert.AreEqual(RuntimeStreamingMode.Enabled, request.Execution.Streaming);
        Assert.AreEqual(true, request.Execution.PersistToolArguments);
        Assert.AreEqual(0.2, request.Execution.Parameters["temperature"].GetDouble());
    }

    [TestMethod]
    public void AgentRunnerMapsAllToolArgumentRetentionModes()
    {
        var agent = CreateAgentResource("web-agent");

        bool? Map(ToolArgumentRetentionMode mode) => new AgentRunnerModel
        {
            Prompt = "test",
            ToolArgumentRetention = mode
        }.ToRequest(agent).Execution.PersistToolArguments;

        Assert.IsNull(Map(ToolArgumentRetentionMode.Inherit));
        Assert.AreEqual(true, Map(ToolArgumentRetentionMode.Retain));
        Assert.AreEqual(false, Map(ToolArgumentRetentionMode.DoNotRetain));
    }

    [TestMethod]
    public async Task RuntimeClientProcessesProgressiveSseAndClosesAtEndOfStream()
    {
        var first = RunEvent(1, RuntimeRunEventKind.StatusChanged, state: RuntimeRunState.Running);
        var second = RunEvent(2, RuntimeRunEventKind.ResponseDelta, content: "partial response");
        var third = RunEvent(3, RuntimeRunEventKind.RunCompleted, state: RuntimeRunState.Succeeded);
        var payload = string.Join(string.Empty, new[] { first, second, third }.Select(item => $"id: {item.Sequence}\nevent: {item.Kind}\ndata: {JsonSerializer.Serialize(item, JsonOptions)}\n\n"));
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) })) { BaseAddress = new Uri("http://localhost/") };
        var client = new RuntimeApiClient(httpClient);
        var state = new AgentRunnerState();
        state.Reset(CreateRun("run-test"));

        await foreach (var runEvent in client.ObserveRunAsync("run-test", 0, default)) state.Apply(runEvent);

        Assert.AreEqual("partial response", state.Response);
        Assert.AreEqual(RuntimeRunState.Succeeded, state.State);
        Assert.HasCount(3, state.Events);
    }

    [TestMethod]
    public async Task RuntimeClientReadsPersistedEventHistoryAfterTheRequestedSequence()
    {
        Uri? requested = null;
        var expected = new[] { RunEvent(8, RuntimeRunEventKind.RunCompleted, state: RuntimeRunState.Succeeded) };
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        var client = new RuntimeApiClient(httpClient);

        var actual = await client.GetRunEventsAsync("run-test", 7, default);

        Assert.HasCount(1, actual);
        Assert.AreEqual(RuntimeRunEventKind.RunCompleted, actual[0].Kind);
        Assert.AreEqual("/api/runtime/runs/run-test/eventHistory?afterSequence=7", requested!.PathAndQuery);
    }

    [TestMethod]
    public void AgentRunnerRestoresToolCallsFromTheDurableRunProjection()
    {
        var persistedToolCall = new RuntimeToolCall
        {
            Id = "tool-call-1",
            InvocationId = "invocation-1",
            ToolId = "microsoft-learn.microsoft_docs_search",
            Name = "microsoft_docs_search",
            State = RuntimeRunState.Succeeded,
            Attempt = 1,
            StartedAt = DateTimeOffset.UtcNow
        };
        var originalRun = CreateRun("run-with-tool");
        var run = originalRun with
        {
            Status = originalRun.Status with { ToolCalls = [persistedToolCall] }
        };
        var state = new AgentRunnerState();

        state.Reset(run);

        Assert.HasCount(1, state.ToolCalls);
        Assert.AreEqual(persistedToolCall, state.ToolCalls[0]);
    }

    [TestMethod]
    public void AgentRunnerRestoresPersistedTraceWithoutDuplicatingProjectedResponse()
    {
        var originalRun = CreateRun("completed-run");
        var run = originalRun with
        {
            Status = originalRun.Status with
            {
                State = RuntimeRunState.Succeeded,
                Response = "final response"
            }
        };
        var state = new AgentRunnerState();
        state.Reset(run);

        state.Restore(RunEvent(1, RuntimeRunEventKind.StatusChanged, state: RuntimeRunState.Running));
        state.Restore(RunEvent(2, RuntimeRunEventKind.ResponseDelta, content: "final response"));
        state.Restore(RunEvent(3, RuntimeRunEventKind.RunCompleted, state: RuntimeRunState.Succeeded));

        Assert.AreEqual("final response", state.Response);
        Assert.AreEqual(RuntimeRunState.Succeeded, state.State);
        Assert.HasCount(3, state.Events);
        Assert.AreEqual(3L, state.LastSequence);
    }

    [TestMethod]
    public async Task AgentRunnerRuntimeClientReadsCanonicalReadinessEndpoint()
    {
        var requested = new List<Uri>();
        var readiness = new AgentRuntimeReadinessResponse("agent-id", 4, true, "Ready", "deployment-id", "revision-id", null);
        var preparation = new PrepareAgentRuntimeResponse("agent-id", 4, "deployment-id", "revision-id", "Ready");
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create<object>(request.Method == HttpMethod.Post ? preparation : readiness) };
        }))
        { BaseAddress = new Uri("http://localhost/") };
        IAgentRunnerRuntimeClient client = new RuntimeApiClient(httpClient);

        var actual = await client.GetAgentReadinessAsync("sql-expert", 4, default);
        var prepared = await client.PrepareAgentAsync("sql-expert", 4, default);
        var agentNamespace = new ResourceNamespace("agentstration.daily-life-assistant");
        var namespaced = await client.GetAgentReadinessAsync(agentNamespace, "concierge", 5, default);
        var namespacedPreparation = await client.PrepareAgentAsync(agentNamespace, "concierge", 5, default);

        Assert.IsTrue(actual.Ready);
        Assert.AreEqual("Ready", prepared.State);
        Assert.IsTrue(namespaced.Ready);
        Assert.AreEqual("Ready", namespacedPreparation.State);
        StringAssert.Contains(requested[0].PathAndQuery, "/api/runtime/agents/sql-expert/readiness?generation=4");
        StringAssert.Contains(requested[1].PathAndQuery, "/api/runtime/agents/sql-expert/prepare?generation=4");
        StringAssert.Contains(requested[2].PathAndQuery, "/api/runtime/namespaces/agentstration.daily-life-assistant/agents/concierge/readiness?generation=5");
        StringAssert.Contains(requested[3].PathAndQuery, "/api/runtime/namespaces/agentstration.daily-life-assistant/agents/concierge/prepare?generation=5");
    }

    [TestMethod]
    public void AgentRunnerRejectsProviderAndModelOverrides()
    {
        var model = new AgentRunnerModel { Prompt = "test", RuntimeParameters = "{\"model\":\"other\"}" };

        var exception = Assert.ThrowsExactly<ArgumentException>(() => model.ToRequest(CreateAgentResource("web-agent")));

        StringAssert.Contains(exception.Message, "not supported");
    }

    [TestMethod]
    public async Task SimulatedRuntimeRetryCreatesNewRunIdentifier()
    {
        var client = new MockApiClient(TimeProvider.System);
        var request = new CreateRuntimeRunRequest
        {
            Agent = new RuntimeAgentReference(CreateAgentResource("web-agent").Metadata.Name, 1),
            Input = new RuntimeRunInput { Messages = [new RuntimeRunMessage(RuntimeMessageRole.User, "test")] }
        };
        var original = await client.CreateRunAsync(request, default);

        var retry = await client.RetryRunAsync(original.Id, default);

        Assert.AreNotEqual(original.Id, retry.Id);
        Assert.AreEqual(original.Properties.Input, retry.Properties.Input);
    }

}

