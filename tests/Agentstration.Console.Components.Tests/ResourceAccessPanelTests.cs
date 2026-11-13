using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourceAccessPanelTests
{
    [TestMethod]
    [DataRow("en-US", "New grant", "Add to list", "Cancel addition")]
    [DataRow("fr-FR", "Nouvelle autorisation", "Ajouter à la liste", "Annuler l’ajout")]
    public void SavingIncludesTheSelectedGrantWithoutAnAddStep(string culture, string openLabel, string addLabel, string cancelLabel)
    {
        using var cultureScope = new TestCultureScope(culture);
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies();
        using var context = Context(policies, new FakeScopes(Guid.NewGuid(), tenant));
        var cut = RenderPanel(context);
        Assert.AreEqual(openLabel, cut.Find("[data-testid='resource-access-open-add']").TextContent);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        Assert.AreEqual(cancelLabel, cut.Find("[data-testid='resource-access-open-add']").TextContent);
        Assert.AreEqual(addLabel, cut.Find("[data-testid='resource-access-add']").TextContent);
        cut.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        cut.FindAll(".access-add input[type='checkbox']")[2].Change(true);
        Assert.IsFalse(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
        cut.Find("[data-testid='resource-access-save']").Click();
        Assert.IsNotNull(policies.Saved);
        var grant = policies.Saved.Grants.Single();
        Assert.AreEqual(tenant, grant.TargetScopeRef);
        Assert.IsTrue(grant.IncludeDescendants);
        CollectionAssert.AreEquivalent(new[] { ResourceAccessAction.Discover, ResourceAccessAction.Use }, grant.Actions.ToArray());
        Assert.AreEqual(1, policies.PutCalls);
        Assert.HasCount(1, cut.FindAll("[data-testid='resource-access-grant']"));
        Assert.IsTrue(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void SavingCombinesListedAndPendingGrantsAndRetainsThePolicyEtag()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var anotherTenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var uid = Guid.NewGuid();
        var policies = new FakePolicies
        {
            Existing = new(new ResourceAccessPolicy(new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent"), []), "\"existing-policy\"")
        };
        using var context = Context(policies, new FakeScopes(uid, tenant, additionalTenant: anotherTenant));
        var cut = RenderPanel(context);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        cut.Find("[data-testid='resource-access-add']").Click();
        Assert.IsNull(policies.Saved);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(anotherTenant.Value);
        cut.Find("[data-testid='resource-access-save']").Click();
        Assert.IsNotNull(policies.Saved);
        CollectionAssert.AreEquivalent(new[] { tenant, anotherTenant }, policies.Saved.Grants.Select(grant => grant.TargetScopeRef).ToArray());
        Assert.AreEqual(1, policies.PutCalls);
        Assert.AreEqual("\"existing-policy\"", policies.ReceivedEtag);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void CancellingAnUnlistedGrantClearsItWithoutSaving(bool discardAll)
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies();
        using var context = Context(policies, new FakeScopes(Guid.NewGuid(), tenant));
        var cut = RenderPanel(context);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        Assert.IsFalse(cut.Find("[data-testid='resource-access-discard']").HasAttribute("disabled"));
        cut.Find(discardAll ? "[data-testid='resource-access-discard']" : "[data-testid='resource-access-open-add']").Click();
        Assert.IsTrue(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
        cut.Find("[data-testid='resource-access-open-add']").Click();
        Assert.IsTrue(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
        Assert.IsFalse(cut.FindAll("[data-testid='resource-access-target'] option").Any(option => option.HasAttribute("selected") && option.GetAttribute("value") != string.Empty));
        Assert.AreEqual(0, policies.PutCalls);
        Assert.IsEmpty(cut.FindAll("[data-testid='resource-access-grant']"));
    }

    [TestMethod]
    public void AnInvalidPendingTargetCannotBeSilentlyOmittedFromSaving()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies();
        using var context = Context(policies, new FakeScopes(Guid.NewGuid(), tenant));
        var cut = RenderPanel(context);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(ResourceScopeRef.Instance.Value);
        cut.Find("[data-testid='resource-access-save']").Click();
        Assert.HasCount(1, cut.FindAll("[data-testid='resource-access-invalid-target']"));
        Assert.AreEqual(0, policies.PutCalls);
        Assert.IsEmpty(cut.FindAll("[data-testid='resource-access-saved']"));
    }

    [TestMethod]
    public void PendingGrantRequiresAnActionAndPreservesTheChosenActions()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies();
        using var context = Context(policies, new FakeScopes(Guid.NewGuid(), tenant));
        var cut = RenderPanel(context);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        cut.FindAll(".access-add input[type='checkbox']")[0].Change(false);
        cut.FindAll(".access-add input[type='checkbox']")[1].Change(false);
        Assert.IsTrue(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
        Assert.HasCount(1, cut.FindAll("[role='alert']"));
        Assert.AreEqual(0, policies.PutCalls);
        cut.FindAll(".access-add input[type='checkbox']")[1].Change(true);
        cut.Find("[data-testid='resource-access-save']").Click();
        Assert.IsNotNull(policies.Saved);
        CollectionAssert.AreEqual(new[] { ResourceAccessAction.Use }, policies.Saved.Grants.Single().Actions.ToArray());
    }

    private static BunitContext Context(FakePolicies policies, FakeScopes scopes)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(scopes);
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        return context;
    }

    private static IRenderedComponent<ResourceAccessPanel> RenderPanel(BunitContext context) => context.Render<ResourceAccessPanel>(parameters => parameters
        .Add(value => value.OwnerScopeRef, ResourceScopeRef.Instance)
        .Add(value => value.Namespace, ResourceNamespace.Default)
        .Add(value => value.Kind, "Agent").Add(value => value.Name, "managed-agent"));

    [TestMethod]
    public void ManagedResourceKeepsDefinitionReadOnlyWhileGrantCanBeSaved()
    {
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var scopes = new FakeScopes(uid, tenant);
        var policies = new FakePolicies();
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(scopes);
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);

        var rendered = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(component => component.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.Namespace, ResourceNamespace.Default)
            .Add(component => component.Kind, "Agent")
            .Add(component => component.Name, "managed-agent")
            .Add(component => component.Generation, 4)
            .Add(component => component.Version, "1.2.0")
            .Add(component => component.Governance, ResourceGovernance.Extension("registration", "example.extension", "1.2.0")));

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Find(".access-governance").TextContent, "example.extension");
            StringAssert.Contains(rendered.Find(".access-identity").TextContent, uid.ToString());
        });
        rendered.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        rendered.Find("[data-testid='resource-access-add']").Click();
        rendered.Find("[data-testid='resource-access-save']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(policies.Saved);
            var grant = policies.Saved.Grants.Single();
            Assert.AreEqual(tenant, grant.TargetScopeRef);
            CollectionAssert.AreEquivalent(new[] { ResourceAccessAction.Discover, ResourceAccessAction.Use }, grant.Actions.ToArray());
            Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.Find(".access-success").TextContent));
        });
    }

    [TestMethod]
    public void CallerWithoutOwnerWritePermissionGetsReadOnlyPolicy()
    {
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies
        {
            Existing = new(new ResourceAccessPolicy(
                new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent", 1),
                [new(tenant, [ResourceAccessAction.Use])]), "\"policy-1\"")
        };
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeScopes(uid, tenant, canWriteOwner: false));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);

        var rendered = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(component => component.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.Namespace, ResourceNamespace.Default)
            .Add(component => component.Kind, "Agent")
            .Add(component => component.Name, "managed-agent")
            .Add(component => component.Generation, 1));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(0, rendered.FindAll("[data-testid='resource-access-save']").Count);
            Assert.IsTrue(rendered.FindAll(".access-actions input").All(value => value.HasAttribute("disabled")));
            Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.Find(".access-readonly").TextContent));
        });
    }

    [TestMethod]
    public void InvalidOwnerTargetShowsLocalizedFeedbackWithoutAddingGrant()
    {
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeScopes(uid, tenant));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new FakePolicies());

        var rendered = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(component => component.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(component => component.Namespace, ResourceNamespace.Default)
            .Add(component => component.Kind, "Agent")
            .Add(component => component.Name, "managed-agent"));

        rendered.WaitForElement("[data-testid='resource-access-target']");
        rendered.Find("[data-testid='resource-access-target']").Change(ResourceScopeRef.Instance.Value);
        rendered.Find("[data-testid='resource-access-add']").Click();

        Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.Find("[data-testid='resource-access-invalid-target']").TextContent));
        Assert.AreEqual(0, rendered.FindAll("[data-testid='resource-access-grant']").Count);
    }

    [TestMethod]
    public void CancellingAccessChangesRestoresTheLastSavedGrants()
    {
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies();
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeScopes(uid, tenant));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        var cut = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(value => value.OwnerScopeRef, ResourceScopeRef.Instance)
            .Add(value => value.Namespace, ResourceNamespace.Default)
            .Add(value => value.Kind, "Agent").Add(value => value.Name, "managed-agent"));
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-target']").Change(tenant.Value);
        cut.Find("[data-testid='resource-access-add']").Click();
        Assert.HasCount(1, cut.FindAll("[data-testid='resource-access-grant']"));
        cut.Find("[data-testid='resource-access-discard']").Click();
        Assert.IsEmpty(cut.FindAll("[data-testid='resource-access-grant']"));
        Assert.IsTrue(cut.Find("[data-testid='resource-access-save']").HasAttribute("disabled"));
        Assert.IsNull(policies.Saved);
        cut.Find("[data-testid='resource-access-open-add']").Click();
        cut.Find("[data-testid='resource-access-search']").Input("missing-scope");
        Assert.HasCount(1, cut.FindAll("[data-testid='resource-access-target'] option"));
    }

    private sealed class FakePolicies : IResourceAccessPolicyClient
    {
        public ResourceAccessPolicySnapshot? Existing { get; init; }
        public ResourceAccessPolicy? Saved { get; private set; }
        public int PutCalls { get; private set; }
        public string? ReceivedEtag { get; private set; }
        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) => Task.FromResult(Existing);
        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken cancellationToken)
        {
            PutCalls++;
            ReceivedEtag = etag;
            Saved = policy;
            return Task.FromResult(new ResourceAccessPolicySnapshot(policy, "\"policy-2\""));
        }
    }

    private sealed class FakeScopes(Guid uid, ResourceScopeRef tenant, bool canWriteOwner = true, ResourceScopeRef? additionalTenant = null) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken)
        {
            List<ResourceScopeInventoryNodeResponse> nodes = [
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", null, false,
                    [new(uid, ResourceNamespace.Default, "Agent", "managed-agent", DateTimeOffset.UnixEpoch)]),
                new(tenant, ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, true, [])
            ];
            if (additionalTenant is { } extra) nodes.Add(new(extra, ResourceScopeKind.Tenant, "Another tenant", ResourceScopeRef.Instance, true, []));
            return Task.FromResult(new ResourceScopeInventoryResponse(nodes));
        }

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken)
        {
            List<ResourceScopeTargetResponse> scopeTargets = [
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", canWriteOwner),
                new(tenant, ResourceScopeKind.Tenant, "Development", true)
            ];
            if (additionalTenant is { } extra) scopeTargets.Add(new(extra, ResourceScopeKind.Tenant, "Another tenant", true));
            return Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>(scopeTargets);
        }
    }
}
