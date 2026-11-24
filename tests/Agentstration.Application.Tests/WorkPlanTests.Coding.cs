using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Application.Tests;

public sealed partial class WorkPlanTests
{
    [TestMethod]
    public async Task CodingRepositorySelectionRequiresPrivateProjectTaskAssociationAndOneRepositoryPerAtomicTask()
    {
        await using var fixture = await Fixture.CreateAsync(); var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync(); var binding = CodingBinding(fixture);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [], [tree.RootTaskId]) { Bindings = [binding] }), default));
        var gateway = new CodingGateway(); var service = Coding(fixture, gateway);
        var composite = await service.ResolveAsync(fixture.Workspace, project.Id, new(tree.RootTaskId.Value, ["fixture/one", "fixture/two"], 1), default);
        Assert.IsTrue(composite.IsSuccess, composite.ErrorCode); Assert.HasCount(2, composite.Value!.Repositories);
        Assert.AreEqual("coding_repository_selection_invalid", (await service.ResolveAsync(fixture.Workspace, project.Id,
            new(tree.Tasks[1].Id.Value, ["fixture/one", "fixture/two"], 1), default)).ErrorCode);
        var leaf = await service.ResolveAsync(fixture.Workspace, project.Id, new(tree.Tasks[1].Id.Value, ["fixture/one"], 1), default);
        Assert.IsTrue(leaf.IsSuccess, leaf.ErrorCode); Assert.AreEqual(binding.Target, leaf.Value!.Connection);
        Assert.AreEqual(Now, leaf.Value.ObservedAt); Assert.AreEqual(64, leaf.Value.Digest.Length);
        Assert.AreEqual("coding_task_changed", (await service.ResolveAsync(fixture.Workspace, project.Id, new(tree.Tasks[1].Id.Value, ["fixture/one"], 7), default)).ErrorCode);
        Assert.IsFalse((await Coding(fixture, gateway, Guid.NewGuid()).ResolveAsync(fixture.Workspace, project.Id,
            new(tree.Tasks[1].Id.Value, ["fixture/one"]), default)).IsSuccess);
        Assert.IsFalse((await service.ResolveAsync(WorkspaceId.New(), project.Id, new(tree.Tasks[1].Id.Value, ["fixture/one"]), default)).IsSuccess);
        var unrelated = await fixture.CreateTreeAsync();
        Assert.AreEqual("coding_task_project_mismatch", (await service.ResolveAsync(fixture.Workspace, project.Id,
            new(unrelated.RootTaskId.Value, ["fixture/one"]), default)).ErrorCode);
        gateway.WrongRepository = true;
        Assert.AreEqual("coding_repository_response_invalid", (await service.ResolveAsync(fixture.Workspace, project.Id,
            new(tree.Tasks[1].Id.Value, ["fixture/one"]), default)).ErrorCode);
        await fixture.ReopenAsync();
        Assert.AreEqual(1, (await fixture.Trees().GetAsync(fixture.Workspace, tree.Tasks[1].Id, default)).Value!.Tasks.Single().Version);
    }

    [TestMethod]
    public async Task CodingPullRequestPinsConnectionIntentAndTaskVersionAndSeparatesRecoveryFromNewEffects()
    {
        await using var fixture = await Fixture.CreateAsync(); var projects = ProjectService(fixture); await projects.InitializeAsync(default);
        var tree = await fixture.CreateTreeAsync(); var binding = CodingBinding(fixture);
        var project = ProjectSuccess(await projects.CreateAsync(fixture.Workspace, WorkProjectId.New(), "coding", default,
            new(Guid.NewGuid(), 0, new("Coding", null, [], [tree.RootTaskId]) { Bindings = [binding] }), default));
        var gateway = new CodingGateway(); var service = Coding(fixture, gateway);
        var command = new InvokeCodingPullRequestRequest(Guid.NewGuid(), tree.Tasks[1].Id.Value, 1, CodingProviderProtocol.CreatePullRequest,
            new("fixture/one", Title: "Deliver task", Head: "task", Base: "main", ExpectedHeadRevision: new string('a', 40)), binding.Target, project.Version);
        var created = await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command, default);
        Assert.IsTrue(created.IsSuccess, created.ErrorCode); var key = gateway.Last!.OperationKey; var digest = gateway.Last.IntentDigest;
        Assert.IsTrue((await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command with { Recover = true }, default)).IsSuccess);
        Assert.AreEqual(key, gateway.Last.OperationKey); Assert.AreEqual(digest, gateway.Last.IntentDigest); Assert.AreEqual("recover", gateway.Last.Mode);
        Assert.AreEqual("coding_atomic_task_required", (await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command with { TaskId = tree.RootTaskId.Value }, default)).ErrorCode);
        Assert.AreEqual("coding_connection_changed", (await service.InvokePullRequestAsync(fixture.Workspace, project.Id,
            command with { Connection = command.Connection with { Generation = 2 } }, default)).ErrorCode);
        await fixture.SetStatusAsync(tree.Tasks[1].Id, WorkItemStatus.Failed);
        Assert.AreEqual("coding_task_changed", (await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command, default)).ErrorCode);
        Assert.IsTrue((await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command with { Recover = true }, default)).IsSuccess);
        gateway.WrongRepository = true;
        Assert.AreEqual("coding_pull_request_response_invalid", (await service.InvokePullRequestAsync(fixture.Workspace, project.Id, command with { Recover = true }, default)).ErrorCode);
    }

    private static WorkProjectContextBinding CodingBinding(Fixture fixture) => new("repository", WorkProjectBindingPurpose.Repository,
        new(ScopedResourceAddress.Create(ResourceScopeRef.Workspace(fixture.Workspace.Value), default, "Tool", "agentstration.github.v1.0.0"), Guid.NewGuid(), 1));
    private static CodingRepositoryService Coding(Fixture fixture, CodingGateway gateway, Guid? owner = null) => new(ProjectService(fixture, owner),
        fixture.Trees(owner: owner), fixture.Plans(owner: owner), new Context(fixture.Workspace, owner ?? fixture.Principal), gateway, new Clock(Now));
    private sealed class CodingGateway : ICodingProviderGateway
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public CodingProviderRequest? Last { get; private set; }
        public bool WrongRepository { get; set; }
        public Task<Result<CodingProviderResponse>> InvokeAsync(WorkspaceId workspace, Guid principal, WorkProjectContextBinding binding, CodingProviderRequest request, CancellationToken token)
        {
            Last = request; token.ThrowIfCancellationRequested(); CodingProviderResponse response;
            if (request.Operation == CodingProviderProtocol.ResolveRepositories)
                response = new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Succeeded,
                    request.Request.GetProperty("repositories").EnumerateArray().Select((value, index) => new CodingRepositoryIdentity("fixture", WrongRepository ? "wrong/repo" : value.GetString()!,
                        index.ToString(System.Globalization.CultureInfo.InvariantCulture), new("https://example.test/repository.git"), new("https://example.test/repository"), "main", new string('a', 40), ["issue.read"])).ToArray());
            else
            {
                var input = request.Request.Deserialize<CodingPullRequestInput>(Json)!;
                response = new(request.OperationKey, request.IntentDigest, CodingProviderStatus.Succeeded, PullRequest: new("fixture", WrongRepository ? "wrong/repo" : input.Repository,
                    "100", "1", new("https://example.test/pull/1"), "open", true, false, input.Head!, input.ExpectedHeadRevision!, input.Base!, new string('b', 40), "revision"));
            }
            return Task.FromResult(Result<CodingProviderResponse>.Success(response));
        }
    }
}
