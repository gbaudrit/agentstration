using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using RelationsComponent = Agentstration.Web.Components.ResourceRelations.ResourceRelations;

namespace Agentstration.Web.Tests;

public sealed partial class ConsoleResourceDetailsTemplateTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task LayoutAddsAgentRelationsLazilyWithoutChangingTheScreen(string culture)
    {
        using var locale = new TestCultureScope(culture);
        using var context = RelationsContext(out var navigation);
        var resource = Example() with { ScopeRef = navigation.Actor, Metadata = new() { Name = "example", Namespace = new("shared") } };
        var page = Render(context, resource);
        Assert.AreEqual("Relations", page.Find("[data-resource-section=relations]").TextContent.Trim());
        Assert.IsEmpty(page.FindComponents<Stub<RelationsComponent>>(), "Overview must not mount or load the graph.");
        var displayScope = ResourceScopeRef.Tenant(Guid.NewGuid());
        var draft = new LayoutDraft();
        var edit = new EditContext(draft);
        page.Render(p => p.Add(c => c.PresentationScopeRef, displayScope).Add(c => c.HasDraft, true)
            .Add(c => c.DefinitionContent, _ => builder =>
            {
                builder.OpenComponent<ResourceDefinitionForm>(0);
                builder.AddAttribute(1, nameof(ResourceDefinitionForm.Id), "layout-definition");
                builder.AddAttribute(2, nameof(ResourceDefinitionForm.EditContext), edit);
                builder.AddAttribute(3, nameof(ResourceDefinitionForm.ChildContent), (RenderFragment)(field =>
                {
                    field.OpenElement(0, "input");
                    field.AddAttribute(1, "id", "layout-draft");
                    field.AddAttribute(2, "value", draft.Name);
                    field.AddAttribute(3, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(draft,
                        args => draft.Name = args.Value?.ToString() ?? string.Empty));
                    field.CloseElement();
                }));
                builder.CloseComponent();
            }));
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        page.Find("#layout-draft").Input("Unsaved input");
        var definition = page.FindComponent<ResourceDefinitionForm>().Instance;
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("relations"));
        var relations = page.FindComponent<Stub<RelationsComponent>>().Instance;
        Assert.AreEqual(navigation.Actor, relations.Parameters.Get(p => p.Context));
        Assert.AreNotEqual(displayScope, relations.Parameters.Get(p => p.Context));
        Assert.AreEqual(resource.Namespace, relations.Parameters.Get(p => p.Namespace));
        Assert.AreEqual(SystemGraphIds.Definition(resource.Uid), relations.Parameters.Get(p => p.Root));
        Assert.IsTrue(page.Find("#layout-definition").Closest(".resource-details-section")!.HasAttribute("hidden"));
        Assert.IsTrue(page.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/configuration"));
        Assert.AreSame(definition, page.FindComponent<ResourceDefinitionForm>().Instance);
        Assert.AreSame(edit, definition.EditContext);
        Assert.AreEqual("Unsaved input", page.Find("#layout-draft").GetAttribute("value"));
        Assert.AreSame(relations, page.FindComponent<Stub<RelationsComponent>>().Instance);
    }

    [TestMethod]
    public async Task InheritedAgentRelationsRetainTheActorRatherThanTheOwner()
    {
        using var context = RelationsContext(out var navigation);
        var resource = Example();
        var page = Render(context, resource);
        Assert.AreEqual(ResourceReadOnlyReason.Inherited, page.FindComponent<ResourceDetailsTemplate>().Instance.DefinitionReadOnlyReason);
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("relations"));
        var relations = page.FindComponent<Stub<RelationsComponent>>().Instance;
        Assert.AreEqual(navigation.Actor, relations.Parameters.Get(p => p.Context));
        Assert.AreNotEqual(resource.ScopeRef, relations.Parameters.Get(p => p.Context));
    }

    [TestMethod]
    public async Task AcceptedSavedGenerationAndActorChangesRefreshTheRetainedRelations()
    {
        using var context = RelationsContext(out var navigation);
        var resource = Example();
        var page = Render(context, resource);
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("relations"));
        var first = page.FindComponent<Stub<RelationsComponent>>().Instance;
        page.Render(p => p.Add(c => c.Resource, resource with { Generation = 2, ETag = "saved-v2" }));
        Assert.AreNotSame(first, page.FindComponent<Stub<RelationsComponent>>().Instance);
        Assert.IsTrue(page.Instance.SectionState.Is("relations"));
        var second = page.FindComponent<Stub<RelationsComponent>>().Instance;
        navigation.Actor = ResourceScopeRef.Workspace(Guid.NewGuid());
        page.Render();
        var third = page.FindComponent<Stub<RelationsComponent>>().Instance;
        Assert.AreNotSame(second, third);
        Assert.AreEqual(navigation.Actor, third.Parameters.Get(p => p.Context));
        page.Render(p => p.Add(c => c.Resource, resource with { Uid = Guid.NewGuid() }));
        Assert.IsEmpty(page.FindComponents<Stub<RelationsComponent>>(), "A replacement identity cannot retain the previous graph.");
    }

    [TestMethod]
    public void RelationsRequireASavedAgentAndAnActorRatherThanAnOwnerFallback()
    {
        using var context = Context();
        context.ComponentFactories.AddStub<RelationsComponent>();
        var resource = Example();
        var page = Render(context, resource);
        Assert.IsEmpty(page.FindAll("[data-resource-section=relations]"), "An owner alone must not establish an actor context.");
        using var ready = RelationsContext(out _);
        var agent = Render(ready, resource with { Uid = Guid.Empty });
        Assert.IsEmpty(agent.FindAll("[data-resource-section=relations]"));
        agent.Render(p => p.Add(c => c.Resource, resource with { Kind = "ModelProfile" }));
        Assert.IsEmpty(agent.FindAll("[data-resource-section=relations]"), "The first rollout is restricted centrally to Agents.");
        agent.Render(p => p.Add(c => c.Resource, null).Add(c => c.LoadState, new ResourceLoadState(ResourceLoadStatus.Loading)));
        Assert.IsEmpty(agent.FindAll("[data-resource-section=relations]"));
        Assert.IsEmpty(agent.FindComponents<Stub<RelationsComponent>>());
    }

    [TestMethod]
    public async Task LayoutPreservesFamilySectionsAndRejectsARelationsCollision()
    {
        using var context = RelationsContext(out _);
        var page = Render(context, Example());
        page.Render(p => p.Add(c => c.AdditionalSections, new[] { new ResourceNavigationItem("activity", "Activity", "clock") })
            .Add(c => c.AdditionalContent, _ => builder => builder.AddContent(0, "Family activity")));
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("activity"));
        Assert.Contains("Family activity", page.Find(".resource-details-section:not([hidden])").TextContent);
        await page.InvokeAsync(() => page.FindComponent<ResourceNavigation>().Instance.NavigateSection("relations"));
        Assert.IsNotNull(page.FindComponent<Stub<RelationsComponent>>());
        Assert.Throws<ArgumentException>(() => page.Render(p => p.Add(c => c.AdditionalSections,
            new[] { new ResourceNavigationItem("relations", "Family relations", "hierarchy") })));
    }

    private static BunitContext RelationsContext(out RelationsNavigation navigation)
    {
        navigation = new() { Actor = ResourceScopeRef.Workspace(Guid.NewGuid()) };
        var context = Context(navigation);
        context.ComponentFactories.AddStub<RelationsComponent>();
        return context;
    }

    private sealed class RelationsNavigation : IConsoleNavigationContext
    {
        public ResourceScopeRef Actor { get; set; }
        public string? ScopeRef => Actor.Value;
        public string AddToUri(string uri) => uri;
    }

    private sealed class LayoutDraft
    {
        public string Name { get; set; } = "Saved input";
    }
}
