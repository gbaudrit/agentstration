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
    public void ManagedResourceKeepsDefinitionReadOnlyWhileGrantCanBeSaved()
    {
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var scopes = new FakeScopes(uid, tenant);
        var policies = new FakePolicies();
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
        using var context = new BunitContext();
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
    public void EvaluationUsesSavedGenericDecisionsAndIdentifiesUnsavedGrants()
    {
        using var culture = new TestCultureScope("en-US");
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeScopes(uid, tenant));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new FakePolicies());
        var page = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(value => value.OwnerScopeRef, ResourceScopeRef.Instance).Add(value => value.Namespace, ResourceNamespace.Default)
            .Add(value => value.Kind, "Agent").Add(value => value.Name, "managed-agent").Add(value => value.EvaluateSavedAccess, true));
        page.WaitForElement("[data-testid=resource-access-target]").Change(tenant.Value);
        page.Find("[data-testid=resource-access-add]").Click();
        page.Find("[data-testid=resource-access-evaluation-target]").Change(tenant.Value);
        page.Find("[data-testid=resource-access-evaluate]").Click();
        page.WaitForElement("[data-testid=resource-access-decisions]");
        StringAssert.Contains(page.Find("[data-testid=resource-access-decisions]").TextContent, "Allowed");
        StringAssert.Contains(page.Find("[data-testid=resource-access-decisions]").TextContent, "Denied");
        StringAssert.Contains(page.Markup, "unsaved changes");
        StringAssert.Contains(page.Markup, "do not evaluate user permissions, UsePolicy");
    }

    [TestMethod]
    public void ConflictRetainsIndependentActionsAndReviewDoesNotDiscardDraft()
    {
        using var culture = new TestCultureScope("en-US");
        var uid = Guid.NewGuid();
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var policies = new FakePolicies
        {
            Existing = new(new(new(uid, ResourceScopeRef.Instance, ResourceNamespace.Default, "Agent"),
                [new(tenant, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), "\"policy-1\""),
            FailWrites = true
        };
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new FakeScopes(uid, tenant));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);
        var page = context.Render<ResourceAccessPanel>(parameters => parameters
            .Add(value => value.OwnerScopeRef, ResourceScopeRef.Instance).Add(value => value.Namespace, ResourceNamespace.Default)
            .Add(value => value.Kind, "Agent").Add(value => value.Name, "managed-agent"));
        page.WaitForElement("[data-testid=resource-access-grant]");
        page.FindAll(".access-actions input")[0].Change(false);
        page.Find("[data-testid=resource-access-save]").Click();
        page.WaitForElement("[data-testid=resource-access-review]");
        Assert.IsFalse(page.FindAll(".access-actions input")[0].HasAttribute("checked"));
        policies.CurrentEtag = "\"new-baseline\"";
        page.Find("[data-testid=resource-access-review]").Click();
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("[data-testid=resource-access-review]")));
        StringAssert.Contains(page.Find(".access-current-policy").TextContent, "Discover, Use");
        StringAssert.Contains(page.Find(".access-current-policy").TextContent, tenant.Value);
        StringAssert.Contains(page.Find(".access-current-policy").TextContent, "Exact target only");
        Assert.IsFalse(page.FindAll(".access-actions input")[0].HasAttribute("checked"));
        policies.FailWrites = false;
        page.Find("[data-testid=resource-access-save]").Click();
        page.WaitForElement("[data-testid=resource-access-saved]");
        CollectionAssert.AreEqual(new[] { ResourceAccessAction.Use }, policies.Saved!.Grants.Single().Actions.ToArray());
        Assert.IsNull(policies.Saved.Resource.Generation);
        Assert.AreEqual("\"new-baseline\"", policies.LastEtag);
    }

    private sealed class FakePolicies : IResourceAccessPolicyClient
    {
        public Task<ResourceAccessDecision> EvaluateAsync(EvaluateResourceAccessRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceAccessDecision(request.Action == ResourceAccessAction.Discover,
                request.Action == ResourceAccessAction.Discover ? ResourceAccessDecisionReasons.ExactGrant : ResourceAccessDecisionReasons.GrantMissing));
        public bool FailWrites { get; set; }
        public string CurrentEtag { get; set; } = "\"policy-1\"";
        public string? LastEtag { get; private set; }
        public ResourceAccessPolicySnapshot? Existing { get; init; }
        public ResourceAccessPolicy? Saved { get; private set; }
        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) => Task.FromResult(Existing is null ? null : Existing with { ETag = CurrentEtag });
        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken cancellationToken)
        {
            Saved = policy;
            LastEtag = etag;
            if (FailWrites) throw new AgentstrationApiException("PRIVATE", "id", System.Net.HttpStatusCode.Conflict);
            return Task.FromResult(new ResourceAccessPolicySnapshot(policy, "\"policy-2\""));
        }
    }

    private sealed class FakeScopes(Guid uid, ResourceScopeRef tenant, bool canWriteOwner = true) : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceScopeInventoryResponse([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", null, false,
                    [new(uid, ResourceNamespace.Default, "Agent", "managed-agent", DateTimeOffset.UnixEpoch)]),
                new(tenant, ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, true, [])
            ]));

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", canWriteOwner),
                new(tenant, ResourceScopeKind.Tenant, "Development", true)
            ]);
    }
}
