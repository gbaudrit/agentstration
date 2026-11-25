extern alias codingworker;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Awp.Abstractions;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Runtime.Worker.Core;
using Agentstration.Testing;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Native = codingworker::Agentstration.Runtime.Worker.Coding;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class CodingWorkerApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow("success", WorkPlanAttemptStatus.Completed)]
    [DataRow("partial", WorkPlanAttemptStatus.Completed)]
    [DataRow("no-usage", WorkPlanAttemptStatus.Completed)]
    [DataRow("fail", WorkPlanAttemptStatus.Failed)]
    [DataRow("cancel", WorkPlanAttemptStatus.Cancelled)]
    public async Task RealAwpDispatchRetainsNativePlacementConsumptionAndFailedOrCancelledEvidence(string mode, WorkPlanAttemptStatus expected)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using var fixture = await Fixture.CreateAsync(mode);
        var plan = await fixture.StartPlanAsync();
        await using var incompatible = await AwpTestWorker.StartAsync(fixture.Host, false, capabilities:
            [new(CodingExecutionProtocol.RuntimeCapability, CodingExecutionProtocol.CapabilityVersion, ["99.0"])]);
        Assert.IsNull((await incompatible.ClaimOnceAsync()).Assignment, "An incompatible material version must not receive the Run.");
        await using var worker = await fixture.WorkerAsync();
        if (mode == "cancel")
        {
            await fixture.WaitAssignmentAsync(plan.Attempts.Single().FlowRunId, value => value.ExecutionEvents.Any(item =>
                item.Payload is { } payload && payload.TryGetProperty("codingConsumption", out _)));
            plan = (await fixture.Http.GetFromJsonAsync<WorkPlanResponse>(fixture.PlanUrl))!;
            using var cancelled = await fixture.Http.PostAsJsonAsync(fixture.PlanUrl + "/execution/cancel", new ControlWorkPlanExecutionRequest(Guid.NewGuid(), plan.Version));
            Assert.AreEqual(HttpStatusCode.Accepted, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync());
        }
        plan = await fixture.WaitPlanAsync(value => value.Attempts.Count == 1 && value.Attempts[0].Status == expected);
        var attempt = plan.Attempts.Single();
        Assert.AreEqual(WorkTaskOutcome.Pending, plan.Tasks.Single().Outcome);
        var assignment = await fixture.WaitAssignmentAsync(attempt.FlowRunId, value => value.State is RuntimeAssignmentState.Succeeded or RuntimeAssignmentState.Failed or RuntimeAssignmentState.Cancelled);
        Assert.AreEqual(CodingExecutionProtocol.RuntimeCapability, assignment.RuntimeCapability);
        Assert.AreEqual(worker.WorkerId, assignment.CurrentAttempt!.WorkerId.Value); Assert.AreEqual(1, assignment.FencingGeneration);
        var consumption = assignment.ExecutionEvents.Where(item => item.Payload is { } value && value.TryGetProperty("codingConsumption", out _))
            .Select(item => item.Payload!.Value.GetProperty("codingConsumption").Deserialize<CodingConsumptionObservation>(Json)!).ToArray();
        Assert.HasCount(1, consumption); Assert.AreEqual(mode == "no-usage" ? null : (long?)13, consumption[0].InputTokens);
        Assert.AreEqual(mode == "no-usage" ? CodingConsumptionAvailability.Unavailable : mode is "cancel" or "partial" ? CodingConsumptionAvailability.Partial : CodingConsumptionAvailability.Known, consumption[0].Availability);
        Assert.AreEqual(attempt.Id, consumption[0].Coordinates.TaskAttemptId); Assert.AreEqual(fixture.PlanId, consumption[0].Coordinates.PlanId.Value);
        Assert.AreEqual(assignment.CurrentAttempt.Id.Value, consumption[0].Coordinates.InvocationId);
        var receipt = assignment.ExecutionEvents.Single(item => item.Payload is { } value && value.TryGetProperty("codingResult", out _))
            .Payload!.Value.GetProperty("codingResult").Deserialize<CodingNativeResult>(Json)!;
        Assert.AreEqual(consumption[0].Coordinates, receipt.Coordinates);
        if (mode != "cancel") StringAssert.Contains(receipt.Patch!, "answer.txt");
        if (mode == "success") { Assert.AreEqual(0, receipt.Checks.Single().ExitCode); Assert.IsTrue(receipt.Checks.Single().Completed); }
        if (mode == "fail") Assert.AreEqual(17, receipt.CliExitCode);
        using var advance = await fixture.Http.PostAsync(fixture.PlanUrl + "/execution/advance", null);
        Assert.HasCount(1, (await advance.Content.ReadFromJsonAsync<WorkPlanResponse>())!.Attempts);
        var before = JsonSerializer.Serialize(assignment);
        await worker.DisposeAsync(); await fixture.RestartAsync();
        var retained = (await fixture.Http.GetFromJsonAsync<WorkTaskExecutionPage>($"/api/workspaces/default/tasks/{fixture.TaskId}/execution-history"))!;
        Assert.HasCount(1, retained.Items); Assert.AreEqual(attempt.Id, retained.Items.Single().Attempt.Id);
        var persisted = await fixture.WaitAssignmentAsync(attempt.FlowRunId, _ => true);
        Assert.AreEqual(before, JsonSerializer.Serialize(persisted));
        if (expected == WorkPlanAttemptStatus.Completed) Assert.IsNotNull(retained.Items.Single().Attempt.Result);
        else Assert.IsNotNull(retained.Items.Single().Attempt.Error);
        using var foreign = await fixture.Http.GetAsync($"/api/workspaces/{Guid.NewGuid()}/tasks/{fixture.TaskId}/execution-history");
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
    }

    [TestMethod]
    public async Task NativeFlowPublicationRequiresGrantedInstalledPinsAndAdmissionRejectsMissingProject()
    {
        await using var fixture = await Fixture.CreateAsync("success");
        using var actor = fixture.Scope();
        var flows = fixture.Host.Services.GetRequiredService<FlowService>();
        var bad = fixture.RuntimeDefinition with { MaterialVersion = "99.0" };
        var created = await flows.CreateExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), new("unsupported-native", null, "1.0.0", true, bad), default, default);
        var version = await Assert.ThrowsAsync<FlowValidationException>(() => flows.PublishVersionExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), created.Value.Id, "1.0.0", true, default));
        Assert.AreEqual("coding_material_version_unsupported", version.Code);
        await fixture.StartPlanAsync(false, "coding_project_required");
        await fixture.GrantRuntimeAsync(false);
        var denied = await flows.CreateExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), new("denied-native", null, "1.0.0", true, fixture.RuntimeDefinition), default, default);
        Assert.AreEqual("coding_runtime_profile_denied", (await Assert.ThrowsAsync<FlowValidationException>(() =>
            flows.PublishVersionExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), denied.Value.Id, "1.0.0", true, default))).Code);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private AuthenticatedGitFixture native = null!;
        private string data = "";
        private string mode = "";
        internal WebApplicationFactory<Program> Host { get; private set; } = null!;
        internal HttpClient Http { get; private set; } = null!;
        internal RequestContext Context { get; private set; } = null!;
        internal Guid PlanId { get; } = Guid.NewGuid();
        internal Guid TaskId { get; } = Guid.NewGuid();
        private Guid ProjectId { get; } = Guid.NewGuid();
        private WorkProjectResourceReference connection = null!;
        private RuntimeProfileResource runtime = null!;
        internal RuntimeFlowDefinition RuntimeDefinition { get; private set; } = null!;
        internal string PlanUrl => $"/api/workspaces/default/work-plans/{PlanId}";
        private bool stopped;
        internal IDisposable Scope() => Host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(Context);
        internal static async Task<Fixture> CreateAsync(string mode)
        {
            var fixture = new Fixture { mode = mode, native = await AuthenticatedGitFixture.CreateAsync() };
            fixture.data = Path.Combine(fixture.native.Root, "authority");
            await fixture.native.GitAsync(fixture.native.RepositoryPath, "config", "remote.origin.url", fixture.native.Repository);
            await fixture.StartHostAsync();
            using var actor = fixture.Scope();
            var resources = fixture.Host.Services.GetRequiredService<IResourceStore>();
            var tool = await resources.PutExactAsync(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                ScopeRef = ResourceScopeRef.Workspace(fixture.Context.WorkspaceId),
                Metadata = new() { Name = "native-repository" },
                Generation = 1,
                Definition = new() { DisplayName = "Native repository fixture", Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch } }
            }, null, true, default);
            fixture.connection = new(new(tool.Value.ScopeRef!.Value, tool.Value.Namespace, tool.Value.Kind, tool.Value.Name), tool.Value.Uid, tool.Value.Generation);
            using (fixture.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem())
                fixture.runtime = (await resources.PutExactAsync(ResourceScopeRef.Instance, new RuntimeProfileResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = RuntimeProfileResourceKinds.RuntimeProfile,
                    ScopeRef = ResourceScopeRef.Instance,
                    Metadata = new() { Name = "coding-local" },
                    Generation = 1,
                    Definition = new() { DisplayName = "Coding fixture", RuntimeType = CodingExecutionProtocol.RuntimeCapability }
                }, null, true, default)).Value;
            await fixture.GrantRuntimeAsync(true);
            var profiles = fixture.Host.Services.GetRequiredService<HarnessProfileService>();
            var workspace = new WorkspaceId(fixture.Context.WorkspaceId);
            Assert.IsTrue((await profiles.CreateConfigurationAsync(workspace, new("native", default, "1.0.0", new("codex", "1.0.0", new("fixture-model", "workspace-write"))), default)).IsSuccess);
            var draft = await profiles.CreateAsync(workspace, new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = HarnessResourceKinds.Profile,
                Metadata = new() { Name = "coder" },
                Definition = new("Fixture", "Perform the requested bounded repository change.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), new("native", "1.0.0", default))
            }, default);
            Assert.IsTrue(draft.IsSuccess, draft.ErrorCode);
            var profile = await profiles.PublishAsync(workspace, default, "coder", new("1.0.0", draft.Value!.ETag), default); Assert.IsTrue(profile.IsSuccess, profile.ErrorCode);
            var fake = FakeCli();
            var config = new CodingExecutionConfiguration(new("coding-local", default), new("coder", default, "1.0.0", profile.Value!.Value.ContentDigest), "example/fixture")
            { Checks = [new("check", "fixture-check", [fake, "check", "pass"])] };
            fixture.RuntimeDefinition = new(CodingExecutionProtocol.RuntimeCapability, CodingExecutionProtocol.CapabilityVersion, CodingExecutionProtocol.MaterialVersion, JsonSerializer.SerializeToElement(config, Json));
            var scope = ResourceScopeRef.Workspace(fixture.Context.WorkspaceId);
            using var flow = await fixture.Http.PostAsJsonAsync("/api/flows", new CreateFlowRequest("native-fixture", null, "1.0.0", true, fixture.RuntimeDefinition) { ScopeRef = scope });
            Assert.AreEqual(HttpStatusCode.Created, flow.StatusCode, await flow.Content.ReadAsStringAsync());
            using var publication = await fixture.Http.PostAsJsonAsync("/api/flows/native-fixture/versions?scopeRef=" + Uri.EscapeDataString(scope.Value), new CreateFlowVersionRequest("1.0.0"));
            Assert.AreEqual(HttpStatusCode.Created, publication.StatusCode, await publication.Content.ReadAsStringAsync());
            return fixture;
        }
        internal async Task GrantRuntimeAsync(bool granted)
        {
            using var system = Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var access = Host.Services.GetRequiredService<IResourceAccessPolicyService>();
            var previous = await access.GetAsync(runtime.Uid, default);
            await access.PutAsync(new(new(runtime.Uid, ResourceScopeRef.Instance, runtime.Namespace, runtime.Kind, runtime.Generation),
                granted ? [new(ResourceScopeRef.Workspace(Context.WorkspaceId), [ResourceAccessAction.Discover, ResourceAccessAction.Use])] : []), previous?.ETag, default);
        }
        internal async Task<WorkPlanResponse> StartPlanAsync(bool includeProject = true, string? expectedError = null)
        {
            using var tree = await Http.PostAsJsonAsync("/api/workspaces/default/tasks/tree", new CreateWorkTaskTreeRequest(TaskId,
                [new(TaskId, WorkTaskKind.Atomic, "Deliver fixture", "Create the deterministic fixture answer and retain evidence.")]));
            Assert.AreEqual(HttpStatusCode.Created, tree.StatusCode);
            using var created = await Http.PostAsJsonAsync("/api/workspaces/default/work-plans", new CreateWorkPlanRequest(PlanId, Guid.NewGuid(), new("Deliver fixture", [TaskId], [])));
            var plan = (await created.Content.ReadFromJsonAsync<WorkPlanResponse>())!;
            foreach (var action in new[] { "validate", "activate" })
            { using var response = await Http.PostAsJsonAsync(PlanUrl + "/" + action, new TransitionWorkPlanRequest(Guid.NewGuid(), plan.Version, plan.CurrentRevision, plan.Revision.Digest)); Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync()); plan = (await response.Content.ReadFromJsonAsync<WorkPlanResponse>())!; }
            using var project = await Http.PostAsJsonAsync("/api/workspaces/default/projects", new CreateWorkProjectRequest(ProjectId, Guid.NewGuid(), "fixture", new("Fixture", null, [PlanId], [])
            { Bindings = [new("repository", WorkProjectBindingPurpose.Repository, connection)] }));
            Assert.AreEqual(HttpStatusCode.Created, project.StatusCode, await project.Content.ReadAsStringAsync());
            using var start = await Http.PostAsJsonAsync(PlanUrl + "/execution", new StartWorkPlanExecutionRequest(Guid.NewGuid(), plan.Version, plan.CurrentRevision, plan.Revision.Digest,
                [new(TaskId, new(new FlowId("native-fixture")), JsonSerializer.SerializeToElement(new CodingTaskInput(native.InitialRevision, new Dictionary<string, string>()), Json))])
            { ProjectId = includeProject ? ProjectId : null });
            if (expectedError is not null)
            {
                Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode);
                using var rejected = await Http.PostAsync(PlanUrl + "/execution/advance", null);
                Assert.IsFalse(rejected.IsSuccessStatusCode); StringAssert.Contains(await rejected.Content.ReadAsStringAsync(), expectedError);
                Assert.HasCount(0, (await Http.GetFromJsonAsync<WorkPlanResponse>(PlanUrl))!.Attempts);
                return plan;
            }
            Assert.AreEqual(HttpStatusCode.Accepted, start.StatusCode, await start.Content.ReadAsStringAsync());
            return await WaitPlanAsync(value => value.Attempts.Count == 1);
        }
        internal Task<AwpTestWorker> WorkerAsync()
        {
            var fake = FakeCli(); var dotnet = DotnetHost();
            var options = new Native.CodingWorkerOptions
            {
                CliExecutable = dotnet,
                CliPrefixArguments = [fake, "run-mode", mode, Path.Combine(data, "authority-key-not-mounted")],
                ReadOnlyRuntimeDirectories = [Path.GetDirectoryName(fake)!],
                CheckExecutables = new Dictionary<string, string> { ["fixture-check"] = dotnet },
                Repositories = [new("github", "42", native.Repository, native.RepositoryPath)]
            };
            var executor = new Native.CodingAssignmentExecutor(new Native.CodingSandbox(Options.Create(options), Options.Create(new RuntimeWorkerOptions())), TimeProvider.System);
            return AwpTestWorker.StartAsync(Host, true, executor, [new(CodingExecutionProtocol.RuntimeCapability, CodingExecutionProtocol.CapabilityVersion, [CodingExecutionProtocol.MaterialVersion])]);
        }
        internal async Task<WorkPlanResponse> WaitPlanAsync(Func<WorkPlanResponse, bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); WorkPlanResponse? last = null;
            try { while (true) { last = await Http.GetFromJsonAsync<WorkPlanResponse>(PlanUrl, timeout.Token); if (last is not null && ready(last)) return last; await Task.Delay(50, timeout.Token); } }
            catch (OperationCanceledException) { Assert.Fail("Native plan did not reach the expected state: " + JsonSerializer.Serialize(last)); throw; }
        }
        internal async Task<RuntimeWorkerAssignment> WaitAssignmentAsync(string run, Func<RuntimeWorkerAssignment, bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (true) { var value = await Host.Services.GetRequiredService<RuntimeWorkerAssignmentService>().GetByTargetExactAsync(ResourceScopeRef.Workspace(Context.WorkspaceId), RuntimeAssignmentTargetKind.FlowRun, run, timeout.Token); if (value is not null && ready(value.Value)) return value.Value; await Task.Delay(50, timeout.Token); }
        }
        private async Task StartHostAsync()
        {
            stopped = false;
            Host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            { builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", data); builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic"); builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning"); AwpTestWorker.Configure(builder); builder.ConfigureTestServices(services => { services.RemoveAll<ICodingProviderGateway>(); services.AddSingleton<ICodingProviderGateway>(new RepositoryGateway(native)); }); });
            Http = Host.CreateClient(); Context = await Host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        }
        private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
        private static string FakeCli()
        { for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent) if (File.Exists(Path.Combine(root.FullName, "Agentstration.slnx"))) return Path.Combine(root.FullName, "tests/fixtures/NativeCodingCli/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net10.0/NativeCodingCli.dll"); throw new InvalidOperationException("Fixture unavailable."); }
        private async Task StopAsync() { if (stopped) return; stopped = true; Http.Dispose(); await Host.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        internal async Task RestartAsync() { await StopAsync(); await StartHostAsync(); }
        public async ValueTask DisposeAsync() { await StopAsync(); await native.DisposeAsync(); }
    }
    private sealed class RepositoryGateway(AuthenticatedGitFixture native) : ICodingProviderGateway
    {
        public Task<Result<CodingProviderResponse>> InvokeAsync(WorkspaceId workspaceId, Guid principalId, WorkProjectContextBinding binding, CodingProviderRequest request, CancellationToken token) =>
            Task.FromResult(Result<CodingProviderResponse>.Success(new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Succeeded,
                [new("github", "example/fixture", "42", new(native.Repository), new(native.Repository), "main", native.InitialRevision, [])])));
    }
}
