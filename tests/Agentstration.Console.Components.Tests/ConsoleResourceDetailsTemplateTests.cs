using Agentstration.Agents;
using Agentstration.Agents.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConsoleResourceDetailsTemplateTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ManagedAndPermissionReadOnlyDefinitionsRetainIndependentAccessCommands(bool managed)
    {
        using var context = Context();
        var resource = Example();
        if (managed) resource = resource with { Governance = ResourceGovernance.Core("fixture") };
        var cut = Render(context, resource);
        if (!managed) cut.Render(p => p.Add(c => c.DefinitionReadOnlyReason, ResourceReadOnlyReason.InsufficientPermissions));
        cut.Render(p => p.Add(c => c.HasDraft, true).Add(c => c.DraftActions, "<button>Save</button>").Add(c => c.YamlReadOnly, false));
        var submissions = 0;
        var edit = new Microsoft.AspNetCore.Components.Forms.EditContext(new object());
        cut.Render(p => p.Add(c => c.DefinitionContent, _ => builder =>
        {
            builder.OpenComponent<ResourceDefinitionForm>(0);
            builder.AddAttribute(1, nameof(ResourceDefinitionForm.Id), "locked-definition");
            builder.AddAttribute(2, nameof(ResourceDefinitionForm.EditContext), edit);
            builder.AddAttribute(3, nameof(ResourceDefinitionForm.FieldLabels), new Dictionary<string, string>());
            builder.AddAttribute(4, nameof(ResourceDefinitionForm.OnValidSubmit), EventCallback.Factory.Create<Microsoft.AspNetCore.Components.Forms.EditContext>(this, _ => submissions++));
            builder.CloseComponent();
        }));
        Assert.AreEqual(managed ? ResourceReadOnlyReason.Managed : ResourceReadOnlyReason.InsufficientPermissions,
            cut.FindComponent<ResourceDetailsTemplate>().Instance.DefinitionReadOnlyReason);
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"));
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        Assert.IsTrue(cut.Find("#locked-definition fieldset").HasAttribute("disabled"));
        await cut.Find("#locked-definition").SubmitAsync();
        Assert.AreEqual(0, submissions);
        await cut.InvokeAsync(() => nav.NavigateSection("yaml"));
        Assert.IsTrue(cut.Find("textarea").HasAttribute("readonly"));
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        Assert.IsNotNull(cut.FindComponent<Stub<ResourceAccessPanel>>());
        Assert.AreEqual(resource.Uid, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance.Parameters.Get(p => p.ResourceUid));
    }

    [TestMethod]
    public async Task AdapterPreservesPolicyDraftAcrossGenerationRefreshAndRejectsStaleResourceCallbacks()
    {
        using var context = Context();
        var resource = Example();
        var cut = Render(context, resource);
        Assert.IsEmpty(cut.FindComponents<Stub<ResourceAccessPanel>>());
        var nav = cut.FindComponent<ResourceNavigation>().Instance;
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        var policy = cut.FindComponent<Stub<ResourceAccessPanel>>().Instance;
        Assert.AreEqual(resource.Uid, policy.Parameters.Get(p => p.ResourceUid));
        Assert.AreEqual(resource.ScopeRef, policy.Parameters.Get(p => p.OwnerScopeRef));
        var oldCallback = policy.Parameters.Get(p => p.DraftChanged);
        await cut.InvokeAsync(() => oldCallback.InvokeAsync(true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.IsEmpty(cut.FindAll(".unsaved-changes-bar"), "Access retains its own Save/Discard commands.");
        await cut.InvokeAsync(() => nav.NavigateSection("definition/configuration"));
        cut.Render(p => p.Add(c => c.Resource, resource with { Generation = 2 }));
        await cut.InvokeAsync(() => nav.NavigateSection("access"));
        Assert.AreSame(policy, cut.FindComponent<Stub<ResourceAccessPanel>>().Instance);
        Assert.AreEqual(2L, policy.Parameters.Get(p => p.Generation));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        cut.Render(p => p.Add(c => c.Resource, resource with { Uid = Guid.NewGuid() }));
        Assert.IsEmpty(cut.FindComponents<Stub<ResourceAccessPanel>>());
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        await cut.InvokeAsync(() => oldCallback.InvokeAsync(true));
        Assert.IsFalse(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
        Assert.AreEqual("overview", cut.Instance.SectionState.CurrentPath);
    }

    [TestMethod]
    public async Task AdapterControlsAccessVisibilityAndYamlEditingCapability()
    {
        using var context = Context();
        var cut = Render(context, Example());
        cut.Render(p => p.Add(c => c.AllowAccess, false));
        Assert.IsEmpty(cut.FindAll("[data-resource-section=access]"));
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("yaml"));
        Assert.IsTrue(cut.Find("textarea").HasAttribute("readonly"));
        string? changed = null;
        cut.Render(p => p.Add(c => c.YamlReadOnly, false).Add(c => c.YamlChanged, value => changed = value));
        cut.Find("textarea").Input("invalid: [yaml");
        Assert.AreEqual("invalid: [yaml", changed, "The adapter forwards input; parsing remains in the family.");
        cut.Render(p => p.Add(c => c.Busy, true));
        Assert.IsTrue(cut.Find("textarea").HasAttribute("disabled"));
        cut.Render(p => p.Add(c => c.HasDraft, true));
        Assert.IsTrue(cut.FindComponent<UnsavedNavigationGuard>().Instance.HasUnsavedChanges!());
    }

    private static IRenderedComponent<ConsoleResourceDetailsTemplate> Render(BunitContext context, AgentResource resource) =>
        context.Render<ConsoleResourceDetailsTemplate>(p => p
            .Add(c => c.Resource, resource).Add(c => c.ResourceIdentity, "example|instance")
            .Add(c => c.Name, "Example").Add(c => c.ResourceType, "Agent")
            .Add(c => c.OverviewConfiguration, "<p>Saved settings</p>")
            .Add(c => c.DefinitionContent, _ => builder => builder.AddContent(0, "Definition"))
            .Add(c => c.Yaml, "kind: Agent"));

    private static AgentResource Example() => new()
    {
        Uid = Guid.NewGuid(),
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = AgentResourceKinds.Agent,
        ScopeRef = ResourceScopeRef.Instance,
        Metadata = new() { Name = "example" },
        Generation = 1,
        Definition = new() { DisplayName = "Example", Instructions = "Example instructions", ModelProfile = new("default", ResourceScopeRef.Instance) }
    };

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.ComponentFactories.AddStub<ResourceAccessPanel>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/agents/example");
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/configuration", "access", "yaml" })
        {
            var target = "http://localhost/agents/example#" + path;
            module.Setup<string>("navigate", call => Equals(call.Arguments[0], target)).SetResult(target);
        }
        return context;
    }
}
