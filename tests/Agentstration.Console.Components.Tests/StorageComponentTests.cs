using System.Net;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
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
public sealed class StorageComponentTests
{
    private static readonly Guid TypeUid = Guid.Parse("10460000-0000-0000-0000-000000000001");
    private static readonly Guid ServiceUid = Guid.Parse("10460000-0000-0000-0000-000000000002");
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("10460000-0000-0000-0000-000000000003"));
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly ConnectedServiceExportDescriptor Endpoint = new("endpoint", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard);
    private static readonly ConnectedServiceExportDescriptor Token = new("token", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured);
    private static readonly ConnectedServiceTypeResource ServiceType = new()
    {
        Uid = Guid.Parse("10460000-0000-0000-0000-000000000004"), ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedServiceType, Metadata = new() { Name = "http" }, ScopeRef = Scope,
        Definition = new() { DisplayName = "HTTP", PreferredVersion = "1", Versions = [new("1", "schema", Empty, [], [Endpoint, Token, new("wrong", ConnectedServiceValueType.Logical, ConnectedServiceValueProtection.Standard)])] }
    };
    private static StorageTypeResource Type(bool remote = true) => new()
    {
        Uid = TypeUid, ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
        Metadata = new() { Name = remote ? "remote" : "local" }, ScopeRef = Scope,
        Governance = remote ? ResourceGovernance.Extension("provider", "storage-provider", "1") : ResourceGovernance.Core("storage.local", "1"),
        ObservedVersions = [new("1", StorageAvailability.Ready)],
        Definition = new()
        {
            DisplayName = remote ? "Remote fixture" : "Local fixture", PreferredVersion = "1", Versions = [new()
            {
                Version = "1", ConfigurationSchema = EmptySchema, SchemaDigest = StorageDigests.Schema(EmptySchema),
                EnvelopeSchema = EmptySchema, EnvelopeSchemaDigest = StorageDigests.Schema(EmptySchema),
                RequiresConnectedService = remote, Requirements = remote ? [new("endpoint", true, Endpoint.Type, Endpoint.Protection), new("credential", true, Token.Type, Token.Protection)] : [],
                Capabilities = new() { Read = remote, List = remote },
                Contribution = remote ? new(new(Scope, ResourceNamespace.Default, "Extension", "storage-provider"), ServiceUid, 1, "storage", "1", "evidence") : null
            }]
        }
    };
    private static JsonElement EmptySchema => JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    private static ConnectedServiceResource Service(long generation = 1) => new()
    {
        Uid = ServiceUid, ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedService,
        Metadata = new() { Name = "remote-service" }, ScopeRef = Scope, Generation = generation,
        Definition = new()
        {
            DisplayName = "Remote service", Type = new() { Type = new(ServiceType.Name, Scope, ServiceType.Namespace), TypeUid = ServiceType.Uid, Version = "1", SchemaDigest = "schema" },
            Options = JsonSerializer.SerializeToElement(new { credentialTrap = "DO-NOT-DISPLAY-CONNECTION-OPTIONS" })
        },
        ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = DateTimeOffset.UnixEpoch }
    };
    private static StorageResource Resource(bool remote = true)
    {
        var draft = new StorageEditorModel { Name = "documents", DisplayName = "Documents" };
        var type = Type(remote); draft.SelectType(new(type, type.Definition.Versions[0]));
        if (remote) { draft.SelectService(Service()); draft.Bind("endpoint", Endpoint); draft.Bind("credential", Token); }
        return new()
        {
            Uid = Guid.Parse("10460000-0000-0000-0000-000000000005"), ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = draft.Name, Namespace = ResourceNamespace.Parse("evidence") }, ScopeRef = Scope, Generation = 4,
            Definition = draft.Properties(), ObservedState = new(StorageAvailability.Ready, DateTimeOffset.UnixEpoch, null, [])
        };
    }

    [TestMethod]
    public void ListShowsExactScopedLinksAndBuiltinAndExtensionEvidence()
    {
        using var context = Setup(new());
        var page = context.Render<Storages>();
        page.WaitForAssertion(() =>
        {
            var href = page.Find("[data-testid=storage-name-link]").GetAttribute("href")!;
            StringAssert.Contains(href, Uri.EscapeDataString(Scope.Value));
            StringAssert.Contains(href, "namespace=evidence");
            Assert.HasCount(2, page.FindAll("[data-testid=storage-type-evidence]"));
            StringAssert.Contains(page.Markup, "No content operations advertised");
            StringAssert.Contains(page.Markup, "storage-provider");
            Assert.DoesNotContain("DO-NOT-DISPLAY", page.Markup, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ReadOnlyContextHidesCreationAndLifecycleActions()
    {
        using var context = Setup(new(), canWrite: false);
        var list = context.Render<Storages>();
        list.WaitForElement("[data-testid=storage-row]");
        Assert.IsEmpty(list.FindAll("[data-testid=storage-create]"));
        Navigate(context, Resource());
        var detail = context.Render<StorageDetails>(parameters => parameters.Add(value => value.Name, "documents"));
        detail.WaitForElement("[data-testid=storage-mapping]");
        Assert.IsEmpty(detail.FindAll("[data-testid=storage-deactivate], [data-testid=storage-reconcile], [data-testid=storage-access-tab]"));
    }

    [TestMethod]
    public void NoReadPermissionDoesNotFetchStorageResources()
    {
        var client = new StorageClient();
        using var context = Setup(client, canRead: false);
        var page = context.Render<Storages>();
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "Access is denied"));
        Assert.AreEqual(0, client.ListCalls);
    }

    [TestMethod]
    public void EditorRequiresExplicitTypeServiceAndEveryMandatoryExport()
    {
        using var context = Setup(new());
        var page = context.Render<StorageEditor>();
        page.WaitForElement("[data-testid=storage-type]");
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        SelectType(page, "remote");
        Assert.AreEqual("", page.Find("[data-testid=storage-service]").GetAttribute("value"));
        SelectService(page);
        var selects = page.FindAll("[data-testid=storage-export]");
        Assert.HasCount(2, selects);
        Assert.IsTrue(selects.All(select => select.GetAttribute("value") == ""));
        Assert.DoesNotContain("wrong", string.Join(' ', selects.Select(select => select.TextContent)), StringComparison.Ordinal);
        Assert.IsFalse(selects[0].TextContent.Contains("token", StringComparison.Ordinal));
        selects[0].Change(selects[0].QuerySelectorAll("option")[1].GetAttribute("value"));
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        var second = page.FindAll("[data-testid=storage-export]")[1];
        second.Change(second.QuerySelectorAll("option")[1].GetAttribute("value"));
        Assert.IsFalse(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        Assert.DoesNotContain("DO-NOT-DISPLAY", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void NewLocalStorageNeedsNoConnectedServiceAndSavesExactType()
    {
        var client = new StorageClient();
        using var context = Setup(client);
        var page = context.Render<StorageEditor>();
        page.WaitForElement("[data-testid=storage-type]");
        SelectType(page, "local");
        Assert.IsEmpty(page.FindAll("[data-testid=storage-service], [data-testid=storage-export]"));
        page.Find("[data-testid=storage-display-name]").Change("Local documents");
        page.Find("[data-testid=storage-name]").Change("local-documents");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        Assert.AreEqual("local", client.Created!.Properties.Type.Type.Name);
        Assert.AreEqual(TypeUid, client.Created!.Properties.Type.TypeUid);
        Assert.AreEqual(Scope, client.Created.ScopeRef);
        Assert.IsNull(client.Created!.Properties.ConnectedService);
        Assert.IsEmpty(client.Created!.Properties.Bindings);
    }

    [TestMethod]
    public void ExistingServiceGenerationIsPreservedUntilExplicitRebindAndConfirmation()
    {
        var client = new StorageClient();
        using var context = Setup(client, serviceGeneration: 2);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "Saved service revision unavailable"));
        Assert.AreEqual(0, client.Reconciles);
        SelectService(page);
        Assert.IsTrue(page.FindAll("[data-testid=storage-export]").All(select => select.GetAttribute("value") == ""));
        for (var index = 0; index < 2; index++)
        {
            var select = page.FindAll("[data-testid=storage-export]")[index];
            select.Change(select.QuerySelectorAll("option")[1].GetAttribute("value"));
        }
        page.Find("form").Submit();
        page.WaitForElement("[data-testid=storage-rebind-confirmation]");
        Assert.AreEqual(0, client.Reconciles);
        page.Find("[data-testid=storage-rebind-confirmation-confirm]").Click();
        page.WaitForAssertion(() => Assert.AreEqual(1, client.Reconciles));
        Assert.AreEqual(2, client.Reconciled!.Properties!.ConnectedService!.Generation);
        Assert.AreEqual("\"revision-4\"", client.LastEtag);
        Assert.AreEqual("evidence", client.LastAddress?.Namespace.Value);
    }

    [TestMethod]
    public void MetadataUpdateUsesPutAndKeepsSavedMappings()
    {
        var client = new StorageClient();
        using var context = Setup(client);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForElement("[data-testid=storage-save]");
        page.Find("[data-testid=storage-display-name]").Change("Updated documents");
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Updated));
        Assert.AreEqual("Updated documents", client.Updated!.Properties.DisplayName);
        Assert.HasCount(2, client.Updated!.Properties.Bindings);
        Assert.AreEqual(0, client.Reconciles);
        Assert.AreEqual("\"revision-4\"", client.LastEtag);
    }

    [TestMethod]
    public void ConflictIsLocalizedAndDoesNotShowBackendProblemDetails()
    {
        var client = new StorageClient { Failure = new("PRIVATE BACKEND DETAILS", "correlation", HttpStatusCode.Conflict, "storage_version_conflict") };
        using var context = Setup(client);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForElement("[data-testid=storage-save]");
        page.Find("form").Submit();
        page.WaitForAssertion(() => StringAssert.Contains(page.Find("[data-testid=storage-error]").TextContent, "revision is stale"));
        Assert.DoesNotContain("PRIVATE BACKEND", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailUsesCanonicalTabsAndYamlContainsReferencesWithoutConnectionValues()
    {
        using var context = Setup(new()); Navigate(context, Resource());
        var page = context.Render<StorageDetails>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForElement("[data-testid=storage-mapping]");
        Assert.HasCount(4, page.FindAll("[role=tab]"));
        StringAssert.Contains(page.Markup, "evidence/documents");
        StringAssert.Contains(page.Markup, ServiceUid.ToString());
        Assert.IsEmpty(page.FindAll("a[href*='/edit']"));
        page.Find("[data-testid=storage-yaml-tab]").Click();
        StringAssert.Contains(page.Find("[data-testid=storage-yaml]").TextContent, "apiVersion:");
        Assert.DoesNotContain("DO-NOT-DISPLAY", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DeactivationRequiresConfirmationAndReportsDependenciesWithoutLosingResource()
    {
        var client = new StorageClient { Failure = new("PRIVATE", "correlation", HttpStatusCode.Conflict, "storage_in_use") };
        using var context = Setup(client); Navigate(context, Resource());
        var page = context.Render<StorageDetails>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForElement("[data-testid=storage-deactivate]").Click();
        Assert.AreEqual(0, client.Deactivations);
        page.Find("[data-testid=storage-deactivate-confirmation-confirm]").Click();
        page.WaitForAssertion(() => StringAssert.Contains(page.Find("[data-testid=storage-error]").TextContent, "Dependencies prevent"));
        Assert.HasCount(2, page.FindAll("[data-testid=storage-mapping]"));
        Assert.AreEqual(1, client.Deactivations);
    }

    [TestMethod]
    public void DriftObservationSendsNoReplacementAndUsesSavedEtag()
    {
        var client = new StorageClient();
        using var context = Setup(client); Navigate(context, Resource());
        var page = context.Render<StorageDetails>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForElement("[data-testid=storage-reconcile]").Click();
        Assert.AreEqual(0, client.Reconciles);
        page.Find("[data-testid=storage-reconcile-confirmation-confirm]").Click();
        page.WaitForAssertion(() => Assert.AreEqual(1, client.Reconciles));
        Assert.IsNull(client.Reconciled!.Properties);
        Assert.AreEqual("\"revision-4\"", client.LastEtag);
    }

    [TestMethod]
    public void MissingTypeKeepsExactSavedIdentityVisibleAndMetadataEditable()
    {
        using var context = Setup(new() { TypesMissing = true }); Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents"));
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "saved exact type is unavailable"));
        StringAssert.Contains(page.Markup, TypeUid.ToString());
        Assert.IsFalse(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void LocalCreationRemainsAvailableWhenConnectedServiceMetadataFails()
    {
        using var context = Setup(new(), servicesUnavailable: true);
        var page = context.Render<StorageEditor>();
        page.WaitForElement("[data-testid=storage-type]");
        SelectType(page, "local");
        Assert.IsFalse(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll("[data-testid=storage-service]"));
    }

    [TestMethod]
    [DataRow("/instance")]
    [DataRow("/tenants/10460000-0000-0000-0000-000000000003")]
    [DataRow("/workspaces/10460000-0000-0000-0000-000000000003")]
    public void ListUsesTheSelectedAdministrativeScope(string selectedScope)
    {
        var client = new StorageClient();
        using var context = Setup(client, selectedScope: selectedScope);
        var page = context.Render<Storages>();
        page.WaitForElement("[data-testid=storage-row]");
        Assert.AreEqual(ResourceScopeRef.Parse(selectedScope), client.ListScope);
    }

    [TestMethod]
    public void ConnectedServiceStorageDependencyLinksPreserveQualifiedIdentity()
    {
        var url = StorageLinks.Details(Resource());
        Assert.AreEqual(url, StorageLinks.FromApi("/api" + url));
    }

    [TestMethod]
    public void ClearingTheTypeSelectionClearsConnectionDraftAndDisablesSave()
    {
        using var context = Setup(new());
        var page = context.Render<StorageEditor>();
        page.WaitForElement("[data-testid=storage-type]");
        SelectType(page, "remote");
        SelectService(page);
        page.Find("[data-testid=storage-type]").Change(string.Empty);
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll("[data-testid=storage-service], [data-testid=storage-export]"));
    }

    private static BunitContext Setup(StorageClient client, bool canWrite = true, bool canRead = true, long serviceGeneration = 1, bool servicesUnavailable = false, string? selectedScope = null)
    {
        var context = new BunitContext();
        context.JSInterop.SetupModule("./_content/Agentstration.Console.Components/Components/Pages/StorageConfirmation.razor.js").SetupVoid("open", _ => true);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStoragesClient>(client);
        context.Services.AddSingleton<IConnectedServicesClient>(new ServiceClient(serviceGeneration, servicesUnavailable));
        context.Services.AddSingleton(new ConsoleContextState(new ContextProvider(canWrite, canRead, selectedScope)));
        return context;
    }
    private static void Navigate(BunitContext context, StorageResource resource, bool edit = false) => context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageLinks.Details(resource).Replace("?", edit ? "/edit?" : "?", StringComparison.Ordinal));
    private static void SelectType(IRenderedComponent<StorageEditor> page, string name)
    {
        var type = Type(name == "remote"); page.Find("[data-testid=storage-type]").Change(new StorageTypeChoice(type, type.Definition.Versions[0]).Key);
    }
    private static void SelectService(IRenderedComponent<StorageEditor> page)
    {
        var select = page.Find("[data-testid=storage-service]");
        select.Change(select.QuerySelectorAll("option").Last().GetAttribute("value"));
    }
    private sealed class ContextProvider(bool canWrite, bool canRead, string? selectedScope) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(Guid.Empty, "Administrator", selectedScope ?? Scope.Value, ConsoleContextKind.Workspace, Guid.Empty, "tenant", "Tenant", Guid.Empty, "workspace", "Workspace", new HashSet<string>(new[] { canRead ? "resources/read" : "", canWrite ? "resources/write" : "" }.Where(value => value.Length > 0)), [], []));
    }
    private sealed class StorageClient : IStoragesClient
    {
        public bool TypesMissing { get; init; }
        public AgentstrationApiException? Failure { get; init; }
        public ResourceScopeRef? ListScope { get; private set; }
        public int ListCalls { get; private set; }
        public int Reconciles { get; private set; }
        public int Deactivations { get; private set; }
        public CreateStorageRequest? Created { get; private set; }
        public PutStorageRequest? Updated { get; private set; }
        public ReconcileStorageRequest? Reconciled { get; private set; }
        public string? LastEtag { get; private set; }
        public ScopedResourceAddress? LastAddress { get; private set; }
        public Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token) => Task.FromResult<IReadOnlyList<StorageTypeResource>>(TypesMissing ? [] : [Type(), Type(false)]);
        public Task<IReadOnlyList<StorageResource>> GetStoragesAsync(ResourceScopeRef scope, CancellationToken token) { ListCalls++; ListScope = scope; return Task.FromResult<IReadOnlyList<StorageResource>>([Resource()]); }
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(Scope, ResourceScopeKind.Workspace, "Workspace", true)]);
        public Task<ResourceSnapshot<StorageResource>> GetAsync(ScopedResourceAddress address, CancellationToken token) => Task.FromResult(new ResourceSnapshot<StorageResource>(Resource(), "\"revision-4\""));
        public Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress address, CancellationToken token) => Task.FromResult<IReadOnlyList<StorageUsage>>([]);
        public Task<ResourceSnapshot<StorageResource>> CreateAsync(CreateStorageRequest body, CancellationToken token) { Created = body; return Result(); }
        public Task<ResourceSnapshot<StorageResource>> UpdateAsync(ScopedResourceAddress address, PutStorageRequest body, string etag, CancellationToken token) { Updated = body; LastEtag = etag; LastAddress = address; return Result(); }
        public Task<ResourceSnapshot<StorageResource>> ReconcileAsync(ScopedResourceAddress address, ReconcileStorageRequest body, string etag, CancellationToken token) { Reconciles++; Reconciled = body; LastEtag = etag; LastAddress = address; return Result(); }
        public Task<ResourceSnapshot<StorageResource>> DeactivateAsync(ScopedResourceAddress address, string etag, CancellationToken token) { Deactivations++; LastEtag = etag; return Result(); }
        private Task<ResourceSnapshot<StorageResource>> Result() => Failure is null ? Task.FromResult(new ResourceSnapshot<StorageResource>(Resource(), "\"revision-5\"")) : Task.FromException<ResourceSnapshot<StorageResource>>(Failure);
    }
    private sealed class ServiceClient(long generation, bool unavailable) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken token) => unavailable ? Task.FromException<IReadOnlyList<ConnectedServiceTypeResource>>(new HttpRequestException()) : Task.FromResult<IReadOnlyList<ConnectedServiceTypeResource>>([ServiceType]);
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ConnectedServiceResource>>([Service(generation)]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scope, CancellationToken token) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scope, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scope, PutConnectedServiceRequest request, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scope, string etag, CancellationToken token) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scope, string etag, CancellationToken token) => throw new NotSupportedException();
    }
}
