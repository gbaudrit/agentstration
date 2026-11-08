using System.Net;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageTypeComponentTests
{
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Tenant(Guid.Parse("10640000-0000-0000-0000-000000000001"));

    [TestMethod]
    public void CatalogueLinksPreserveExactIdentityAndUseSelectedContext()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        var page = context.Render<StorageTypes>();
        page.WaitForElement("[data-testid=storage-type-name-link]");
        Assert.AreEqual(StorageTypeLinks.Details(client.Resource), page.Find("[data-testid=storage-type-name-link]").GetAttribute("href"));
        Assert.AreEqual(Scope, client.ListScope);
        page.Find("input[type=search]").Input("does-not-exist");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-type-row]"));
    }

    [TestMethod]
    public void CreationComputesDigestsAndCannotForgeAProviderContribution()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        var page = context.Render<StorageTypeEditor>();
        page.WaitForElement("[data-testid=storage-type-display-name]").Change("My storage type");
        page.Find("[data-testid=storage-type-name]").Change("my-storage-type");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        var version = client.Created!.Properties.Versions.Single();
        Assert.AreEqual(StorageDigests.Schema(version.ConfigurationSchema), version.SchemaDigest);
        Assert.AreEqual(StorageDigests.Schema(version.EnvelopeSchema), version.EnvelopeSchemaDigest);
        Assert.IsNull(version.Contribution);
        Assert.IsFalse(version.Capabilities.Write);
        Assert.IsEmpty(client.Created.Properties.UsePolicy.Grants);
        Assert.AreEqual(Scope, client.Created.ScopeRef);
    }

    [TestMethod]
    public void InvalidSchemaRetainsDraftAndNeverCallsApi()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        var page = context.Render<StorageTypeEditor>();
        page.WaitForElement("[data-testid=storage-type-display-name]").Change("Draft retained");
        page.Find("[data-testid=storage-type-configuration-schema]").Change("not-json");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.IsNotNull(page.Find("[role=alert]")));
        Assert.IsNull(client.Created);
        Assert.AreEqual("Draft retained", page.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
    }

    [TestMethod]
    public void MetadataConflictRetainsInputAndExplicitReviewUsesTheLatestEtag()
    {
        var client = new TypesClient { FailWrites = true };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "edit"));
        var page = context.Render<StorageTypeEditor>(parameters => parameters.Add(value => value.Name, client.Resource.Name));
        page.WaitForElement("[data-testid=storage-type-display-name]").Change("My retained change");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-type-configuration-schema]"));
        page.Find("form").Submit();
        page.WaitForElement("[data-testid=storage-type-review]");
        Assert.AreEqual("My retained change", page.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        client.CurrentEtag = "\"latest\"";
        client.Resource = client.Resource with
        {
            Definition = client.Resource.Definition with { Description = "Another administrator's saved description" }
        };
        page.Find("[data-testid=storage-type-review]").Click();
        page.WaitForAssertion(() => Assert.IsEmpty(page.FindAll("[data-testid=storage-type-review]")));
        StringAssert.Contains(page.Find(".storage-type-current").TextContent, "Another administrator's saved description");
        Assert.AreEqual("My retained change", page.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        client.FailWrites = false;
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.AreEqual("\"latest\"", client.LastEtag));
        Assert.AreEqual("My retained change", client.Updated!.Properties.DisplayName);
        Assert.AreEqual(client.Resource.Definition.Versions.Single().ContractDigest, client.Updated.Properties.Versions.Single().ContractDigest);
    }

    [TestMethod]
    public void PublicationHasSeparateVersionFieldsAndRetainsDraftAfterConflict()
    {
        var client = new TypesClient { FailWrites = true };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "versions/new"));
        var page = context.Render<StorageTypeEditor>(parameters => parameters.Add(value => value.Name, client.Resource.Name));
        page.WaitForElement("[data-testid=storage-type-version]").Change("2.0.0");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-type-display-name]"));
        page.Find("form").Submit();
        page.WaitForElement("[data-testid=storage-type-review]");
        Assert.AreEqual("2.0.0", page.Find("[data-testid=storage-type-version]").GetAttribute("value"));
        Assert.AreEqual("2.0.0", client.Published!.Version.Version);
        Assert.IsNull(client.Published.Version.Contribution);
    }

    [TestMethod]
    public void ManagedTypeHidesDefinitionActionsButKeepsOwnerAccessAdministration()
    {
        var client = new TypesClient();
        client.Resource = client.Resource with { Governance = ResourceGovernance.Core("storage.local", "1.0.0") };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var page = context.Render<StorageTypeDetails>(parameters => parameters.Add(value => value.Name, client.Resource.Name));
        page.WaitForElement("[data-testid=storage-type-managed]");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-type-edit], [data-testid=storage-type-publish], [data-testid=storage-type-delete]"));
        Assert.HasCount(1, page.FindAll("[data-testid=storage-type-access-tab]"));
        page.Find("[data-testid=storage-type-definition-tab]").Click();
        Assert.HasCount(1, page.FindAll("[data-testid=storage-type-version-select]"));
        StringAssert.Contains(page.Markup, client.Resource.Definition.Versions.Single().SchemaDigest);
    }

    [TestMethod]
    public void InheritedAndManagedEditorsRemainReadOnly()
    {
        foreach (var managed in new[] { false, true })
        {
            var client = new TypesClient();
            if (managed) client.Resource = client.Resource with { Governance = ResourceGovernance.Core("storage.local") };
            using var context = Setup(client, selectedScope: managed ? Scope.Value : ResourceScopeRef.Workspace(Guid.NewGuid()).Value);
            context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "edit"));
            var page = context.Render<StorageTypeEditor>(parameters => parameters.Add(value => value.Name, client.Resource.Name));
            page.WaitForAssertion(() => Assert.IsNotNull(page.Find("[role=note]")));
            Assert.IsEmpty(page.FindAll("[data-testid=storage-type-save]"));
        }
    }

    private static BunitContext Setup(TypesClient client, string? selectedScope = null)
    {
        var context = new BunitContext();
        context.JSInterop.SetupModule("./_content/Agentstration.Console.Components/Components/Pages/StorageConfirmation.razor.js").SetupVoid("open", _ => true);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStorageTypesClient>(client);
        context.Services.AddSingleton(new ConsoleContextState(new ContextProvider(selectedScope ?? Scope.Value)));
        return context;
    }
    private sealed class ContextProvider(string scope) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(Guid.Empty, "Administrator", scope,
            ConsoleContextKind.Tenant, Scope.TargetId!.Value, "tenant", "Tenant", Guid.Empty, "", "",
            new HashSet<string> { "resources/read", "resources/write", "resources/delete" }, [], []));
    }
    private sealed class TypesClient : IStorageTypesClient
    {
        public StorageTypeResource Resource { get; set; } = Type();
        public bool FailWrites { get; set; }
        public string CurrentEtag { get; set; } = "\"original\"";
        public string? LastEtag { get; private set; }
        public ResourceScopeRef? ListScope { get; private set; }
        public CreateStorageTypeRequest? Created { get; private set; }
        public PutStorageTypeRequest? Updated { get; private set; }
        public PublishStorageTypeVersionRequest? Published { get; private set; }
        public Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token) { ListScope = scope; return Task.FromResult<IReadOnlyList<StorageTypeResource>>([Resource]); }
        public Task<ResourceSnapshot<StorageTypeResource>> GetTypeAsync(ScopedResourceAddress address, CancellationToken token) => Task.FromResult(new ResourceSnapshot<StorageTypeResource>(Resource, CurrentEtag));
        public Task<IReadOnlyList<StorageUsage>> GetTypeUsagesAsync(ScopedResourceAddress address, CancellationToken token) => Task.FromResult<IReadOnlyList<StorageUsage>>([]);
        public Task<ResourceSnapshot<StorageTypeResource>> CreateTypeAsync(CreateStorageTypeRequest body, CancellationToken token) { Created = body; return Result(); }
        public Task<ResourceSnapshot<StorageTypeResource>> UpdateTypeAsync(ScopedResourceAddress address, PutStorageTypeRequest body, string etag, CancellationToken token) { Updated = body; LastEtag = etag; return Result(); }
        public Task<ResourceSnapshot<StorageTypeResource>> PublishTypeVersionAsync(ScopedResourceAddress address, PublishStorageTypeVersionRequest body, string etag, CancellationToken token) { Published = body; LastEtag = etag; return Result(); }
        public Task DeleteTypeAsync(ScopedResourceAddress address, string etag, CancellationToken token) => Task.CompletedTask;
        private Task<ResourceSnapshot<StorageTypeResource>> Result() => FailWrites
            ? Task.FromException<ResourceSnapshot<StorageTypeResource>>(new AgentstrationApiException("PRIVATE", "id", HttpStatusCode.Conflict, "storage_version_conflict"))
            : Task.FromResult(new ResourceSnapshot<StorageTypeResource>(Resource, CurrentEtag));
    }
    private static StorageTypeResource Type()
    {
        var version = new StorageTypeVersionDraft().Build();
        return new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.StorageType,
            Uid = Guid.NewGuid(),
            ScopeRef = Scope,
            Metadata = new() { Name = "custom", Namespace = ResourceNamespace.Parse("catalog") },
            Generation = 1,
            Definition = new() { DisplayName = "Custom type", PreferredVersion = version.Version, Versions = [version] },
            ObservedVersions = [new(version.Version, StorageAvailability.Ready)]
        };
    }
}
