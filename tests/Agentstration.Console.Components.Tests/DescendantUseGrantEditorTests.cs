using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DescendantUseGrantEditorTests
{
    [TestMethod]
    public void CandidateAndSavedGrantShowScopeKindAndName()
    {
        var tenant = new ResourceScopeTargetResponse(ResourceScopeRef.Tenant(Guid.NewGuid()), ResourceScopeKind.Tenant, "Development", true);
        var workspace = new ResourceScopeTargetResponse(ResourceScopeRef.Workspace(Guid.NewGuid()), ResourceScopeKind.Workspace, "Default", true);
        var grants = new List<DescendantUseGrant> { new(workspace.ScopeRef) };
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<DescendantUseGrantEditor>(parameters => parameters
            .Add(component => component.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.Targets, [tenant, workspace])
            .Add(component => component.Grants, grants));

        Assert.AreEqual("Tenant · Development", rendered.Find($"option[value='{tenant.ScopeRef.Value}']").TextContent);
        Assert.AreEqual("Workspace", rendered.Find(".grant-target-identity .resource-scope-badge").TextContent);
        Assert.AreEqual("Default", rendered.Find(".grant-target-identity strong").TextContent);
        Assert.AreEqual(workspace.ScopeRef.Value, rendered.Find(".grant-target-identity").GetAttribute("title"));
        Assert.AreEqual(0, rendered.FindAll(".grant-checkbox").Count);
    }

    [TestMethod]
    public void TenantGrantUsesCompactCheckboxAndCanBeRemoved()
    {
        var tenant = new ResourceScopeTargetResponse(ResourceScopeRef.Tenant(Guid.NewGuid()), ResourceScopeKind.Tenant, "Development", true);
        var grants = new List<DescendantUseGrant>();
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<DescendantUseGrantEditor>(parameters => parameters
            .Add(component => component.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.Targets, [tenant])
            .Add(component => component.Grants, grants));

        rendered.Find(".grant-add select").Change(tenant.ScopeRef.Value);
        rendered.Find(".grant-add-button").Click();
        Assert.AreEqual("Development", rendered.Find(".grant-target-identity strong").TextContent);
        Assert.AreEqual(tenant.ScopeRef, grants.Single().ScopeRef);

        rendered.Find(".grant-checkbox input").Change(true);
        Assert.IsTrue(grants.Single().IncludeDescendants);

        rendered.Find(".grant-remove").Click();
        Assert.AreEqual(0, grants.Count);
    }

    [TestMethod]
    public void SavedGrantRemainsVisibleWhenNoScopeCandidateIsReturned()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var rendered = context.Render<DescendantUseGrantEditor>(parameters => parameters
            .Add(component => component.OwnerScopeRef, tenant)
            .Add(component => component.Targets, Array.Empty<ResourceScopeTargetResponse>())
            .Add(component => component.Grants, [new(workspace)]));

        Assert.AreEqual(workspace.Value, rendered.Find(".grant-target-identity strong").TextContent);
        Assert.AreEqual(0, rendered.FindAll(".grant-add").Count);
    }
}
