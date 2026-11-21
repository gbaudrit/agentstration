using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.ResourceRelations;
using Bunit;
using Bunit.TestDoubles;

namespace Agentstration.Web.Tests;

public sealed partial class ResourceRelationsTests
{
    [TestMethod]
    [DataRow("Agent")]
    [DataRow("Flow")]
    [DataRow("ExtensionRegistration")]
    public async Task SelectionUsesTheExistingFamilyPreviewWithExactOwnerAndSeparateActor(string kind)
    {
        var actor = ResourceScopeRef.Workspace(Guid.NewGuid());
        var root = Node("resource", ns: "shared") with { Identity = NodeIdentity(kind), Evidence = new(ETag: "observed") };
        root = root with { Id = SystemGraphIds.Definition(root.Identity.ResourceUid) };
        var client = new Client([root], []);
        using var context = Context(client);
        context.ComponentFactories.AddStub<AgentResourcePreview>();
        context.ComponentFactories.AddStub<FlowResourcePreview>();
        context.ComponentFactories.AddStub<ExtensionResourcePreview>();
        var page = Render(context, root, actor);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        Assert.IsEmpty(page.FindComponents<ResourceRelationsPreview>());
        await page.Find(".relations-view-switch button:nth-child(2)").ClickAsync();
        await page.Find(".relations-resource-row").ClickAsync();
        var target = ScopedResourceAddress.Create(root.Identity.OwnerScope, root.Identity.Namespace, kind, root.Identity.Name);
        var expected = new ResourcePreviewExpectation(actor, root.Identity.ResourceUid, ETag: "observed");
        if (kind == "Agent")
        {
            var preview = page.FindComponent<Stub<AgentResourcePreview>>().Instance.Parameters;
            Assert.AreEqual(target, preview.Get(p => p.Address));
            Assert.AreEqual(expected, preview.Get(p => p.Expectation));
            StringAssert.Contains(preview.Get(p => p.OverviewHref), "/namespaces/shared/agents/resource?");
            StringAssert.Contains(preview.Get(p => p.OverviewHref), "context=" + Uri.EscapeDataString(actor.Value));
        }
        else if (kind == "Flow")
        {
            var preview = page.FindComponent<Stub<FlowResourcePreview>>().Instance.Parameters;
            Assert.AreEqual(target, preview.Get(p => p.Address));
            Assert.AreEqual(expected, preview.Get(p => p.Expectation));
            StringAssert.Contains(preview.Get(p => p.DesignerHref), "/namespaces/shared/flows/resource/designer?");
            StringAssert.Contains(preview.Get(p => p.DesignerHref), "context=" + Uri.EscapeDataString(actor.Value));
        }
        else
        {
            var preview = page.FindComponent<Stub<ExtensionResourcePreview>>().Instance.Parameters;
            Assert.AreEqual(target, preview.Get(p => p.Address));
            Assert.AreEqual(expected, preview.Get(p => p.Expectation));
            Assert.IsFalse(preview.Get(p => p.CanManageEnrollment));
        }
        Assert.IsEmpty(page.FindAll(".relations-detail-identity"), "The graph must not duplicate the family identity panel.");
        Assert.HasCount(1, page.FindAll(".relations-technical"));
        Assert.HasCount(1, client.Calls);
    }

    [TestMethod]
    [DataRow("publication", "Agent")]
    [DataRow("recorded-generation", "Agent")]
    [DataRow("publication", "Flow")]
    [DataRow("definition", "Tool")]
    public async Task HistoricalStatesAndUnsupportedFamiliesRetainGraphDetailsWithoutReadingCurrentDefinitions(string state, string kind)
    {
        var identity = NodeIdentity(kind);
        var root = new SystemGraphNode(state == "definition" ? SystemGraphIds.Definition(identity.ResourceUid) : SystemGraphIds.Generation(identity.ResourceUid, 2), identity, state, new(2));
        using var context = Context(new Client([root], []));
        var page = Render(context, root, ResourceScopeRef.Instance);
        page.WaitForAssertion(() => Assert.AreEqual("false", page.Find("section.resource-relations").GetAttribute("aria-busy")));
        await page.Find(".relations-view-switch button:nth-child(2)").ClickAsync();
        await page.Find(".relations-resource-row").ClickAsync();
        Assert.AreEqual("resource", page.Find("aside h3").TextContent);
        Assert.IsEmpty(page.FindComponents<AgentResourcePreview>());
        Assert.IsEmpty(page.FindComponents<FlowResourcePreview>());
        Assert.HasCount(1, page.FindAll(".relations-technical"));
    }

    private static SystemGraphIdentity NodeIdentity(string kind) => new(Guid.NewGuid(), ResourceScopeRef.Instance, new("shared"), kind, "resource");
}
