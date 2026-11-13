using Agentstration.Flows.Application;
using Agentstration.Flows;
using Agentstration.Resources;

namespace Agentstration.Application.Tests;

public sealed partial class FlowTests
{
    [TestMethod]
    public async Task PublishedVersionBatchIsOneBoundedQueryAndKeepsOwnerAndNamespaceIsolated()
    {
        var commands = new FlowCommandInterceptor();
        await using var fixture = await FlowFixture.CreateAsync(commands);
        var instance = ResourceScopeRef.Instance;
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var id = new FlowId("same-name", new("team"));
        var otherNamespace = new FlowId("same-name", new("other"));
        var definition = new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"));
        foreach (var (owner, flow, version) in new[] { (instance, id, "1.0.0"), (instance, id, "2.0.0"), (tenant, id, "7.0.0"), (instance, otherNamespace, "9.0.0") })
            await fixture.Repository.CreateVersionAsync(new FlowVersion(owner, flow, version, null, definition, new Dictionary<string, string>(), Now), default);
        commands.Reset();
        var result = await fixture.Repository.ListPublishedVersionsAsync([(instance, id), (tenant, id), (instance, new("missing"))], default);
        Assert.HasCount(2, result);
        Assert.AreEqual("2.0.0", result[(instance, id)]);
        Assert.AreEqual("7.0.0", result[(tenant, id)]);
        Assert.HasCount(1, commands.CommandTexts);
        commands.Reset();
        Assert.IsEmpty(await fixture.Repository.ListPublishedVersionsAsync([], default));
        Assert.IsEmpty(commands.CommandTexts);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Repository.ListPublishedVersionsAsync(Enumerable.Repeat((instance, id), 201).ToArray(), default));
    }

    [TestMethod]
    public async Task PublicationListDoesNotLoadDraftsAndDraftReadsKeepExactIsolation()
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
        foreach (var (scope, flow, author, revision) in new[] { (owner, id, workspace, 7L), (tenant, id, workspace, 11L), (owner, otherNamespace, workspace, 13L), (owner, id, otherWorkspace, 99L) })
        {
            var draft = await fixture.Repository.GetDraftExactAsync(author, scope, flow, default);
            Assert.IsNotNull(draft);
            Assert.AreEqual(revision, draft.Value.Revision);
        }
        Assert.IsNull(await fixture.Repository.GetDraftExactAsync(otherWorkspace, tenant, id, default));
        await fixture.Service.CreateExactAsync(owner, new CreateFlowCommand(id.Value, null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "assistant"))), id.Namespace, default);
        commands.Reset();
        var page = await fixture.Service.ListExactAsync(owner, id.Namespace, 0, 1, default);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual(id, page.Items[0].Value.Id);
        Assert.IsNull(page.Items[0].Value.ActiveVersion);
        Assert.HasCount(1, commands.CommandTexts);
        Assert.IsFalse(commands.CommandTexts.Any(command => command.Contains("Draft", StringComparison.OrdinalIgnoreCase)),
            "A publication list must not resolve draft counters.");
    }
}
