using Agentstration.Flows.Application;
using Agentstration.Flows;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task DraftRevisionBatchIsOneBoundedQueryAndKeepsWorkspaceOwnerAndNamespaceIsolated()
    {
        var commands = new FlowCommandInterceptor();
        await using var fixture = await FlowFixture.CreateAsync(commands);
        var workspace = new WorkspaceId(Guid.NewGuid());
        var otherWorkspace = new WorkspaceId(Guid.NewGuid());
        var owner = ResourceScopeRef.Instance;
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var id = new FlowId("same-name", new("team"));
        var otherNamespace = new FlowId("same-name", new("other"));
        foreach (var (scope, flow, author, revision) in new[] { (owner, id, workspace, 7L), (tenant, id, workspace, 11L), (owner, otherNamespace, workspace, 13L), (owner, id, otherWorkspace, 99L) })
        {
            await fixture.Repository.CreateDraftAsync(new FlowDraft
            {
                WorkspaceId = author, ScopeRef = scope, FlowId = flow, Id = "draft", DisplayName = "Flow",
                Definition = FlowDraftTemplates.Create("empty"), Revision = revision, CreatedAt = Now, UpdatedAt = Now
            }, default);
        }
        commands.Reset();
        var result = await fixture.Repository.ListDraftRevisionsAsync(workspace, [(owner, id), (tenant, id)], default);
        Assert.HasCount(2, result);
        Assert.AreEqual(7L, result[(owner, id)]);
        Assert.AreEqual(11L, result[(tenant, id)]);
        Assert.HasCount(1, commands.CommandTexts);
        commands.Reset();
        Assert.IsEmpty(await fixture.Repository.ListDraftRevisionsAsync(workspace, [], default));
        Assert.IsEmpty(commands.CommandTexts);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Repository.ListDraftRevisionsAsync(workspace, Enumerable.Repeat((owner, id), 201).ToArray(), default));
    }
}
