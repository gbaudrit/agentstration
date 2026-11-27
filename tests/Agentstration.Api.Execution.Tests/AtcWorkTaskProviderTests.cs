extern alias atcSample;
using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Server;
using Agentstration.Flows;
using Agentstration.Infrastructure.Work;
using Agentstration.Resources;
using Agentstration.Runtime.Core;
using Agentstration.Tests.Work;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Agentstration.Work.Storage.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using MemoryTrackingProvider = atcSample::Atc.Samples.LocalTracking.MemoryTrackingProvider;

namespace Agentstration.Api.Tests;

[TestClass]
public sealed class AtcWorkTaskProviderTests
{
    [TestMethod]
    public async Task NonRepositoryTrackingUsesGovernedMcpAndRetainsNativeEvidenceAcrossSqliteRestart()
    {
        await using var host = await Host.CreateAsync(); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        var command = host.Command(new(ExternalTaskOperationKind.Publish, Title: "Parent", Body: "Children stay local") { Tracking = host.Target });
        host.Provider.LoseResponse = true;
        var uncertain = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id, command, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, uncertain.Status);
        Assert.IsNotNull(uncertain.Route.Tracking!.WireIntentDigest);
        Assert.AreEqual(WorkTrackingRecovery.Process, uncertain.Route.Tracking.Capabilities.Recovery);
        Assert.IsFalse(uncertain.Route.Capabilities.IdempotentOperations, "Process receipts must not authorize replay after Missing.");
        await host.ReopenAsync();
        var published = Success(await host.Service.ResumeAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Succeeded, published.Status); Assert.AreEqual(1, host.Provider.Effects);
        Assert.AreEqual("", published.Task!.Repository); Assert.AreEqual(host.Target.ProviderId, published.Task.Tracking!.Identity.ProviderId);
        var item = published.Task;
        item = await host.PerformAsync(new(ExternalTaskOperationKind.Update, TaskId: item.TaskId, Title: "Revised", Labels: ["native"], ExpectedRevision: item.Revision) { Tracking = host.Target });
        Assert.AreEqual("Revised", item.Title); CollectionAssert.AreEqual(new[] { "native" }, item.Labels.ToArray());
        item = await host.PerformAsync(new(ExternalTaskOperationKind.Comment, TaskId: item.TaskId, ExpectedRevision: item.Revision) { Tracking = host.Target, Comment = "Evidence" });
        Assert.AreEqual("Evidence", item.Tracking!.Comments.Single().Text);
        var other = await host.PerformAsync(new(ExternalTaskOperationKind.Publish, Title: "Related") { Tracking = host.Target });
        item = await host.PerformAsync(new(ExternalTaskOperationKind.Relate, TaskId: item.TaskId, ExpectedRevision: item.Revision)
        { Tracking = host.Target, Relation = new(WorkTrackingRelationKind.Blocks, other.Tracking!.Identity) });
        Assert.AreEqual(WorkTrackingRelationKind.Blocks, item.Tracking!.Relations.Single().Kind);
        item = await host.PerformAsync(new(ExternalTaskOperationKind.Transition, TaskId: item.TaskId, ExpectedRevision: item.Revision)
        { Tracking = host.Target, TransitionId = "finish" });
        Assert.AreEqual("closed", item.State); Assert.AreEqual("done", item.CompletionReason);
        var read = await host.PerformAsync(new(ExternalTaskOperationKind.Read, TaskId: item.TaskId) { Tracking = host.Target });
        Assert.AreEqual(WorkPlanDefinition.Digest(item), WorkPlanDefinition.Digest(read));
        Assert.AreEqual(host.Project.Version, (await host.Projects.GetAsync(host.Fixture.WorkspaceId, host.Fixture.PrincipalId, host.Project.Id, default))!.Version);
        Assert.IsTrue(host.Fixture.Calls.All(call => call.OwnerKind == Agentstration.Runtime.Abstractions.ToolExecutionOwnerKind.WorkOperation));
        Assert.AreEqual("task_provider_operation_not_found", (await host.Service.ResumeAsync(WorkspaceId.New(), host.Project.Id, command.Id, default)).ErrorCode);
        using var foreign = host.Fixture.Context.Push(host.Fixture.Caller with { PrincipalId = Guid.NewGuid() });
        Assert.AreEqual("task_provider_denied", (await host.Gateway.InvokeAsync(uncertain, true, default)).ErrorCode);
    }

    [TestMethod]
    public async Task MissingProcessReceiptNeverDuplicatesPublicationAndRevocationPreventsRemoteCalls()
    {
        await using var host = await Host.CreateAsync(); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        host.Provider.SuppressEffect = true;
        var command = host.Command(new(ExternalTaskOperationKind.Publish, Title: "Uncertain") { Tracking = host.Target });
        _ = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id, command, default));
        host.Provider.SuppressEffect = false;
        var missing = Success(await host.Service.ResumeAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, missing.Status); Assert.AreEqual(0, host.Provider.Effects);
        host.Fixture.Denied.Add(host.Binding.Contract!.Provider.Uid);
        var calls = host.Fixture.Calls.Count;
        Assert.IsFalse((await host.Service.ResumeAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default)).IsSuccess);
        Assert.AreEqual(calls, host.Fixture.Calls.Count);
    }

    [TestMethod]
    public async Task TrackingRejectsUnsupportedInputsStaleRevisionsTargetAndIncompleteObservations()
    {
        await using var host = await Host.CreateAsync(); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        var input = new ExternalTaskOperationInput(ExternalTaskOperationKind.Publish, Title: "Parent") { Tracking = host.Target };
        var wrong = await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id,
            host.Command(input with { Tracking = host.Target with { TargetId = "foreign" } }), default);
        Assert.AreEqual("task_provider_input_unsupported", wrong.ErrorCode); Assert.AreEqual(0, host.Provider.Effects);
        var item = await host.PerformAsync(input);
        var stale = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id,
            host.Command(new(ExternalTaskOperationKind.Update, TaskId: item.TaskId, Title: "Stale", ExpectedRevision: "old") { Tracking = host.Target }), default));
        Assert.AreEqual(WorkTaskOperationStatus.Rejected, stale.Status); Assert.AreEqual("revision_conflict", stale.ErrorCode);
        host.Provider.Incomplete = true;
        var incomplete = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id,
            host.Command(new(ExternalTaskOperationKind.Read, TaskId: item.TaskId) { Tracking = host.Target }), default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, incomplete.Status); Assert.IsNull(incomplete.Task);
        host.Provider.ReadOnly = true;
        Assert.AreEqual("task_provider_capability_missing", (await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id, host.Command(input), default)).ErrorCode);
        Assert.AreEqual(1, host.Provider.Effects);
    }

    [TestMethod]
    public async Task AuthorizedAgentToolReusesApplicationReceiptsAndDryRunHasNoRemoteEffects()
    {
        await using var host = await Host.CreateAsync(); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        var tool = new WorkPlanningMcpTool(WorkPlanningOperation.BeginTaskOperation, null!, null!, null!, null!,
            host.Fixture.Context, host.Fixture, host.Service);
        var arguments = new
        {
            projectId = host.Project.Id.Value,
            expectedProjectVersion = host.Project.Version,
            bindingName = host.Binding.Name,
            input = new ExternalTaskOperationInput(ExternalTaskOperationKind.Publish, Title: "Agent parent") { Tracking = host.Target }
        };
        var invocation = new InternalMcpToolInvocation(host.Fixture.Caller.TenantId, host.Fixture.WorkspaceId,
            host.Fixture.PrincipalId, "stable-agent-call", null, JsonSerializer.SerializeToElement(arguments, JsonSerializerOptions.Web), ToolDefinitionCallerKind.Agent);
        var denied = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => tool.ExecuteAsync(invocation, default));
        Assert.AreEqual("tool_not_authorized", denied.Code);
        invocation = invocation with
        {
            ExposedTools = [new(AgentstrationToolProvider.ToolResourceName(tool.Definition.Name),
            ResourceScopeRef.Workspace(host.Fixture.WorkspaceId.Value), ResourceNamespace.Default)]
        };
        var dryArguments = JsonSerializer.SerializeToNode(arguments, JsonSerializerOptions.Web)!.AsObject(); dryArguments[ToolDryRunContract.ParameterName] = true;
        _ = await tool.ExecuteAsync(invocation with { Arguments = JsonSerializer.SerializeToElement(dryArguments) }, default);
        Assert.HasCount(0, host.Fixture.Calls);
        var first = (await tool.ExecuteAsync(invocation, default))!.Value;
        var second = (await tool.ExecuteAsync(invocation, default))!.Value;
        Assert.AreEqual(first.GetProperty("value").GetProperty("id").GetProperty("value").GetGuid(),
            second.GetProperty("value").GetProperty("id").GetProperty("value").GetGuid());
        Assert.AreEqual(1, host.Provider.Effects);
        Assert.HasCount(1, (await host.Service.ListAsync(host.Fixture.WorkspaceId, host.Project.Id, 0, 100, default)).Value!.Items);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryWithoutGuaranteeOrOutsideWindowNeverReissuesAnEffect(bool noRecovery)
    {
        await using var host = await Host.CreateAsync(); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        host.Provider.NoRecovery = noRecovery;
        host.Provider.LoseResponse = true;
        var command = host.Command(new(ExternalTaskOperationKind.Publish, Title: "Expired") { Tracking = host.Target });
        _ = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id, command, default));
        host.Clock.Advance(TimeSpan.FromSeconds(301));
        var resumed = Success(await host.Service.ResumeAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, resumed.Status);
        Assert.AreEqual("task_provider_recovery_unavailable", resumed.ErrorCode); Assert.AreEqual(1, host.Provider.Effects);
    }

    [TestMethod]
    public async Task IndependentSqliteInstancesUseEqualPortableNeedsAndDifferentLocalScopesAfterRestart()
    {
        await using var first = await Host.CreateAsync(true); await using var second = await Host.CreateAsync();
        Assert.AreEqual(WorkPlanDefinition.Digest(first.Project.Definition.ConnectionRequirements),
            WorkPlanDefinition.Digest(second.Project.Definition.ConnectionRequirements));
        Assert.AreNotEqual(first.Project.Definition.ConnectionAssignments[0].Service.Uid, second.Project.Definition.ConnectionAssignments[0].Service.Uid);
        Assert.AreNotEqual(first.Project.Definition.ConnectionAssignments[0].Service.Address.ScopeRef, second.Project.Definition.ConnectionAssignments[0].Service.Address.ScopeRef);
        foreach (var host in new[] { first, second })
        {
            using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
            var item = await host.PerformAsync(new(ExternalTaskOperationKind.Publish, Title: "Portable target") { Tracking = host.Target });
            await host.ReopenAsync();
            var read = await host.PerformAsync(new(ExternalTaskOperationKind.Read, TaskId: item.TaskId) { Tracking = host.Target });
            Assert.AreEqual(item.TaskId, read.TaskId);
            var receipts = (await host.Service.ListAsync(host.Fixture.WorkspaceId, host.Project.Id, 0, 100, default)).Value!.Items;
            Assert.IsTrue(receipts.All(value => value.Route.Tracking!.Connection.Service is not null));
            Assert.AreEqual(host.Project.Definition.ConnectionAssignments[0].Service.Uid,
                receipts[0].Route.Tracking!.Connection.Service!.Service.Uid);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UncertainOperationRetainsItsServicePinsWhenConfigurationOrLocalAssignmentChanges(bool replaceAssignment)
    {
        await using var host = await Host.CreateAsync(parameterRevision: true); using var caller = host.Fixture.Context.Push(host.Fixture.Caller);
        host.Provider.LoseResponse = true;
        var command = host.Command(new(ExternalTaskOperationKind.Publish, Title: "Original service") { Tracking = host.Target });
        var operation = Success(await host.Service.BeginAsync(host.Fixture.WorkspaceId, host.Project.Id, command, default));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, operation.Status); var original = JsonSerializer.Serialize(operation.Route);
        if (replaceAssignment)
        {
            var assignment = host.Project.Definition.ConnectionAssignments.Single();
            var receipt = new WorkProjectCommandReceipt(Guid.NewGuid(), "explicit-replacement", 2, host.Clock.GetUtcNow());
            var changed = host.Project with
            {
                Version = 2,
                Definition = host.Project.Definition with
                { ConnectionAssignments = [assignment with { Service = assignment.Service with { Uid = Guid.NewGuid() } }] },
                Commands = [.. host.Project.Commands, receipt]
            };
            Assert.AreEqual(WorkProjectSaveStatus.Saved, await host.Projects.TrySaveAsync(new(changed, 1, receipt, [], []), default));
        }
        else host.Fixture.ParameterValues[host.Fixture.ParameterValues.Keys.Single()] = JsonSerializer.SerializeToElement("revision-2");
        await host.ReopenAsync();
        Assert.IsFalse((await host.Service.ResumeAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default)).IsSuccess);
        var retained = Success(await host.Service.GetAsync(host.Fixture.WorkspaceId, host.Project.Id, command.Id, default));
        Assert.AreEqual(original, JsonSerializer.Serialize(retained.Route));
        Assert.AreEqual(WorkTaskOperationStatus.Uncertain, retained.Status); Assert.AreEqual(1, host.Provider.Effects);
    }

    private static WorkTaskOperation Success(Result<WorkTaskOperation> result)
    { Assert.IsTrue(result.IsSuccess, result.ErrorCode); return result.Value!; }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "atc-work-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider storage = null!;
        private WebApplication app = null!;
        public ProjectContractFixture Fixture { get; } = new() { UseActualMcp = true };
        public FaultProvider Provider { get; } = new();
        public Clock Clock { get; } = new();
        public WorkProjectContextBinding Binding { get; private set; } = null!;
        public WorkProjectSnapshot Project { get; private set; } = null!;
        public WorkTrackingTarget Target => new(Binding.Contract!.ProviderId, Binding.Contract.TargetId);
        public AtcWorkTaskProviderGateway Gateway => new(Fixture.Resolver, Fixture, new ToolExecutionPipeline(Fixture), Fixture.Context, Clock);
        public IWorkProjectRepository Projects => storage.GetRequiredService<IWorkProjectRepository>();
        public WorkTaskProviderService Service => new(storage.GetRequiredService<IWorkTaskOperationRepository>(), Projects,
            new(storage.GetRequiredService<IWorkPlanRepository>(), storage.GetRequiredService<IWorkItemRepository>(), Fixture,
                Clock, NullLogger<WorkPlanService>.Instance), Gateway, Fixture,
            [new Scope(new(Fixture.Caller.TenantId, Fixture.WorkspaceId, Fixture.PrincipalId))], Clock,
            NullLogger<WorkTaskProviderService>.Instance);
        public BeginWorkTaskOperation Command(ExternalTaskOperationInput input) => new(new(Guid.NewGuid()), Project.Version, Binding.Name, input);
        public async Task<ExternalTaskSnapshot> PerformAsync(ExternalTaskOperationInput input)
        {
            var result = Success(await Service.BeginAsync(Fixture.WorkspaceId, Project.Id, Command(input), default));
            Assert.AreEqual(WorkTaskOperationStatus.Succeeded, result.Status, result.ErrorCode + ": " + Fixture.InvocationError);
            return result.Task!;
        }
        public static async Task<Host> CreateAsync(bool instanceService = false, bool parameterRevision = false)
        {
            var host = new Host(); Directory.CreateDirectory(host.directory);
            host.Binding = host.Fixture.AddConnection(true, "tracking", instanceService ? ResourceScopeRef.Instance : null);
            var selection = host.Binding.Contract!; var target = new AtcTargetRef(selection.ProviderId, selection.TargetId, selection.BindingId, selection.Revision);
            var server = new AtcMcpServer([new(target, host.Provider, (_, _) => ValueTask.FromResult(
                new AtcAccessContext(target, "fixture", "grant-1", AtcContract.Operations, [])))]);
            var builder = WebApplication.CreateBuilder(); builder.Configuration["AllowedHosts"] = "tracking.test";
            builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
            builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                .WithListToolsHandler(server.ListToolsAsync).WithCallToolHandler(server.CallToolAsync);
            host.app = builder.Build(); host.app.MapMcp("/mcp"); await host.app.StartAsync();
            host.Fixture.McpHandler = host.app.GetTestServer().CreateHandler();
            await host.ReopenAsync();
            var now = TimeProvider.System.GetUtcNow(); var receipt = new WorkProjectCommandReceipt(Guid.NewGuid(), "digest", 1, now);
            var connection = host.Fixture.AddService(host.Binding, instanceService: instanceService, parameterRevision: parameterRevision);
            host.Project = new(WorkProjectId.New(), host.Fixture.WorkspaceId, host.Fixture.PrincipalId, "tracking", default,
                new("Tracking", null, [], []) { ConnectionRequirements = [connection.Requirement], ConnectionAssignments = [connection.Assignment] }, 1, "digest", now, now, [receipt]);
            Assert.AreEqual(WorkProjectSaveStatus.Saved, await host.Projects.TrySaveAsync(new(host.Project, 0, receipt, [], []), default));
            return host;
        }
        public async Task ReopenAsync()
        {
            if (storage is not null) await storage.DisposeAsync();
            var services = new ServiceCollection(); services.AddSqliteWorkPlane($"Data Source={Path.Combine(directory, "work.db")};Pooling=False");
            storage = services.BuildServiceProvider(); await Projects.InitializeAsync(default); await Service.InitializeAsync(default);
        }
        public async ValueTask DisposeAsync()
        { await storage.DisposeAsync(); await app.DisposeAsync(); Fixture.McpHandler?.Dispose(); Directory.Delete(directory, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = TimeProvider.System.GetUtcNow();
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
    private sealed record Scope(FlowRunScope? Current) : IWorkExecutionScopeAccessor;
    private sealed class FaultProvider : IAtcProvider
    {
        private readonly MemoryTrackingProvider inner = new(TimeProvider.System);
        public int Effects { get; private set; }
        public bool LoseResponse { get; set; }
        public bool SuppressEffect { get; set; }
        public bool Incomplete { get; set; }
        public bool ReadOnly { get; set; }
        public bool NoRecovery { get; set; }
        public string ImplementationId => inner.ImplementationId;
        public string ImplementationVersion => inner.ImplementationVersion;
        public AtcCapabilities Capabilities => ReadOnly ? inner.Capabilities with { Operations = ["list", "read"], UpdateFields = [], RelationKinds = [] } : NoRecovery ? inner.Capabilities with { Recovery = AtcRecoveryScope.None, ReceiptWindowSeconds = 0 } : inner.Capabilities;
        public async ValueTask<AtcItem> ReadAsync(AtcAccessContext context, AtcReadRequest request, CancellationToken token)
        { var item = await inner.ReadAsync(context, request, token); return Incomplete ? item with { RelationsComplete = false } : item; }
        public ValueTask<AtcPage> ListAsync(AtcAccessContext context, AtcListRequest request, CancellationToken token) => inner.ListAsync(context, request, token);
        public async ValueTask<AtcReceipt> MutateAsync(AtcAccessContext context, AtcMutationRequest request, CancellationToken token)
        {
            if (SuppressEffect) return new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Uncertain, ErrorCode: "response_lost");
            var receipt = await inner.MutateAsync(context, request, token);
            if (request.Mode == AtcMutationMode.Execute && receipt.Status == AtcMutationStatus.Applied) Effects++;
            if (LoseResponse && request.Mode == AtcMutationMode.Execute)
            { LoseResponse = false; return new(request.OperationKey, request.IntentDigest, AtcMutationStatus.Uncertain, ErrorCode: "response_lost"); }
            return receipt;
        }
    }
}
