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
        var limits = page.FindAll("input[type=number]");
        Assert.IsTrue(limits.All(input => !input.HasAttribute("max")));
        limits[0].Change("1001");
        page.FindAll("input[type=number]")[1].Change("17825792");
        page.FindAll("input[type=number]")[2].Change("17825792");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        var version = client.Created!.Properties.Versions.Single();
        Assert.AreEqual(StorageDigests.Schema(version.ConfigurationSchema), version.SchemaDigest);
        Assert.AreEqual(StorageDigests.Schema(version.EnvelopeSchema), version.EnvelopeSchemaDigest);
        Assert.IsNull(version.Contribution);
        Assert.IsFalse(version.Capabilities.Write);
        Assert.AreEqual(new StorageLimits(1001, 17825792, 17825792), version.Limits);
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

    [TestMethod]
    public void CompactTypeKeepsActualPreferredContractAndManagedLockInCards()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient();
        client.Resource = client.Resource with { Governance = ResourceGovernance.Core("storage.local") };
        using var context = Setup(client);
        var page = context.Render<StorageTypes>();
        page.WaitForElement("[data-testid=storage-type-row]");
        page.Find(".collection-view-switch button:last-child").Click();
        Assert.AreEqual(client.Resource.Definition.PreferredVersion, page.Find(".collection-version code").TextContent);
        Assert.AreEqual("catalog/custom", page.Find(".copyable-value code").GetAttribute("title"));
        StringAssert.Contains(page.Find(".managed-resource-badge").TextContent, "Managed");
        Assert.IsEmpty(page.FindAll(".collection-actions-menu"));
    }

    [TestMethod]
    public void UsageHomonymsExposeOnlyExplicitAuthorizedNamespaceAndOwnerMetadata()
    {
        using var culture = new TestCultureScope("en-US");
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        StorageUsage[] usages = [
            new("Storage", "documents", "Documents", $"/storages/documents?namespace=first&scopeRef={Uri.EscapeDataString(tenant.Value)}"),
            new("Storage", "documents", "Documents", $"/storages/documents?namespace=second&scopeRef={Uri.EscapeDataString(workspace.Value)}")
        ];
        var client = new TypesClient { Usages = usages };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var page = context.Render<StorageTypeDetails>(parameters => parameters.Add(value => value.Name, client.Resource.Name));
        page.WaitForAssertion(() => Assert.HasCount(2, page.FindAll(".resource-links li")));
        var rows = page.FindAll(".resource-links li");
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            Assert.AreEqual(usages[index].Url, row.QuerySelector("a.resource-name-link")!.GetAttribute("href"));
            Assert.AreEqual(index == 0 ? "first/documents" : "second/documents", row.QuerySelector(".copyable-value code")!.GetAttribute("title"));
            Assert.AreEqual(index == 0 ? "T" : "W", row.QuerySelector(".scope-letter")!.TextContent);
            StringAssert.StartsWith(row.QuerySelector(".resource-owner-icon button")!.GetAttribute("aria-label")!, index == 0 ? "Tenant" : "Workspace");
            Assert.IsNull(row.QuerySelector(".managed-resource-badge"));
            Assert.IsNull(row.QuerySelector(".status-icon-only"));
        }
        Assert.AreEqual(1, client.UsageCalls);
    }

    [TestMethod]
    [DataRow("https://elsewhere.test/storages/item?namespace=private&scopeRef=/instance")]
    [DataRow("//elsewhere.test/storages/item?namespace=private&scopeRef=/instance")]
    [DataRow("/storages/item")]
    [DataRow("/storages/item?namespace=&scopeRef=unknown")]
    [DataRow("/storages/item?namespace=first&namespace=second&scopeRef=/instance&scopeRef=/instance")]
    [DataRow("/storages/item?namespace=invalid/slash&scopeRef=/workspaces/invalid")]
    public void UsageIdentityNeverInventsMetadataForMissingOrUnusableQueries(string url)
    {
        var identity = StorageLinks.UsageIdentity(new("Storage", "item", "Item", url));
        Assert.IsNull(identity.Namespace);
        Assert.IsNull(identity.ScopeRef);
    }

    private static BunitContext Setup(TypesClient client, string? selectedScope = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
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
        public IReadOnlyList<StorageUsage> Usages { get; init; } = [];
        public int UsageCalls { get; private set; }

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
        public Task<IReadOnlyList<StorageUsage>> GetTypeUsagesAsync(ScopedResourceAddress address, CancellationToken token) { UsageCalls++; return Task.FromResult(Usages); }
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
