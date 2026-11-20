using Agentstration.Resources;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceIdentityScopePresentationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StandardIdentityOnlyDeclaresAnOwnerWhenTheCallerSuppliesOne(bool hasOwner)
    {
        using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var navigation = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        navigation.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#overview");
        navigation.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        var page = context.Render<ResourceDetailsTemplate>(p => p.Add(c => c.Name, "Saved resource").Add(c => c.ResourceType, "Entry")
            .Add(c => c.TechnicalName, "saved-resource").Add(c => c.ResourceIdentity, "saved-resource")
            .Add(c => c.ScopeRef, hasOwner ? ResourceScopeRef.Instance : null)
            .Add(c => c.OverviewConfiguration, "<p>Actual configuration</p>")
            .Add(c => c.DefinitionContent, _ => b => b.AddContent(0, "Definition"))
            .Add(c => c.YamlContent, "<p>Family YAML</p>"));
        var identity = page.FindComponent<ResourceIdentitySummary>().Instance;
        Assert.AreEqual(hasOwner, identity.Scope is not null);
        Assert.HasCount(hasOwner ? 1 : 0, page.FindAll(".resource-identity-summary .resource-owner-scope"));
        Assert.HasCount(hasOwner ? 4 : 3, page.FindAll(".resource-identity-summary dt"));
        Assert.Contains("Saved resource", page.Find(".resource-identity-summary").TextContent);
    }
    [TestMethod]
    public void StandardIdentityPreservesTheEmptyLabelStateWhenTheFamilySupportsLabels()
    {
        using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var navigation = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        navigation.Setup<string>("currentLocation", _ => true).SetResult("http://localhost/#overview");
        navigation.Setup<string>("navigate", _ => true).SetResult("http://localhost/#overview");
        var labels = new Dictionary<string, string>();
        var page = context.Render<ResourceDetailsTemplate>(p => p.Add(c => c.Name, "Saved resource").Add(c => c.ResourceType, "Agent")
            .Add(c => c.TechnicalName, "saved-resource").Add(c => c.ResourceIdentity, "saved-resource").Add(c => c.ScopeRef, ResourceScopeRef.Instance)
            .Add(c => c.IdentityLabels, labels).Add(c => c.OverviewConfiguration, "<p>Actual configuration</p>")
            .Add(c => c.DefinitionContent, _ => b => b.AddContent(0, "Definition")).Add(c => c.YamlContent, "<p>Family YAML</p>"));
        Assert.AreSame(labels, page.FindComponent<ResourceIdentitySummary>().Instance.Labels);
        Assert.HasCount(1, page.FindAll(".resource-identity-summary .labels-summary > .muted"));
        Assert.AreEqual(0, page.FindComponent<LabelsSummary>().Instance.Values.Count);
        Assert.HasCount(5, page.FindAll(".resource-identity-summary dt"));
    }

}
