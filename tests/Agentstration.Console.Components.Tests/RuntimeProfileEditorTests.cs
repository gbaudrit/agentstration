using Agentstration.ResourceManagement.Contracts;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class RuntimeProfileEditorTests
{
    private static readonly Guid ResourceUid = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly ResourceScopeRef TenantScope = ResourceScopeRef.Tenant(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [TestMethod]
    public void ExistingProfileExposesLocalizedAccessManagement()
    {
        using var culture = new TestCultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(new NotificationState());
        context.Services.AddSingleton<IRuntimeProfilesClient>(new RuntimeProfilesClientStub());
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ScopeInventoryClientStub());
        var policies = new AccessPolicyClientStub();
        context.Services.AddSingleton<IResourceAccessPolicyClient>(policies);

        var rendered = context.Render<RuntimeProfileEditor>(parameters => parameters
            .Add(component => component.Name, "maf-builtin"));

        rendered.WaitForElement("[data-testid='runtime-profile-access-tab']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Accès", rendered.Find("[data-testid='runtime-profile-access-tab']").TextContent.Trim());
            StringAssert.Contains(rendered.Find(".access-identity").TextContent, ResourceUid.ToString());
            Assert.AreEqual(0, rendered.FindAll("[data-testid='runtime-profile-form']").Count);
        });

        rendered.Find("[data-testid='resource-access-target']").Change(TenantScope.Value);
        rendered.Find("[data-testid='resource-access-add']").Click();
        rendered.Find("[data-testid='resource-access-save']").Click();

        rendered.WaitForAssertion(() =>
        {
            var grant = policies.Saved?.Grants.Single();
            Assert.IsNotNull(grant);
            Assert.AreEqual(TenantScope, grant.TargetScopeRef);
        });
    }

    private sealed class RuntimeProfilesClientStub : IRuntimeProfilesClient
    {
        private static readonly RuntimeProfileResource Profile = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = RuntimeProfileResourceKinds.RuntimeProfile,
            Metadata = new ResourceMetadata { Name = "maf-builtin" },
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 2,
            Definition = new()
            {
                DisplayName = "Microsoft Agent Framework",
                RuntimeType = "microsoft-agent-framework"
            }
        };

        public Task<IReadOnlyList<RuntimeProfileSummaryResponse>> GetRuntimeProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RuntimeProfileSummaryResponse>>([]);

        public Task<ResourceSnapshot<RuntimeProfileResource>> GetRuntimeProfileAsync(string profileName, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<RuntimeProfileResource>(Profile, "\"runtime-profile\""));

        public Task<RuntimeProfileUsagesResponse> GetRuntimeProfileUsagesAsync(string profileName, CancellationToken cancellationToken) =>
            Task.FromResult(new RuntimeProfileUsagesResponse([], 0));

        public Task<ResourceSnapshot<RuntimeProfileResource>> CreateRuntimeProfileAsync(CreateRuntimeProfileRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<RuntimeProfileResource>> UpdateRuntimeProfileAsync(string profileName, PutRuntimeProfileRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteRuntimeProfileAsync(string profileName, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ScopeInventoryClientStub : IResourceScopeInventoryClient
    {
        public Task<ResourceScopeInventoryResponse> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ResourceScopeInventoryResponse([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", null, false,
                    [new(ResourceUid, ResourceNamespace.Default, RuntimeProfileResourceKinds.RuntimeProfile, "maf-builtin", DateTimeOffset.UnixEpoch)]),
                new(TenantScope, ResourceScopeKind.Tenant, "Development", ResourceScopeRef.Instance, true, [])
            ]));

        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetTargetsAsync(string kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([
                new(ResourceScopeRef.Instance, ResourceScopeKind.Instance, "Instance", true)
            ]);
    }

    private sealed class AccessPolicyClientStub : IResourceAccessPolicyClient
    {
        public ResourceAccessPolicy? Saved { get; private set; }

        public Task<ResourceAccessPolicySnapshot?> GetAsync(Guid resourceUid, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceAccessPolicySnapshot?>(null);

        public Task<ResourceAccessPolicySnapshot> PutAsync(ResourceAccessPolicy policy, string? etag, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceAccessPolicySnapshot(Saved = policy, "\"policy\""));
    }
}
