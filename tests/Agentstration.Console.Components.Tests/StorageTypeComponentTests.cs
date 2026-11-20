using System.Net;
using System.Text.Json;
using Agentstration.Api.Contracts;
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
public sealed partial class StorageTypeComponentTests
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
        Assert.IsEmpty(page.FindAll("input[type=search], [data-filter-key]"));
        Assert.HasCount(1, page.FindAll("[data-testid=storage-type-row]"));
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
        page.WaitForAssertion(() => Assert.IsNotNull(page.Find("[data-testid=storage-type-configuration-schema]").Closest(".form-field")!.QuerySelector(".validation-message")));
        Assert.AreEqual("step", page.Find("[data-wizard-step=contract]").GetAttribute("aria-current"));
        Assert.IsNull(client.Created);
        Assert.AreEqual("Draft retained", page.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
    }

    [TestMethod]
    public void MetadataConflictRetainsInputAndExplicitReviewUsesTheLatestEtag()
    {
        var client = new TypesClient { FailWrites = true };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "edit"));
        var page = RenderEditor(context, client.Resource.Name);
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
        var page = RenderEditor(context, client.Resource.Name);
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
        var page = RenderDetails(context, client.Resource.Name);
        page.WaitForElement(".resource-readonly-notice");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-type-edit], [data-testid=storage-type-publish], [data-testid=storage-type-delete]"));
        Assert.HasCount(1, page.FindAll(".resource-navigation-rail [data-resource-section=access]"));
        SelectSection(context, "versions");
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
            var page = RenderEditor(context, client.Resource.Name);
            page.WaitForAssertion(() => Assert.IsTrue(page.Find("[data-testid=storage-type-definition-fields]").HasAttribute("disabled")));
            page.Find("form").Submit();
            Assert.IsNull(client.Updated);
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
        var page = RenderDetails(context, client.Resource.Name);
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

    [TestMethod]
    public void TypeCreationWizardAllowsIncompleteNavigationAndRetainsTheContractForm()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient();
        using var context = Setup(client);
        var page = context.Render<StorageTypeEditor>();
        var form = page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        page.Find("[data-testid=creation-wizard-next]").Click();
        Assert.AreEqual("step", page.Find("[data-wizard-step=contract]").GetAttribute("aria-current"));
        Assert.AreEqual("false", page.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        page.Find("[data-testid=storage-type-configuration-schema]").Change("not-json");
        page.Find("[data-wizard-step=identity]").Click();
        page.Find("[data-testid=storage-type-display-name]").Change("Wizard storage type");
        page.Find("[data-testid=storage-type-name]").Change("wizard-type");
        page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=storage-type-save]").Click();
        Assert.AreEqual("step", page.Find("[data-wizard-step=contract]").GetAttribute("aria-current"));
        Assert.IsNull(client.Created);
        Assert.AreEqual("not-json", page.Find("[data-testid=storage-type-configuration-schema]").GetAttribute("value"));
        Assert.AreSame(form, page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        page.Find("[data-testid=storage-type-configuration-schema]").Change("{\"type\":\"object\"}");
        page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=storage-type-save]").Click();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        Assert.IsNotNull(client.Created);
        Assert.AreEqual("wizard-type", client.Created.Name);
        Assert.AreEqual("1.0.0", client.Created.Properties.PreferredVersion);
        Assert.IsNull(client.Created.Properties.Versions.Single().Contribution);
    }

    [TestMethod]
    public void NewVersionWizardRequiresAnExplicitVersionAndReturnsToThatImmutableContract()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient();
        var previous = client.Resource.Definition.Versions.Single();
        using var context = Setup(client);
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(StorageTypeLinks.Action(client.Resource, "versions/new"));
        var page = RenderEditor(context, client.Resource.Name);
        Assert.AreEqual("", page.Find("[data-testid=storage-type-version]").GetAttribute("value"));
        page.Find("[data-wizard-step=review]").Click(); page.Find("[data-testid=storage-type-save]").Click();
        Assert.IsNull(client.Published);
        Assert.AreEqual("step", page.Find("[data-wizard-step=contract]").GetAttribute("aria-current"));
        page.Find("[data-testid=storage-type-version]").Change("2.0.0");
        page.Find("[data-wizard-step=review]").Click(); page.Find("[data-testid=storage-type-save]").Click();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Published));
        Assert.AreEqual("#versions", new Uri(nav.Uri).Fragment);
        StringAssert.Contains(new Uri(nav.Uri).Query, "version=2.0.0");
        Assert.AreEqual(previous, client.Resource.Definition.Versions[0]);
        Assert.AreEqual("2.0.0", client.Resource.Definition.Versions[1].Version);
        Assert.AreEqual(previous.Version, client.Resource.Definition.PreferredVersion);
    }

    [TestMethod]
    public void TypeHeaderActivationKeepsMetadataDraftsAndImmutableVersionsWithTheReturnedEtag()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new TypesClient(); var versions = client.Resource.Definition.Versions;
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var page = RenderDetails(context, client.Resource.Name);
        SelectSection(context, "definition/identity");
        var editor = page.FindComponent<StorageTypeEditor>().Instance;
        page.Find("[data-testid=storage-type-display-name]").Change("Pending type title");
        SelectSection(context, "overview"); page.Find("[data-testid=storage-type-enabled]").Change(false);
        page.Find("[data-testid=storage-type-disable-confirmation-confirm]").Click();
        page.WaitForAssertion(() => Assert.IsFalse(client.Resource.Definition.Enabled));
        Assert.AreEqual("Custom type", client.Resource.Definition.DisplayName);
        CollectionAssert.AreEqual(versions.ToArray(), client.Resource.Definition.Versions.ToArray());
        var activationEtag = client.CurrentEtag;
        SelectSection(context, "definition/identity");
        Assert.AreSame(editor, page.FindComponent<StorageTypeEditor>().Instance);
        Assert.AreEqual("Pending type title", page.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.AreEqual("Pending type title", client.Resource.Definition.DisplayName));
        Assert.AreEqual(activationEtag, client.LastEtag);
        Assert.IsFalse(client.Resource.Definition.Enabled);
        CollectionAssert.AreEqual(versions.ToArray(), client.Resource.Definition.Versions.ToArray());
    }

    [TestMethod]
    public void ImmutableVersionChoiceSurvivesSectionChangesWithoutAnotherResourceRead()
    {
        var client = new TypesClient(); var initial = client.Resource.Definition.Versions.Single();
        client.Resource = client.Resource with { Definition = client.Resource.Definition with { Versions = [initial, initial with { Version = "2.0.0" }] } };
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Details(client.Resource));
        var page = RenderDetails(context, client.Resource.Name);
        SelectSection(context, "versions"); page.Find("[data-testid=storage-type-version-select]").Change("2.0.0");
        SelectSection(context, "overview"); SelectSection(context, "versions");
        Assert.AreEqual("2.0.0", page.Find("[data-testid=storage-type-version-select]").GetAttribute("value"));
        Assert.AreEqual(1, client.ResourceReads);
        Assert.AreEqual(initial.Version, client.Resource.Definition.PreferredVersion);
    }

    private static IRenderedComponent<StorageTypeEditor> RenderEditor(BunitContext context, string name)
    {
        var publication = new Uri(context.Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath.EndsWith("/versions/new", StringComparison.Ordinal);
        return context.Render<StorageTypeEditor>(p => p.Add(value => value.Name, name).Add(value => value.Embedded, !publication));
    }
    private static IRenderedComponent<StorageTypeDetails> RenderDetails(BunitContext context, string name)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(nav.Uri.Split('#')[0] + "#overview");
        return context.Render<StorageTypeDetails>(p => p.Add(value => value.Name, name));
    }
    private static void SelectSection(BunitContext context, string path)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#" + path);
    }

    private static BunitContext Setup(TypesClient client, string? selectedScope = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-form.js").Setup<bool>("focusFirstInvalid", _ => true).SetResult(true);
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
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
        public int ResourceReads { get; private set; }
        public Exception? ReadError { get; set; }
        public Exception? UsageError { get; set; }
        public Task<ResourceSnapshot<StorageTypeResource>>? PendingRead { get; set; }
        private int revision;

        public StorageTypeResource Resource { get; set; } = Type();
        public bool FailWrites { get; set; }
        public string CurrentEtag { get; set; } = "\"original\"";
        public string? LastEtag { get; private set; }
        public ResourceScopeRef? ListScope { get; private set; }
        public CreateStorageTypeRequest? Created { get; private set; }
        public PutStorageTypeRequest? Updated { get; private set; }
        public PublishStorageTypeVersionRequest? Published { get; private set; }
        public Task<PagedResponse<StorageTypeResource>> GetTypesPageAsync(ResourceScopeRef scope, int skip, int take, CancellationToken token) { ListScope = scope; return Task.FromResult(new PagedResponse<StorageTypeResource>([Resource], null)); }
        public Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token) { ListScope = scope; return Task.FromResult<IReadOnlyList<StorageTypeResource>>([Resource]); }
        public Task<ResourceSnapshot<StorageTypeResource>> GetTypeAsync(ScopedResourceAddress address, CancellationToken token) { ResourceReads++; return PendingRead ?? (ReadError is { } error ? Task.FromException<ResourceSnapshot<StorageTypeResource>>(error) : Task.FromResult(new ResourceSnapshot<StorageTypeResource>(Resource, CurrentEtag))); }
        public Task<IReadOnlyList<StorageUsage>> GetTypeUsagesAsync(ScopedResourceAddress address, CancellationToken token) { UsageCalls++; return UsageError is { } error ? Task.FromException<IReadOnlyList<StorageUsage>>(error) : Task.FromResult(Usages); }
        public Task<ResourceSnapshot<StorageTypeResource>> CreateTypeAsync(CreateStorageTypeRequest body, CancellationToken token) { Created = body; Resource = Resource with { Uid = Guid.NewGuid(), Metadata = new() { Name = body.Name, Namespace = body.Namespace ?? ResourceNamespace.Default }, ScopeRef = body.ScopeRef, Definition = body.Properties }; return Result(); }
        public Task<ResourceSnapshot<StorageTypeResource>> UpdateTypeAsync(ScopedResourceAddress address, PutStorageTypeRequest body, string etag, CancellationToken token) { Updated = body; LastEtag = etag; return Result(body.Properties); }
        public Task<ResourceSnapshot<StorageTypeResource>> PublishTypeVersionAsync(ScopedResourceAddress address, PublishStorageTypeVersionRequest body, string etag, CancellationToken token) { Published = body; LastEtag = etag; return Result(Resource.Definition with { Versions = Resource.Definition.Versions.Append(body.Version).ToArray(), PreferredVersion = body.SetPreferred ? body.Version.Version : Resource.Definition.PreferredVersion }); }
        public Task DeleteTypeAsync(ScopedResourceAddress address, string etag, CancellationToken token) => Task.CompletedTask;
        private Task<ResourceSnapshot<StorageTypeResource>> Result(StorageTypeProperties? properties = null)
        {
            if (FailWrites) return Task.FromException<ResourceSnapshot<StorageTypeResource>>(new AgentstrationApiException("PRIVATE", "id", HttpStatusCode.Conflict, "storage_version_conflict"));
            if (properties is not null) Resource = Resource with { Definition = properties };
            CurrentEtag = $"\"saved-{++revision}\"";
            return Task.FromResult(new ResourceSnapshot<StorageTypeResource>(Resource, CurrentEtag));
        }
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
