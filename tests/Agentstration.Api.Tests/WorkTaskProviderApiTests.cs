extern alias UtilitiesExtension;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Identity.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Security.Contracts;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AepEnrollmentMode = Agentstration.Extensions.Contracts.AepEnrollmentMode;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class WorkTaskProviderApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task AepTaskProviderDiscoveryPublicationRecoveryAndReceiptsUseCanonicalPrivateWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "task-provider-api-" + Guid.NewGuid().ToString("N"));
        var state = new ProviderState { LoseResponse = true };
        await using var extension = new WebApplicationFactory<UtilitiesExtension::Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(state);
            services.AddMcpServer().WithTools<ProviderTools>();
            services.Configure<AepExtensionOptions>(options => options.Tools.Add(Contribution("task.operations")));
        }));
        _ = extension.CreateClient();
        var root = Guid.NewGuid(); var first = Guid.NewGuid(); var second = Guid.NewGuid(); var planId = Guid.NewGuid(); var projectId = Guid.NewGuid();
        var operationId = Guid.NewGuid(); var prefix = $"/api/workspaces/default/projects/{projectId}";
        try
        {
            BeginWorkTaskOperationRequest command;
            await using (var host = Factory(directory, extension))
            {
                using var http = host.CreateClient();
                var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
                var provider = await InstallProviderAsync(host.Services, context);
                using var tree = await http.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(root,
                    [new(root, WorkTaskKind.Composite, "Parent", "Deliver parent work"), new(first, WorkTaskKind.Atomic, "First", "First internal task", root),
                        new(second, WorkTaskKind.Atomic, "Second", "Second internal task", root)]));
                Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
                using var planResponse = await http.PostAsJsonAsync("/api/workspaces/default/work-plans", new CreateWorkPlanRequest(planId, Guid.NewGuid(), new("Parent delivery", [root, first, second], [])));
                Assert.AreEqual(HttpStatusCode.Created, planResponse.StatusCode);
                var plan = (await planResponse.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
                var binding = new WorkProjectContextBinding("tracker", WorkProjectBindingPurpose.TaskOperations,
                    new(ScopedResourceAddress.Create(provider.ScopeRef!.Value, provider.Namespace, provider.Kind, provider.Name), provider.Uid, provider.Generation));
                using var project = await http.PostAsJsonAsync("/api/workspaces/default/projects", new CreateWorkProjectRequest(projectId, Guid.NewGuid(), "external-work",
                    new("External work", null, [planId], []) { Bindings = [binding] }));
                Assert.AreEqual(HttpStatusCode.Created, project.StatusCode, await project.Content.ReadAsStringAsync());
                using var discovery = await http.GetAsync(prefix + "/task-provider?bindingName=tracker&operation=Publish");
                Assert.AreEqual(HttpStatusCode.OK, discovery.StatusCode, await discovery.Content.ReadAsStringAsync());
                var route = (await discovery.Content.ReadFromJsonAsync<WorkTaskProviderRoute>(Json))!;
                Assert.Contains(ExternalTaskOperationKind.Publish, route.Capabilities.Operations);
                Assert.IsTrue(route.Capabilities.IdempotentOperations && route.Capabilities.OperationRecovery);
                Assert.AreEqual(provider.Uid, route.Binding.Target.Uid);
                command = new(operationId, 1, "tracker", new(ExternalTaskOperationKind.Publish, "fixture/repository", Title: "Parent issue", Body: "Children remain local"),
                    planId, plan.CurrentRevision, plan.Revision.Digest);
                using var published = await http.PostAsJsonAsync(prefix + "/task-operations", command);
                Assert.AreEqual(HttpStatusCode.Accepted, published.StatusCode, await published.Content.ReadAsStringAsync());
                var pending = (await published.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!;
                Assert.AreEqual(WorkTaskOperationStatus.Uncertain, pending.Status);
                Assert.AreEqual(1, state.Publications);
                var unchanged = (await http.GetFromJsonAsync<WorkPlanResponse>($"/api/workspaces/default/work-plans/{planId}"))!;
                Assert.AreEqual(WorkPlanStatus.Draft, unchanged.Status);
                Assert.HasCount(0, unchanged.Attempts);
            }
            await using (var restarted = Factory(directory, extension))
            {
                using var http = restarted.CreateClient();
                using var resumed = await http.PostAsync(prefix + $"/task-operations/{operationId}/resume", null);
                Assert.AreEqual(HttpStatusCode.Accepted, resumed.StatusCode, await resumed.Content.ReadAsStringAsync());
                var receipt = (await resumed.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!;
                Assert.AreEqual(WorkTaskOperationStatus.Succeeded, receipt.Status);
                Assert.AreEqual("1", receipt.Task!.TaskId);
                Assert.AreEqual(1, state.Publications);
                Assert.AreEqual(1, state.Recoveries);
                using var replay = await http.PostAsJsonAsync(prefix + "/task-operations", command);
                Assert.AreEqual(HttpStatusCode.Accepted, replay.StatusCode);
                Assert.AreEqual(receipt.Version, (await replay.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!.Version);
                using var conflict = await http.PostAsJsonAsync(prefix + "/task-operations", command with { Input = command.Input with { Title = "Different intent" } });
                Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);
                var page = (await http.GetFromJsonAsync<WorkTaskOperationPageResponse>(prefix + "/task-operations?take=1"))!;
                Assert.HasCount(1, page.Items); Assert.IsFalse(page.HasMore);
                using var foreign = await http.GetAsync($"/api/workspaces/{Guid.NewGuid()}/projects/{projectId}/task-operations/{operationId}");
                Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
                using var wrongProject = await http.GetAsync($"/api/workspaces/default/projects/{Guid.NewGuid()}/task-operations/{operationId}");
                Assert.AreEqual(HttpStatusCode.NotFound, wrongProject.StatusCode);
                using var missing = await http.GetAsync(prefix + "/task-provider?bindingName=absent&operation=Publish");
                Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
                var context = await restarted.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
                using var scope = restarted.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
                var audit = await restarted.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(200, default);
                Assert.IsTrue(audit.Any(value => value.Action == SecurityAuditActions.WorkTaskProviderInvoked && value.ReasonCode == receipt.OperationKey
                    && value.ActorPrincipalId == context.PrincipalId && value.WorkspaceId == context.WorkspaceId));
                // A second capability declaration cannot silently change the selected operation.
                extension.Services.GetRequiredService<IOptions<AepExtensionOptions>>().Value.Tools.Add(Contribution("task.duplicate"));
                var tools = restarted.Services.GetRequiredService<ToolManagementService>();
                await tools.RefreshDiscoveryExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "task-fixture", default, default);
                var duplicate = (await tools.ListVisibleToolsAsync(ResourceScopeRef.Workspace(context.WorkspaceId), default))
                    .Single(value => value.Value.Definition.ExternalId == "task.duplicate");
                await tools.SetToolEnabledAsync(duplicate.Value.Name, true, (await tools.GetToolOperationalStateAsync(duplicate.Value, default))?.ETag, default);
                using var stale = await http.GetAsync(prefix + "/task-provider?bindingName=tracker&operation=Publish");
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
                var provider = (await tools.GetProviderExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "task-fixture", default, default))!.Value;
                var project = (await http.GetFromJsonAsync<WorkProjectResponse>(prefix))!;
                var refreshed = project.Bindings[0] with { Target = project.Bindings[0].Target with { Generation = provider.Generation } };
                using var updated = await http.PutAsJsonAsync(prefix, new UpdateWorkProjectRequest(Guid.NewGuid(), project.Version,
                    new(project.DisplayName, project.Description, project.PlanIds, project.TaskIds) { Bindings = [refreshed] }));
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
                using var ambiguous = await http.GetAsync(prefix + "/task-provider?bindingName=tracker&operation=Publish");
                Assert.AreEqual(HttpStatusCode.BadRequest, ambiguous.StatusCode);
                StringAssert.Contains(await ambiguous.Content.ReadAsStringAsync(), "task_provider_capability_ambiguous");
                var options = extension.Services.GetRequiredService<IOptions<AepExtensionOptions>>().Value;
                foreach (var old in options.Tools.Where(value => value.Id.StartsWith("task.", StringComparison.Ordinal)).ToArray()) options.Tools.Remove(old);
                options.Tools.Add(Contribution("task.operations", [ExternalTaskOperationKind.Read]));
                await tools.RefreshDiscoveryExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "task-fixture", default, default);
                provider = (await tools.GetProviderExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "task-fixture", default, default))!.Value;
                project = (await http.GetFromJsonAsync<WorkProjectResponse>(prefix))!;
                refreshed = project.Bindings[0] with { Target = project.Bindings[0].Target with { Generation = provider.Generation } };
                using var updatedAgain = await http.PutAsJsonAsync(prefix, new UpdateWorkProjectRequest(Guid.NewGuid(), project.Version,
                    new(project.DisplayName, project.Description, project.PlanIds, project.TaskIds) { Bindings = [refreshed] }));
                Assert.AreEqual(HttpStatusCode.OK, updatedAgain.StatusCode);
                using var unsupported = await http.GetAsync(prefix + "/task-provider?bindingName=tracker&operation=Publish");
                Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);
                StringAssert.Contains(await unsupported.Content.ReadAsStringAsync(), "task_provider_capability_missing");
                Assert.AreEqual(1, state.Publications);
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static WebApplicationFactory<global::Program> Factory(string directory, WebApplicationFactory<UtilitiesExtension::Program> extension) =>
        new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", directory);
            builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic");
            builder.UseSetting("Agentstration:Extensions:DiscoverOnStartup", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning");
            builder.ConfigureTestServices(services => services.AddSingleton<IHttpMessageHandlerFactory>(new HandlerFactory(extension.Server.CreateHandler)));
        });
    private sealed class HandlerFactory(Func<HttpMessageHandler> create) : IHttpMessageHandlerFactory
    { public HttpMessageHandler CreateHandler(string name) => create(); }
    private static AepToolContribution Contribution(string id, IReadOnlyList<ExternalTaskOperationKind>? operations = null) => new(id, "Fixture task operations", new("utilities", "task_operation"),
        Metadata: new Dictionary<string, JsonElement> { [WorkTaskProviderProtocol.MetadataKey] = JsonSerializer.SerializeToElement(new WorkTaskProviderCapabilities(
            WorkTaskProviderProtocol.Version, operations ?? [ExternalTaskOperationKind.Read, ExternalTaskOperationKind.Update, ExternalTaskOperationKind.Publish], true, true), Json) });
    private static async Task<ToolProviderResource> InstallProviderAsync(IServiceProvider services, RequestContext context)
    {
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        await services.GetRequiredService<IResourceStore>().PutExactAsync(scope, new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ExtensionKinds.ExtensionRegistration, Metadata = new() { Name = "task-fixture" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Task fixture", Endpoint = new("http://task-fixture/"), ExpectedExtensionId = "Agentstration.Extensions.Utilities",
                Enabled = true, AuthenticationMode = AepTransportAuthenticationMode.None, EnrollmentMode = AepEnrollmentMode.PairingCode }
        }, null, true, default);
        var tools = services.GetRequiredService<ToolManagementService>();
        _ = await tools.PutProviderAsync(new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new() { Name = "task-fixture" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Task fixture", ProviderType = ToolProviderType.Aep, Aep = new() { Extension = new("task-fixture", scope, ResourceNamespace.Default) } }
        }, null, true, default);
        await tools.RefreshDiscoveryExactAsync(scope, "task-fixture", default, default);
        var tool = (await tools.ListVisibleToolsAsync(scope, default)).Single(value => value.Value.Definition.ExternalId == "task.operations");
        await tools.SetToolEnabledAsync(tool.Value.Name, true, (await tools.GetToolOperationalStateAsync(tool.Value, default))?.ETag, default);
        return (await tools.GetProviderExactAsync(scope, "task-fixture", default, default))!.Value;
    }

    public sealed class ProviderState
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<string, ExternalTaskProviderResponse> Receipts = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, ExternalTaskSnapshot> Tasks = new(StringComparer.Ordinal);
        public int Updates { get; internal set; }
        public int Publications { get; internal set; }
        public int Recoveries { get; internal set; }
        public bool LoseResponse { get; set; }
    }
    [McpServerToolType]
    public sealed class ProviderTools(ProviderState state)
    {
        [McpServerTool(Name = "task_operation", UseStructuredContent = true)]
        public ExternalTaskProviderResponse Invoke(string mode, string operationKey, string intentDigest, ExternalTaskOperationInput input)
        {
            lock (state.Gate)
            {
                if (mode == "recover")
                {
                    state.Recoveries++;
                    return state.Receipts.TryGetValue(operationKey, out var existing) ? existing : new(operationKey, intentDigest, ExternalTaskProviderStatus.Missing);
                }
                if (state.Receipts.TryGetValue(operationKey, out var prior)) return prior.IntentDigest == intentDigest ? prior
                    : new(operationKey, intentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "operation_conflict");
                if (mode != "execute" || input.Repository != "fixture/repository") return new(operationKey, intentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "repository_denied");
                ExternalTaskSnapshot task;
                if (input.Operation == ExternalTaskOperationKind.Publish)
                {
                    state.Publications++;
                    task = new(input.Repository, state.Publications.ToString(System.Globalization.CultureInfo.InvariantCulture), "revision-1", input.Title!, input.Body,
                        "open", new("https://fixture.test/issues/1"), [], []);
                    state.Tasks.Add(task.TaskId, task);
                }
                else if (!state.Tasks.TryGetValue(input.TaskId!, out task!))
                    return new(operationKey, intentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "task_not_found");
                else if (input.Operation == ExternalTaskOperationKind.Update)
                {
                    if (input.ExpectedRevision != task.Revision) return new(operationKey, intentDigest, ExternalTaskProviderStatus.Rejected, ErrorCode: "remote_revision_conflict");
                    state.Updates++;
                    task = task with { Revision = "revision-" + (state.Updates + 1), Title = input.Title ?? task.Title, Body = input.Body ?? task.Body,
                        State = input.State ?? task.State, Labels = input.Labels ?? task.Labels, Relations = input.Relations ?? task.Relations };
                    state.Tasks[task.TaskId] = task;
                }
                var response = new ExternalTaskProviderResponse(operationKey, intentDigest, ExternalTaskProviderStatus.Succeeded, task);
                state.Receipts.Add(operationKey, response);
                if (state.LoseResponse) { state.LoseResponse = false; throw new McpException("Injected response loss after publication."); }
                return response;
            }
        }
    }
}
