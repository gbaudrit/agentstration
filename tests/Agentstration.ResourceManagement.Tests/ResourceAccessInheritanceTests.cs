using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.ResourceManagement.Tests;

[TestClass]
public sealed class ResourceAccessInheritanceTests
{
    private static readonly ResourceScopeRef Tenant = ResourceScopeRef.Tenant(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly ResourceScopeRef Workspace = ResourceScopeRef.Workspace(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly ResourceAccessAction[] Actions = [ResourceAccessAction.Discover, ResourceAccessAction.Use];

    [TestMethod]
    public void GeneralRuleCoversFutureDescendantsWithoutEnumeratedTargets()
    {
        var node = Node("Agent");
        var policy = new ResourceAccessPolicy(ResourceAccessGraph.Identity(node.Identity), []) { AllDescendantActions = Actions };
        foreach (var scope in new[] { Workspace, ResourceScopeRef.Workspace(Guid.NewGuid()) })
            Assert.IsTrue(ResourceAccessRules.Evaluate(policy, scope, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).Allowed);
        Assert.HasCount(0, policy.Grants);
    }

    [TestMethod]
    public void NearestWorkspaceExceptionOverridesTenantPropagationAndCanRestoreInheritance()
    {
        var node = Node("Agent");
        ResourceAccessGrant tenant = new(Tenant, Actions, true);
        ResourceAccessGrant denied = new(Workspace, []) { DeniedActions = Actions };
        var policy = new ResourceAccessPolicy(ResourceAccessGraph.Identity(node.Identity), [tenant, denied]);
        Assert.AreEqual(ResourceAccessDecisionReasons.ExplicitDeny,
            ResourceAccessRules.Evaluate(policy, Workspace, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).ReasonCode);
        Assert.IsTrue(ResourceAccessRules.Evaluate(policy with { Grants = [tenant] }, Workspace,
            [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).Allowed);
        var deniedTenant = tenant with { Actions = [], DeniedActions = Actions };
        Assert.IsTrue(ResourceAccessRules.Evaluate(policy with { Grants = [deniedTenant, new(Workspace, Actions)] },
            Workspace, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).Allowed);
    }

    [TestMethod]
    public void RemovingGeneralRulePreservesTargetedActionsAndDenials()
    {
        var node = Node("Agent");
        var policy = new ResourceAccessPolicy(ResourceAccessGraph.Identity(node.Identity),
            [new(Workspace, [ResourceAccessAction.Discover]) { DeniedActions = [ResourceAccessAction.Use] }]);
        Assert.IsTrue(ResourceAccessRules.Evaluate(policy, Workspace, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Discover).Allowed);
        Assert.IsFalse(ResourceAccessRules.Evaluate(policy, Workspace, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).Allowed);
    }

    [TestMethod]
    public void ParentEligibilityDoesNotGrantMembersAndRevocationPreservesTheirRules()
    {
        var parent = Node("ExtensionRegistration", granted: true);
        var tool = Node("Tool");
        var relations = new Relations(tool.Identity.Uid, [parent.Identity.Uid], []);
        var graph = Graph([parent, tool], relations);
        Assert.IsFalse(Evaluate(graph, tool).Allowed);
        tool = tool with { Policy = Granted(tool.Identity) };
        graph = Graph([parent, tool], relations);
        Assert.IsTrue(Evaluate(graph, tool).Allowed);
        graph = Graph([parent with { Policy = null }, tool], relations);
        Assert.AreEqual(ResourceAccessDecisionReasons.ParentAccessDenied, Evaluate(graph, tool).ReasonCode);
        Assert.IsNotNull(tool.Policy);
        Assert.IsTrue(Evaluate(Graph([parent, tool], relations), tool).Allowed);
    }

    [TestMethod]
    public void ParentDiscoverOnlyCannotAuthorizeMemberUse()
    {
        var parent = Node("ExtensionRegistration");
        parent = parent with { Policy = new(ResourceAccessGraph.Identity(parent.Identity), [new(Workspace, [ResourceAccessAction.Discover])]) };
        var tool = Node("Tool", granted: true);
        var graph = Graph([parent, tool], new Relations(tool.Identity.Uid, [parent.Identity.Uid], []));
        Assert.IsTrue(graph.Evaluate(ResourceAccessGraph.Identity(tool.Identity), Workspace,
            [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Discover).Allowed);
        Assert.AreEqual(ResourceAccessDecisionReasons.ParentAccessDenied, Evaluate(graph, tool).ReasonCode);
    }

    [TestMethod]
    public void MultipleSourcesCombineAllowsButExplicitMemberDenyAlwaysWins()
    {
        var first = Node("ToolSet");
        var second = Node("ToolSet", granted: true);
        var tool = Node("Tool");
        var relations = new Relations(tool.Identity.Uid, [], [first.Identity.Uid, second.Identity.Uid]);
        Assert.AreEqual(ResourceAccessDecisionReasons.ToolSetInherited, Evaluate(Graph([first, second, tool], relations), tool).ReasonCode);
        tool = tool with { Policy = new(ResourceAccessGraph.Identity(tool.Identity),
            [new(Workspace, []) { DeniedActions = Actions }]) };
        Assert.AreEqual(ResourceAccessDecisionReasons.ExplicitDeny, Evaluate(Graph([first, second, tool], relations), tool).ReasonCode);
    }

    [TestMethod]
    public void SiblingScopeAndStaleIdentityCannotReceiveInheritedAuthority()
    {
        var node = Node("Tool", granted: true);
        var graph = Graph([node]);
        var foreignConsumer = ResourceScopeRef.Workspace(Guid.NewGuid());
        var tenantOwned = node with { Identity = node.Identity with { ScopeRef = Tenant }, Policy = null };
        Assert.AreEqual(ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree,
            Graph([tenantOwned]).Evaluate(ResourceAccessGraph.Identity(tenantOwned.Identity), foreignConsumer,
                [ResourceScopeRef.Tenant(Guid.NewGuid()), ResourceScopeRef.Instance], ResourceAccessAction.Use).ReasonCode);
        Assert.AreEqual(ResourceAccessDecisionReasons.ResourceIdentityStale,
            graph.Evaluate(ResourceAccessGraph.Identity(node.Identity) with { Generation = 99 }, Workspace,
                [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use).ReasonCode);
    }

    [TestMethod]
    public void InvalidEvidenceAndRelationshipCyclesFailClosed()
    {
        var node = Node("Tool", granted: true);
        Assert.AreEqual(ResourceAccessDecisionReasons.RelationshipInvalid,
            Evaluate(Graph([node], new Relations(node.Identity.Uid, [], [], valid: false)), node).ReasonCode);
        var graph = Graph([node], new Relations(node.Identity.Uid, [node.Identity.Uid], []));
        Assert.AreEqual(ResourceAccessDecisionReasons.ParentAccessDenied, Evaluate(graph, node).ReasonCode);
    }

    private static ResourceAccessDocument Node(string kind, bool granted = false)
    {
        var identity = new ResourceIdentitySnapshot(Guid.NewGuid(), ResourceScopeRef.Instance, ResourceNamespace.Default,
            kind, Guid.NewGuid().ToString("N"), 1, null, "\"resource\"");
        return new(identity, ResourceGovernance.User, JsonSerializer.SerializeToElement(new { }), granted ? Granted(identity) : null);
    }

    private static ResourceAccessPolicy Granted(ResourceIdentitySnapshot identity) =>
        new(ResourceAccessGraph.Identity(identity), [new(Workspace, Actions)]);
    private static ResourceAccessGraph Graph(IEnumerable<ResourceAccessDocument> nodes, params IResourceAccessRelationshipProvider[] providers) =>
        new(nodes.ToDictionary(value => value.Identity.Uid), providers);
    private static ResourceAccessDecision Evaluate(ResourceAccessGraph graph, ResourceAccessDocument node) =>
        graph.Evaluate(ResourceAccessGraph.Identity(node.Identity), Workspace, [Tenant, ResourceScopeRef.Instance], ResourceAccessAction.Use);

    private sealed class Relations(Guid target, Guid[] parents, Guid[] sources, bool valid = true) : IResourceAccessRelationshipProvider
    {
        public ResourceAccessRelationships Describe(ResourceAccessDocument resource, IReadOnlyDictionary<Guid, ResourceAccessDocument> documents) =>
            resource.Identity.Uid == target ? new() { Valid = valid, Prerequisites = parents, InheritFrom = sources } : new();
    }
}
