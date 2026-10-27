using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Flows.Storage.Sqlite;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Flows;
using Agentstration.Infrastructure.Runtime;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task FlowServiceIsolatesHomonymousFlowsAndResolvesRelativeReferencesWithinOwnerNamespace()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        var command = new CreateFlowCommand("router", "Routes work", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant")));
        var first = await fixture.Service.CreateAsync(TestScope.WorkspaceId, command, firstNamespace, default);
        var second = await fixture.Service.CreateAsync(TestScope.WorkspaceId, command, secondNamespace, default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, first.Value.Id, "1.0.0", true, default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, second.Value.Id, "1.0.0", true, default);

        Assert.AreEqual(firstNamespace, (await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router", firstNamespace), default))?.Value.Id.Namespace);
        Assert.AreEqual(secondNamespace, (await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router", secondNamespace), default))?.Value.Id.Namespace);
        Assert.IsNull(await fixture.Service.GetAsync(TestScope.WorkspaceId, new FlowId("router"), default));
        Assert.AreEqual(firstNamespace, (await fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(new FlowId("router")), firstNamespace, default)).FlowId.Namespace);
        Assert.AreEqual(secondNamespace, (await fixture.Service.ResolveAsync(TestScope.WorkspaceId, new FlowReference(new FlowId("router")), secondNamespace, default)).FlowId.Namespace);
    }

    [TestMethod]
    public async Task FlowApiSupportsCrudVersionsPolymorphismAndOpenApi()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var request = new CreateFlowRequest("direct-sql", "Direct SQL work", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")))
        {
            DisplayName = "Direct SQL review"
        };
        using var createdResponse = await client.PostAsJsonAsync("/api/flows", request, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.IsInstanceOfType<DirectFlowDefinition>(created!.Definition);
        Assert.AreEqual("Direct SQL review", created.DisplayName);
        Assert.AreEqual(ResourceScopeKind.Workspace, created.ScopeRef?.Kind);

        using var versionResponse = await client.PostAsJsonAsync("/api/flows/direct-sql/versions", new CreateFlowVersionRequest("1.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, versionResponse.StatusCode);
        var version = await client.GetFromJsonAsync<FlowVersionResponse>("/api/flows/direct-sql/versions/1.0.0", JsonOptions);
        Assert.AreEqual("1.0.0", version!.Version);
        Assert.AreEqual(created.ScopeRef, version.ScopeRef);

        var get = await client.GetAsync("/api/flows/direct-sql");
        var etag = get.Headers.ETag!.Tag;
        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/flows/direct-sql")
        {
            Content = JsonContent.Create(new UpdateFlowRequest("Updated", "1.1.0", true, request.Definition), options: JsonOptions)
        };
        update.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        var list = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows");
        Assert.IsTrue(list!.Value.Any(value => value.Id == "direct-sql"));
        Assert.AreEqual(created.ScopeRef, list.Value.Single(value => value.Id == "direct-sql").ScopeRef);

        var openApi = await client.GetStringAsync("/openapi/v1.json");
        StringAssert.Contains(openApi, "flowKind");
        StringAssert.Contains(openApi, "DirectFlowDefinition");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/flows/direct-sql");
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/flows/direct-sql")).StatusCode);
    }

    [TestMethod]
    public async Task FlowApiAddressesHomonymousFlowsThroughNamespaceRoutes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var firstNamespace = new ResourceNamespace("team-a");
        var secondNamespace = new ResourceNamespace("team-b");
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        var first = new CreateFlowRequest("router", null, "1.0.0", true, definition) { Namespace = firstNamespace };
        var second = first with { Namespace = secondNamespace };

        using var firstResponse = await client.PostAsJsonAsync("/api/namespaces/team-a/flows/", first, JsonOptions);
        using var secondResponse = await client.PostAsJsonAsync("/api/namespaces/team-b/flows/", second, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, firstResponse.StatusCode, await firstResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Created, secondResponse.StatusCode, await secondResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("/api/flows/router")).StatusCode);

        var firstStored = await client.GetFromJsonAsync<FlowResponse>("/api/namespaces/team-a/flows/router", JsonOptions);
        var secondStored = await client.GetFromJsonAsync<FlowResponse>("/api/namespaces/team-b/flows/router", JsonOptions);
        Assert.AreEqual(firstNamespace, firstStored?.Namespace);
        Assert.AreEqual(secondNamespace, secondStored?.Namespace);
        var firstPage = await client.GetFromJsonAsync<FlowPageResponse>("/api/namespaces/team-a/flows", JsonOptions);
        Assert.IsTrue(firstPage?.Value.All(flow => flow.Namespace == firstNamespace));
    }

    [TestMethod]
    public async Task FlowApiListsVisibleOwnersRequiresExactScopeOnCollisionAndAllowsAuthorizedExactMutation()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context!.Context.PrincipalId, DateTimeOffset.UtcNow), default);
        client.DefaultRequestHeaders.Add(PrincipalResolutionMiddleware.WorkspaceHeader, context.Context.WorkspaceId.ToString("D"));
        var definition = GraphOnlyDefinition();
        var request = new CreateFlowRequest("scoped-collision", "Workspace", "1.0.0", true, definition);
        using var workspaceResponse = await client.PostAsJsonAsync("/api/flows", request, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, workspaceResponse.StatusCode);

        var service = factory.Services.GetRequiredService<FlowService>();
        using var instanceResponse = await client.PostAsJsonAsync("/api/flows",
            request with { Description = "Instance", ScopeRef = ResourceScopeRef.Instance }, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, instanceResponse.StatusCode, await instanceResponse.Content.ReadAsStringAsync());
        var instanceFlow = await instanceResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.AreEqual(ResourceScopeRef.Instance, instanceFlow?.ScopeRef);
        await service.PublishVersionExactAsync(ResourceScopeRef.Instance, new FlowId("scoped-collision"), "1.0.0", activate: true, default);
        var storedInstanceFlow = await service.GetExactAsync(
            ResourceScopeRef.Instance, new FlowId("scoped-collision"), default);
        Assert.IsNotNull(storedInstanceFlow);
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
        {
            _ = await factory.Services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(
                new ResourceAccessPolicy(
                    new(storedInstanceFlow.Value.Uid, ResourceScopeRef.Instance,
                        storedInstanceFlow.Value.Id.Namespace, FlowResourceKinds.Flow,
                        Version: storedInstanceFlow.Value.Version),
                    [new(ResourceScopeRef.Workspace(context.Context.WorkspaceId),
                        [ResourceAccessAction.Discover, ResourceAccessAction.Use])]),
                null,
                default);
        }

        var page = await client.GetFromJsonAsync<FlowPageResponse>("/api/flows?allNamespaces=true&top=200", JsonOptions);
        CollectionAssert.AreEquivalent(
            new[] { ResourceScopeKind.Workspace, ResourceScopeKind.Instance },
            page!.Value.Where(value => value.Id == "scoped-collision").Select(value => value.ScopeRef!.Value.Kind).ToArray());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("/api/flows/scoped-collision")).StatusCode);

        var exactPath = $"/api/flows/scoped-collision?scopeRef={Uri.EscapeDataString(ResourceScopeRef.Instance.Value)}";
        using var inheritedResponse = await client.GetAsync(exactPath);
        var inherited = await inheritedResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.AreEqual(ResourceScopeRef.Instance, inherited?.ScopeRef);
        client.DefaultRequestHeaders.Add(
            RequestContextMiddleware.ConsoleContextHeader,
            ResourceScopeRef.Instance.Value);
        using var mutation = new HttpRequestMessage(HttpMethod.Put, exactPath)
        {
            Content = JsonContent.Create(new UpdateFlowRequest("Changed", "1.0.0", true, definition), options: JsonOptions)
        };
        mutation.Headers.TryAddWithoutValidation("If-Match", inheritedResponse.Headers.ETag?.Tag);
        using var updatedResponse = await client.SendAsync(mutation);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode, await updatedResponse.Content.ReadAsStringAsync());
        var updated = await updatedResponse.Content.ReadFromJsonAsync<FlowResponse>(JsonOptions);
        Assert.AreEqual("Changed", updated?.Description);
        Assert.AreEqual(ResourceScopeRef.Instance, updated?.ScopeRef);
    }

    [TestMethod]
    public async Task FlowRunsAreIsolatedByTheirCompleteDurableScope()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("scoped-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            new NullFlowRunEventSink(), TimeProvider.System);
        var otherTenantScope = TestScope with { TenantId = Guid.Parse("44444444-4444-4444-4444-444444444444") };
        var otherPrincipalScope = TestScope with { PrincipalId = Guid.Parse("55555555-5555-5555-5555-555555555555") };
        var otherWorkspaceScope = TestScope with { WorkspaceId = new(Guid.Parse("66666666-6666-6666-6666-666666666666")) };
        var otherFlow = await fixture.Service.CreateAsync(otherWorkspaceScope.WorkspaceId, new CreateFlowCommand("scoped-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(otherWorkspaceScope.WorkspaceId, otherFlow.Value.Id, "1.0.0", true, default);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");

        var own = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "own", input.RootElement, TestScope, default);
        var otherTenant = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "other-tenant", input.RootElement, otherTenantScope, default);
        var otherPrincipal = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "other-principal", input.RootElement, otherPrincipalScope, default);
        var otherWorkspace = await runs.CreateAsync(otherFlow.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "other-workspace", input.RootElement, otherWorkspaceScope, default);
        async Task<StoredInputRequest> CreatePrivateInputAsync(StoredFlowRun run, string id) =>
            await fixture.Repository.CreateInputRequestAsync(new InputRequest
            {
                ScopeRef = run.Value.ExecutionScopeRef,
                Id = id,
                RunId = run.Value.Id,
                RuntimeRequestId = $"runtime-{id}",
                Prompt = "Private prompt",
                CreatedAt = Now,
                ExpiresAt = Now.AddMinutes(5)
            }, default);
        var otherTenantRequest = await CreatePrivateInputAsync(otherTenant, "other-tenant-input");
        var otherPrincipalRequest = await CreatePrivateInputAsync(otherPrincipal, "other-principal-input");
        var otherWorkspaceRequest = await CreatePrivateInputAsync(otherWorkspace, "other-workspace-input");

        Assert.IsNotNull(await runs.GetAsync(own.Value.Id, TestScope, default));
        Assert.IsNull(await runs.GetAsync(otherTenant.Value.Id, TestScope, default));
        Assert.IsNull(await runs.GetAsync(otherPrincipal.Value.Id, TestScope, default));
        Assert.IsNull(await runs.GetAsync(otherWorkspace.Value.Id, TestScope, default));
        var page = await runs.ListAsync(null, null, 0, 20, TestScope, default);
        CollectionAssert.AreEqual(new[] { own.Value.Id }, page.Items.Select(item => item.Value.Id).ToArray());
        CollectionAssert.AreEqual(new[] { otherPrincipalRequest.Value.Id },
            (await runs.ListInputsAsync(otherPrincipal.Value.Id, null, otherPrincipalScope, default)).Select(item => item.Value.Id).ToArray());
        Assert.AreEqual(otherPrincipalRequest.Value.Id,
            (await runs.GetInputAsync(otherPrincipal.Value.Id, otherPrincipalRequest.Value.Id, otherPrincipalScope, default))?.Value.Id);
        Assert.IsNotEmpty(await runs.ListEventsAsync(otherPrincipalScope, otherPrincipal.Value.Id, 0, default));

        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.ListInputsAsync(otherPrincipal.Value.Id, null, TestScope, default));
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.GetInputAsync(otherPrincipal.Value.Id, otherPrincipalRequest.Value.Id, TestScope, default));
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.ListEventsAsync(TestScope, otherPrincipal.Value.Id, 0, default));
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.CancelAsync(otherPrincipal.Value.Id, TestScope, default));
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.DeleteAsync(otherPrincipal.Value.Id, otherPrincipal.ETag, TestScope, default));
        Assert.IsNotNull(await fixture.Repository.GetRunAsync(otherPrincipalScope, otherPrincipal.Value.Id, default));
        await using var observation = runs.ObserveAsync(otherPrincipal.Value.Id, TestScope, default).GetAsyncEnumerator();
        await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => observation.MoveNextAsync().AsTask());

        foreach (var inaccessible in new[]
        {
            (Run: otherTenant, Request: otherTenantRequest),
            (Run: otherPrincipal, Request: otherPrincipalRequest),
            (Run: otherWorkspace, Request: otherWorkspaceRequest)
        })
        {
            await Assert.ThrowsExactlyAsync<FlowRunNotFoundException>(() => runs.RespondAsync(
                inaccessible.Run.Value.Id,
                inaccessible.Request.Value.Id,
                JsonSerializer.SerializeToElement("forbidden response"),
                TestScope.PrincipalId.ToString("D"),
                TestScope,
                default));
            var unchanged = await fixture.Repository.GetInputRequestAsync(
                inaccessible.Run.Value.WorkspaceId,
                inaccessible.Run.Value.Id,
                inaccessible.Request.Value.Id,
                default);
            Assert.IsNotNull(unchanged);
            Assert.AreEqual(InputRequestStatus.Pending, unchanged.Value.Status);
            Assert.IsNull(unchanged.Value.Response);
        }
    }

    [TestMethod]
    public async Task FlowRunObservationReturnsNotFoundBeforeStartingAnUnavailableStream()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/flowRuns/not-visible/events", HttpCompletionOption.ResponseHeadersRead);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task FlowRunAuthorizationIsRevalidatedBeforeWorkerMaterialIsIssued()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("revoked-run", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            new NullFlowRunEventSink(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "revoked", input.RootElement, TestScope, default);
        var assignment = new RuntimeWorkerAssignment
        {
            WorkspaceId = TestScope.WorkspaceId,
            TenantId = TestScope.TenantId,
            Id = new(Guid.NewGuid()),
            TargetKind = RuntimeAssignmentTargetKind.FlowRun,
            TargetRunId = pending.Value.Id,
            RuntimeCapability = "microsoft-agent-framework",
            RuntimeCapabilityVersion = "1.0",
            ExecutionMaterialVersion = RuntimeExecutionMaterialVersions.V1,
            ExecutionMaterialId = $"flow-run:{pending.Value.Id}",
            ExecutionMaterialDigest = "sha256:test",
            State = RuntimeAssignmentState.Pending,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        var resolver = new RuntimeExecutionMaterialResolver(null!, fixture.Repository, null!,
            new DeniedFlowRunExecutionScope(), null!, null!, null!,
            Options.Create(new FlowRunExecutionOptions()));

        var exception = await Assert.ThrowsExactlyAsync<FlowValidationException>(() =>
            resolver.ResolveAsync(assignment, default));

        Assert.AreEqual("flow_run_authorization_denied", exception.Code);
    }

    [TestMethod]
    public async Task FlowRunExecutionScopeCannotBeChangedAfterCreation()
    {
        await using var fixture = await FlowFixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(TestScope.WorkspaceId, new CreateFlowCommand("immutable-scope", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent"))), default);
        await fixture.Service.PublishVersionAsync(TestScope.WorkspaceId, created.Value.Id, "1.0.0", true, default);
        var runs = new FlowRunService(fixture.Repository, new TestFlowRunQueue(), new TestCancellationRegistry(),
            new NullFlowRunEventSink(), TimeProvider.System);
        using var input = JsonDocument.Parse("""{"prompt":"test"}""");
        var pending = await runs.CreateAsync(created.Value.Id, null, "local", FlowRunTrigger.Manual, "principal", "immutable", input.RootElement, TestScope, default);
        var changedScope = TestScope with { PrincipalId = Guid.Parse("55555555-5555-5555-5555-555555555555") };

        await Assert.ThrowsExactlyAsync<FlowConcurrencyException>(() => fixture.Repository.UpdateRunAsync(
            pending.Value with { Scope = changedScope }, pending.ETag, default));
    }

    [TestMethod]
    public async Task FlowRunApiCreatesAndListsRunsFromTheSameContract()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("api-run-flow", "API run", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows/api-run-flow/versions", new CreateFlowVersionRequest("1.0.0"))).StatusCode);
        using var input = JsonDocument.Parse("""{"prompt":"Explain SQL joins"}""");
        using var createdResponse = await client.PostAsJsonAsync("/api/flows/api-run-flow/runs",
            new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Accepted, createdResponse.StatusCode);
        var run = await createdResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.IsNotNull(run);
        Assert.AreEqual("1.0.0", run.FlowVersion);
        Assert.AreEqual(3, run.Steps.Count);
        var requestContext = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        Assert.AreEqual(new FlowRunScope(requestContext.TenantId, new(requestContext.WorkspaceId), requestContext.PrincipalId), run.Scope);
        var principal = await factory.Services.GetRequiredService<IIdentityStore>().GetPrincipalAsync(requestContext.PrincipalId, default);
        Assert.AreEqual(principal?.DisplayName, run.StartedBy);
        Assert.AreEqual(FlowInvocationOrigin.Api, run.InvocationOrigin);
        Assert.AreEqual(principal?.DisplayName, run.CallerId);
        Assert.IsNotNull(run.WorkItemResourceId);
        Assert.IsNull(run.ParentFlowRunId);
        Assert.AreEqual(0, run.NestingDepth);
        Assert.IsNull(typeof(CreateFlowRunRequest).GetProperty("StartedBy"));
        var detail = await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{run.Id}", JsonOptions);
        Assert.IsNotNull(detail?.TokenUsage);
        Assert.AreEqual(FlowRunTokenUsageState.Collecting, detail.TokenUsage.State);
        Assert.AreEqual(FlowRunTokenUsageCoverage.Unavailable, detail.TokenUsage.Direct.Coverage);
        Assert.AreEqual(0, detail.TokenUsage.Direct.InvocationCount);
        var global = await client.GetFromJsonAsync<FlowRunPageResponse>("/api/flowRuns", JsonOptions);
        Assert.IsTrue(global!.Value.Any(item => item.Id == run.Id));
        var scoped = await client.GetFromJsonAsync<FlowRunPageResponse>("/api/flows/api-run-flow/runs", JsonOptions);
        Assert.IsTrue(scoped!.Value.Any(item => item.Id == run.Id));
        var causality = await client.GetFromJsonAsync<FlowRunCausalityPageResponse>($"/api/flowRuns/{run.Id}/causality?top=1", JsonOptions);
        Assert.IsNotNull(causality);
        Assert.AreEqual(run.Id, causality.Origin.RootFlowRunId);
        Assert.AreEqual(FlowInvocationOrigin.Api, causality.Origin.InvocationOrigin);
        Assert.AreEqual(1, causality.TotalCount);
        Assert.HasCount(1, causality.Value);
        Assert.IsTrue(causality.Value[0].ResolvedFromActiveReference);
        Assert.IsNull(causality.NextLink);
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        Assert.Contains("/api/flowRuns/{runId}", routes);
        Assert.Contains("/api/flowRuns/{runId}/causality", routes);
        Assert.DoesNotContain("/flowRuns/{runId}", routes);
    }

    [TestMethod]
    public async Task FlowRunApiIdempotencyRecoversTheSameRootWorkAndRejectsConflicts()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("idempotent-api-flow", null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows/idempotent-api-flow/versions", new CreateFlowVersionRequest("1.0.0"))).StatusCode);

        static HttpRequestMessage Request(int value)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, "/api/flows/idempotent-api-flow/runs")
            {
                Content = JsonContent.Create(new CreateFlowRunRequest(JsonSerializer.SerializeToElement(new { value })), options: JsonOptions)
            };
            message.Headers.Add("Idempotency-Key", "api-request-42");
            message.Headers.Add("X-Causation-Id", "external-request-42");
            return message;
        }

        using var firstRequest = Request(1);
        using var firstResponse = await client.SendAsync(firstRequest);
        using var secondRequest = Request(1);
        using var secondResponse = await client.SendAsync(secondRequest);
        Assert.AreEqual(HttpStatusCode.Accepted, firstResponse.StatusCode, await firstResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.Accepted, secondResponse.StatusCode, await secondResponse.Content.ReadAsStringAsync());
        var first = await firstResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        var second = await secondResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.AreEqual(first?.Id, second?.Id);
        Assert.AreEqual("api-request-42", first?.IdempotencyKey);
        Assert.AreEqual("external-request-42", first?.CausationId);

        var workspace = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var work = factory.Services.GetRequiredService<IWorkItemRepository>();
        Assert.HasCount(1, (await work.QueryAsync(new WorkItemQuery(new WorkspaceId(workspace.WorkspaceId), workspace.PrincipalId, Type: "flow-api"), default)).Items);

        using var conflictingRequest = Request(2);
        using var conflict = await client.SendAsync(conflictingRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStreamAsync());
        Assert.AreEqual("flow_invocation_idempotency_conflict", problem.RootElement.GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task FlowRunDeleteApiRequiresTerminalStatusAndCurrentETag()
    {
        var queue = new TestFlowRunQueue();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlowRunQueue>();
                services.AddSingleton<IFlowRunQueue>(queue);
            });
        });
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("delete-api-flow", "Delete API flow", "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "sql-expert")));
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/flows/delete-api-flow/versions", new CreateFlowVersionRequest("1.0.0"), JsonOptions)).StatusCode);
        using var input = JsonDocument.Parse("""{"prompt":"delete me"}""");
        using var createdResponse = await client.PostAsJsonAsync("/api/flows/delete-api-flow/runs", new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
        var created = await createdResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.IsNotNull(created);
        var createdETag = createdResponse.Headers.ETag?.Tag;
        Assert.IsNotNull(createdETag);

        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/flowRuns/{created.Id}")).StatusCode);
        using var pendingDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/flowRuns/{created.Id}");
        pendingDelete.Headers.TryAddWithoutValidation("If-Match", createdETag);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.SendAsync(pendingDelete)).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync($"/api/flowRuns/{created.Id}/cancel", null)).StatusCode);
        using var currentResponse = await client.GetAsync($"/api/flowRuns/{created.Id}");
        var currentETag = currentResponse.Headers.ETag?.Tag;
        Assert.IsNotNull(currentETag);
        using var staleDelete = new HttpRequestMessage(HttpMethod.Delete, $"/api/flowRuns/{created.Id}");
        staleDelete.Headers.TryAddWithoutValidation("If-Match", createdETag);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, (await client.SendAsync(staleDelete)).StatusCode);
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/flowRuns/{created.Id}");
        delete.Headers.TryAddWithoutValidation("If-Match", currentETag);
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/api/flowRuns/{created.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task FlowRunInputApiSupportsEveryInteractionTypeConflictAndExpiration()
    {
        var clock = new AdvancingTimeProvider(new DateTimeOffset(2026, 8, 17, 8, 0, 0, TimeSpan.Zero));
        var queue = new TestFlowRunQueue();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlowRunQueue>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<IFlowRunQueue>(queue);
                services.AddSingleton<TimeProvider>(clock);
            });
        });
        using var client = factory.CreateClient();
        var definition = new CreateFlowRequest("interactive-api-flow", "Interactive API", "1.0.0", true,
            new OrchestrationFlowDefinition(
                [
                    new FlowTargetReference(FlowTargetKind.Agent, "sql-expert"),
                    new FlowTargetReference(FlowTargetKind.Agent, "dotnet-expert")
                ],
                new SequentialOrchestrationPattern()));
        Assert.AreEqual(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/flows", definition, JsonOptions)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created,
            (await client.PostAsJsonAsync("/api/flows/interactive-api-flow/versions", new CreateFlowVersionRequest("1.0.0"))).StatusCode);
        var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var principalId = current.PrincipalId.ToString("D");
        var repository = factory.Services.GetRequiredService<IFlowRepository>();

        var cases = new[]
        {
            new InteractionApiCase("text", InputRequestType.Text, Array.Empty<string>(), JsonSerializer.SerializeToElement("Ada"), JsonSerializer.SerializeToElement("")),
            new InteractionApiCase("choice", InputRequestType.Choice, new[] { "red", "blue" }, JsonSerializer.SerializeToElement("blue"), JsonSerializer.SerializeToElement("green")),
            new InteractionApiCase("confirmation", InputRequestType.Confirmation, Array.Empty<string>(), JsonSerializer.SerializeToElement(true), JsonSerializer.SerializeToElement("yes"))
        };

        foreach (var interaction in cases)
        {
            using var input = JsonDocument.Parse($$"""{"kind":"{{interaction.Kind}}"}""");
            using var createResponse = await client.PostAsJsonAsync("/api/flows/interactive-api-flow/runs",
                new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Accepted, createResponse.StatusCode);
            var run = await createResponse.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
            Assert.IsNotNull(run);
            var stored = await repository.GetRunAsync(run.WorkspaceId, run.Id, default);
            Assert.IsNotNull(stored);
            await PutWaitingForInputAsync(repository, stored, clock,
                interaction.Type, interaction.Options);

            var pending = await client.GetFromJsonAsync<InputRequest[]>(
                $"/api/flowRuns/{run.Id}/inputs?status=Pending", JsonOptions);
            Assert.IsNotNull(pending);
            Assert.HasCount(1, pending);
            var request = pending[0];
            Assert.AreEqual(interaction.Type, request.Type);
            CollectionAssert.AreEqual(interaction.Options.ToArray(), request.Options.ToArray());
            using var detailResponse = await client.GetAsync($"/api/flowRuns/{run.Id}/inputs/{request.Id}");
            Assert.AreEqual(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.IsNotNull(detailResponse.Headers.ETag);

            using var invalidResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.InvalidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

            using var acceptedResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.ValidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Accepted, acceptedResponse.StatusCode);
            var answered = await acceptedResponse.Content.ReadFromJsonAsync<InputRequest>(JsonOptions);
            Assert.AreEqual(InputRequestStatus.Answered, answered!.Status);
            Assert.AreEqual(principalId, answered.Response!.PrincipalId);

            using var duplicateResponse = await client.PostAsJsonAsync(
                $"/api/flowRuns/{run.Id}/inputs/{request.Id}/response",
                new SubmitInputResponseRequest(interaction.ValidValue), JsonOptions);
            Assert.AreEqual(HttpStatusCode.Conflict, duplicateResponse.StatusCode);
        }

        using var expiringInput = JsonDocument.Parse("""{"kind":"text"}""");
        var expiringCreate = await client.PostAsJsonAsync("/api/flows/interactive-api-flow/runs",
            new CreateFlowRunRequest(expiringInput.RootElement.Clone()), JsonOptions);
        var expiringRun = await expiringCreate.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        var expiringStored = await repository.GetRunAsync(expiringRun!.WorkspaceId, expiringRun.Id, default);
        Assert.IsNotNull(expiringStored);
        await PutWaitingForInputAsync(repository, expiringStored, clock);
        var expiringRequest = (await client.GetFromJsonAsync<InputRequest[]>(
            $"/api/flowRuns/{expiringRun.Id}/inputs?status=Pending", JsonOptions))!.Single();
        clock.Advance(TimeSpan.FromDays(8));

        using var expiredResponse = await client.PostAsJsonAsync(
            $"/api/flowRuns/{expiringRun.Id}/inputs/{expiringRequest.Id}/response",
            new SubmitInputResponseRequest(JsonSerializer.SerializeToElement("too late")), JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, expiredResponse.StatusCode);
        var expired = await client.GetFromJsonAsync<InputRequest>(
            $"/api/flowRuns/{expiringRun.Id}/inputs/{expiringRequest.Id}", JsonOptions);
        Assert.AreEqual(InputRequestStatus.Expired, expired!.Status);
        var timedOut = await client.GetFromJsonAsync<FlowRun>($"/api/flowRuns/{expiringRun.Id}", JsonOptions);
        Assert.AreEqual(FlowRunStatus.TimedOut, timedOut!.Status);
    }

    [TestMethod]
    public async Task FlowRunPaginationPreservesRouteNamespaceAndFiltersAcrossPages()
    {
        var queue = new TestFlowRunQueue();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IFlowRunQueue>();
                services.AddSingleton<IFlowRunQueue>(queue);
            });
        });
        using var client = factory.CreateClient();
        var current = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var scope = new FlowRunScope(current.TenantId, new(current.WorkspaceId), current.PrincipalId);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var runs = factory.Services.GetRequiredService<FlowRunService>();
        var definition = GraphOnlyDefinition();
        var defaultFlow = await flows.CreateAsync(scope.WorkspaceId, new CreateFlowCommand("paged-default", null, "1.0.0", true, definition), default);
        var namespacedFlow = await flows.CreateAsync(scope.WorkspaceId, new CreateFlowCommand("paged-namespaced", null, "1.0.0", true, definition), new ResourceNamespace("team-a"), default);
        await flows.PublishVersionAsync(scope.WorkspaceId, defaultFlow.Value.Id, "1.0.0", true, default);
        await flows.PublishVersionAsync(scope.WorkspaceId, namespacedFlow.Value.Id, "1.0.0", true, default);
        using var input = JsonDocument.Parse("{}");
        for (var index = 0; index < 3; index++)
        {
            await runs.CreateAsync(defaultFlow.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", $"default-{index}", input.RootElement, scope, default);
            await runs.CreateAsync(namespacedFlow.Value.Id, null, "local", FlowRunTrigger.Manual, "tester", $"namespaced-{index}", input.RootElement, scope, default);
        }

        var namespaced = await client.GetFromJsonAsync<FlowRunPageResponse>(
            "/api/namespaces/team-a/flows/paged-namespaced/runs?status=Pending&top=1", JsonOptions);
        Assert.IsNotNull(namespaced?.NextLink);
        StringAssert.StartsWith(namespaced.NextLink, "/api/namespaces/team-a/flows/paged-namespaced/runs?");
        StringAssert.Contains(namespaced.NextLink, "status=Pending");
        var namespacedIds = new List<string>(namespaced.Value.Select(value => value.Id));
        var next = namespaced.NextLink;
        while (next is not null)
        {
            var page = await client.GetFromJsonAsync<FlowRunPageResponse>(next, JsonOptions);
            Assert.IsNotNull(page);
            namespacedIds.AddRange(page.Value.Select(value => value.Id));
            next = page.NextLink;
        }
        Assert.HasCount(3, namespacedIds);

        var global = await client.GetFromJsonAsync<FlowRunPageResponse>(
            "/api/flowRuns?flowId=paged-default&status=Pending&top=1", JsonOptions);
        Assert.IsNotNull(global?.NextLink);
        StringAssert.StartsWith(global.NextLink, "/api/flowRuns?");
        StringAssert.Contains(global.NextLink, "flowId=paged-default");
        StringAssert.Contains(global.NextLink, "status=Pending");
    }

    [TestMethod]
    public void FlowRoutesRegisterConsoleAndNamespacedDraftEndpoints()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        var routes = factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(pattern => pattern is not null)
            .ToArray();

        Assert.Contains("/flow-runs", routes);
        Assert.Contains("/flow-runs/{RunId}", routes);
        Assert.Contains("/api/flowRuns/{runId}", routes);
        Assert.DoesNotContain("/flowRuns/{runId}", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/draft", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/validate", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/draft/source", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/publish", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/draft/runs", routes);
        Assert.Contains("/api/namespaces/{namespace}/flows/{id}/versions/{version}/draft", routes);
    }

    [TestMethod]
    public async Task DraftApiValidatesInputRunsImmutableSnapshotPublishesAndRoundTripsYaml()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        using var createdResponse = await client.PostAsJsonAsync("/api/flows/drafts",
            new CreateFlowDraftRequest("designer-api-flow", "Designer API Flow", Template: "AgentRouting"), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var draft = await createdResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.IsNotNull(draft);
        Assert.AreEqual(1L, draft.Value.Revision);
        Assert.AreEqual(6, Enum.GetValues<FlowNodeKind>().Count(kind => kind is FlowNodeKind.Input or FlowNodeKind.Agent or FlowNodeKind.Router or FlowNodeKind.Condition or FlowNodeKind.Transform or FlowNodeKind.Output));

        using var validationResponse = await client.PostAsync("/api/flows/designer-api-flow/validate", null);
        Assert.AreEqual(HttpStatusCode.OK, validationResponse.StatusCode);
        var validation = await validationResponse.Content.ReadFromJsonAsync<FlowValidationResponse>(JsonOptions);
        Assert.IsTrue(validation!.IsValid, string.Join(Environment.NewLine, validation.Issues.Select(issue => issue.Message)));

        var source = await client.GetFromJsonAsync<FlowSourceResponse>("/api/flows/designer-api-flow/draft/source?format=yaml", JsonOptions);
        Assert.IsNotNull(source);
        StringAssert.Contains(source.Source, "entryStep:");
        StringAssert.Contains(source.Source, "type: router");
        using var replaceSource = new HttpRequestMessage(HttpMethod.Put, "/api/flows/designer-api-flow/draft/source")
        {
            Content = JsonContent.Create(new ReplaceFlowSourceRequest(source.Source, "yaml", "source-test"), options: JsonOptions)
        };
        replaceSource.Headers.TryAddWithoutValidation("If-Match", draft.ETag);
        using var replacedResponse = await client.SendAsync(replaceSource);
        Assert.AreEqual(HttpStatusCode.OK, replacedResponse.StatusCode);
        var replaced = await replacedResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.AreEqual(2L, replaced!.Value.Revision);
        using var staleSource = new HttpRequestMessage(HttpMethod.Put, "/api/flows/designer-api-flow/draft/source")
        {
            Content = JsonContent.Create(new ReplaceFlowSourceRequest(source.Source, "yaml", "source-test"), options: JsonOptions)
        };
        staleSource.Headers.TryAddWithoutValidation("If-Match", draft.ETag);
        using var staleResponse = await client.SendAsync(staleSource);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);

        using var missingInput = JsonDocument.Parse("{}");
        using var rejectedRun = await client.PostAsJsonAsync("/api/flows/designer-api-flow/draft/runs", new CreateFlowRunRequest(missingInput.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.BadRequest, rejectedRun.StatusCode);

        using var input = JsonDocument.Parse("""{"prompt":"Review this SQL query"}""");
        using var acceptedRun = await client.PostAsJsonAsync("/api/flows/designer-api-flow/draft/runs", new CreateFlowRunRequest(input.RootElement.Clone()), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Accepted, acceptedRun.StatusCode);
        var run = await acceptedRun.Content.ReadFromJsonAsync<FlowRun>(JsonOptions);
        Assert.AreEqual(FlowDefinitionState.Draft, run!.DefinitionState);
        Assert.AreEqual(2L, run.DraftRevision);
        Assert.IsFalse(string.IsNullOrWhiteSpace(run.DefinitionHash));
        Assert.IsFalse(string.IsNullOrWhiteSpace(run.DefinitionSnapshotId));

        using var publishResponse = await client.PostAsJsonAsync("/api/flows/designer-api-flow/publish", new PublishFlowDraftRequest("1.0.0", "Initial designer release"), JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, publishResponse.StatusCode);
        var version = await publishResponse.Content.ReadFromJsonAsync<FlowVersionResponse>(JsonOptions);
        Assert.AreEqual("Initial designer release", version!.ReleaseNotes);
        Assert.IsNotNull(version.Graph);
        Assert.AreNotEqual(replaced.Value.DefinitionHash, version.DefinitionHash,
            "Publishing must hash the graph after Agent references are pinned to immutable generations.");
        var routing = Assert.IsInstanceOfType<RoutingFlowDefinition>(version.Definition);
        CollectionAssert.AreEqual(new[] { "sql-expert", "dotnet-expert" }, routing.Destinations.Select(target => target.Id).ToArray());
        Assert.AreEqual("dotnet-expert", routing.Fallback?.Id);
        Assert.IsTrue(routing.Destinations.All(target => target.ScopeRef?.Kind == ResourceScopeKind.Workspace
            && long.TryParse(target.Version, out var generation) && generation > 0));
        Assert.IsTrue(routing.Fallback?.ScopeRef?.Kind == ResourceScopeKind.Workspace);
        Assert.IsTrue(long.TryParse(routing.Fallback?.Version, out var fallbackGeneration) && fallbackGeneration > 0);

        var current = await client.GetFromJsonAsync<FlowResponse>("/api/flows/designer-api-flow", JsonOptions);
        Assert.IsNotNull(current);
        Assert.IsNotNull(current.Graph);
        Assert.AreEqual("input", current.Graph.EntryStep);
        Assert.AreEqual(version.Graph.Steps.Count, current.Graph.Steps.Count);

        using var recreateResponse = await client.PostAsync("/api/flows/designer-api-flow/versions/1.0.0/draft", null);
        Assert.AreEqual(HttpStatusCode.OK, recreateResponse.StatusCode);
        var recreated = await recreateResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.AreEqual(3L, recreated!.Value.Revision);
    }

    [TestMethod]
    public async Task DraftApiPreservesInstanceOwnerScopeAcrossDesignerOperations()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        await factory.Services.GetRequiredService<IIdentityStore>().AddPlatformAdministratorAsync(
            new PlatformAdministrator(context!.Context.PrincipalId, DateTimeOffset.UtcNow), default);
        client.DefaultRequestHeaders.Add(PrincipalResolutionMiddleware.WorkspaceHeader, context.Context.WorkspaceId.ToString("D"));
        client.DefaultRequestHeaders.Add(
            RequestContextMiddleware.ConsoleContextHeader,
            ResourceScopeRef.Instance.Value);
        var request = new CreateFlowDraftRequest("instance-designer-flow", "Instance Designer Flow", Template: "Empty")
        {
            ScopeRef = ResourceScopeRef.Instance
        };

        using var createdResponse = await client.PostAsJsonAsync("/api/flows/drafts", request, JsonOptions);
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode, await createdResponse.Content.ReadAsStringAsync());
        var draft = await createdResponse.Content.ReadFromJsonAsync<FlowDraftResponse>(JsonOptions);
        Assert.AreEqual(ResourceScopeRef.Instance, draft?.Value.ScopeRef);

        var scope = Uri.EscapeDataString(ResourceScopeRef.Instance.Value);
        var stored = await client.GetFromJsonAsync<FlowResponse>($"/api/flows/instance-designer-flow?scopeRef={scope}", JsonOptions);
        Assert.AreEqual(ResourceScopeRef.Instance, stored?.ScopeRef);
        var source = await client.GetFromJsonAsync<FlowSourceResponse>($"/api/flows/instance-designer-flow/draft/source?scopeRef={scope}&format=yaml", JsonOptions);
        Assert.IsNotNull(source);
        StringAssert.Contains(source.Source, "entryStep:");

        using var validationResponse = await client.PostAsync($"/api/flows/instance-designer-flow/validate?scopeRef={scope}", null);
        Assert.AreEqual(HttpStatusCode.OK, validationResponse.StatusCode, await validationResponse.Content.ReadAsStringAsync());
    }

    private static WorkflowFlowDefinition GraphOnlyDefinition() => new(
        "input",
        [new FlowNode("input", FlowNodeKind.Input)],
        []);
}

