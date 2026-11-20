using System.Net;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed partial class StorageComponentTests
{
    private static readonly Guid TypeUid = Guid.Parse("10460000-0000-0000-0000-000000000001");
    private static readonly Guid ServiceUid = Guid.Parse("10460000-0000-0000-0000-000000000002");
    private static readonly ResourceScopeRef Scope = ResourceScopeRef.Workspace(Guid.Parse("10460000-0000-0000-0000-000000000003"));
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    private static readonly ConnectedServiceExportDescriptor Endpoint = new("endpoint", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard);
    private static readonly ConnectedServiceExportDescriptor Token = new("token", ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Secured);
    private static readonly ConnectedServiceTypeResource ServiceType = new()
    {
        Uid = Guid.Parse("10460000-0000-0000-0000-000000000004"),
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
        Metadata = new() { Name = "http" },
        ScopeRef = Scope,
        Definition = new() { DisplayName = "HTTP", PreferredVersion = "1", Versions = [new("1", "schema", Empty, [], [Endpoint, Token, new("wrong", ConnectedServiceValueType.Logical, ConnectedServiceValueProtection.Standard)])] }
    };
    private static StorageTypeResource Type(bool remote = true) => new()
    {
        Uid = TypeUid,
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = StorageResourceKinds.StorageType,
        Metadata = new() { Name = remote ? "remote" : "local" },
        ScopeRef = Scope,
        Governance = remote ? ResourceGovernance.Extension("provider", "storage-provider", "1") : ResourceGovernance.Core("storage.local", "1"),
        ObservedVersions = [new("1", StorageAvailability.Ready)],
        ObservedUse = new(Scope, new(true, ResourceAccessDecisionReasons.SameScope)),
        Definition = new()
        {
            DisplayName = remote ? "Remote fixture" : "Local fixture",
            PreferredVersion = "1",
            Versions = [new()
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
        Uid = ServiceUid,
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedService,
        Metadata = new() { Name = "remote-service" },
        ScopeRef = Scope,
        Generation = generation,
        Definition = new()
        {
            DisplayName = "Remote service",
            Type = new() { Type = new(ServiceType.Name, Scope, ServiceType.Namespace), TypeUid = ServiceType.Uid, Version = "1", SchemaDigest = "schema" },
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
            Uid = Guid.Parse("10460000-0000-0000-0000-000000000005"),
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = draft.Name, Namespace = ResourceNamespace.Parse("evidence") },
            ScopeRef = Scope,
            Generation = 4,
            Definition = draft.Properties(),
            ObservedState = new(StorageAvailability.Ready, DateTimeOffset.UnixEpoch, null, [])
        };
    }

    [TestMethod]
    public void ListShowsExactScopedLinksAndBuiltinAndExtensionEvidence()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new());
        var page = context.Render<Storages>();
        page.WaitForAssertion(() =>
        {
            var href = page.Find("[data-testid=storage-name-link]").GetAttribute("href")!;
            StringAssert.Contains(href, Uri.EscapeDataString(Scope.Value));
            StringAssert.Contains(href, "namespace=evidence");
            Assert.HasCount(2, page.FindAll("[data-testid=storage-type-evidence]"));
            var typeIdentity = page.Find("[data-testid=storage-type-evidence] summary");
            Assert.AreEqual(StorageTypeLinks.Details(Type()), typeIdentity.QuerySelector(".resource-name-link")!.GetAttribute("href"));
            Assert.AreEqual("default/remote", typeIdentity.QuerySelector(".copyable-value code")!.GetAttribute("title"));
            Assert.IsNotNull(typeIdentity.QuerySelector(".resource-owner-icon"));
            StringAssert.Contains(page.Markup, "No content operations advertised");
            StringAssert.Contains(page.Markup, "storage-provider");
            Assert.DoesNotContain("DO-NOT-DISPLAY", page.Markup, StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ReadOnlyContextHidesCreationAndLifecycleActions()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new(), canWrite: false);
        var list = context.Render<Storages>();
        list.WaitForElement("[data-testid=storage-row]");
        Assert.IsEmpty(list.FindAll("[data-testid=storage-create]"));
        Navigate(context, Resource());
        var detail = RenderDetails(context);
        detail.WaitForElement("[data-testid=storage-mapping]");
        Assert.IsEmpty(detail.FindAll("[data-testid=storage-reconcile], [data-resource-section=access]"));
    }

    [TestMethod]
    public void NoReadPermissionDoesNotFetchStorageResources()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client, canRead: false);
        var page = context.Render<Storages>();
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "Access denied"));
        Assert.AreEqual(0, client.ListCalls);
    }

    [TestMethod]
    public void EditorRequiresExplicitTypeServiceAndEveryMandatoryExport()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new());
        var page = RenderNewEditor(context);
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
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client);
        var page = RenderNewEditor(context);
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
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client, serviceGeneration: 2);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents").Add(value => value.Embedded, true));
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
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents").Add(value => value.Embedded, true));
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
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient { Failure = new("PRIVATE BACKEND DETAILS", "correlation", HttpStatusCode.Conflict, "storage_version_conflict") };
        using var context = Setup(client);
        Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents").Add(value => value.Embedded, true));
        page.WaitForElement("[data-testid=storage-save]");
        page.Find("form").Submit();
        page.WaitForAssertion(() => StringAssert.Contains(page.Find("[data-testid=storage-error]").TextContent, "revision is stale"));
        Assert.DoesNotContain("PRIVATE BACKEND", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DetailUsesResourceNavigationAndYamlContainsReferencesWithoutConnectionValues()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new()); Navigate(context, Resource());
        var page = RenderDetails(context);
        page.WaitForElement("[data-testid=storage-mapping]");
        Assert.HasCount(4, page.FindAll(".resource-navigation-rail [data-resource-section]"));
        StringAssert.Contains(page.Markup, "evidence/documents");
        StringAssert.Contains(page.Markup, ServiceUid.ToString());
        Assert.IsEmpty(page.FindAll("a[href*='/edit']"));
        SelectSection(context, "yaml");
        StringAssert.Contains(page.Find("[data-testid=storage-yaml]").TextContent, "apiVersion:");
        Assert.DoesNotContain("DO-NOT-DISPLAY", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    public void DeactivationRequiresConfirmationAndReportsDependenciesWithoutLosingResource()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient { Failure = new("PRIVATE", "correlation", HttpStatusCode.Conflict, "storage_in_use") };
        using var context = Setup(client); Navigate(context, Resource());
        var page = RenderDetails(context);
        page.WaitForElement("[data-testid=storage-enabled]").Change(false);
        Assert.AreEqual(0, client.Deactivations);
        page.Find("[data-testid=storage-deactivate-confirmation-confirm]").Click();
        page.WaitForAssertion(() => StringAssert.Contains(page.Find("[data-testid=storage-error]").TextContent, "Dependencies prevent"));
        Assert.HasCount(2, page.FindAll("[data-testid=storage-mapping]"));
        Assert.AreEqual(1, client.Deactivations);
    }

    [TestMethod]
    public void DriftObservationSendsNoReplacementAndUsesSavedEtag()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client); Navigate(context, Resource());
        var page = RenderDetails(context);
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
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new() { TypesMissing = true }); Navigate(context, Resource(), edit: true);
        var page = context.Render<StorageEditor>(parameters => parameters.Add(value => value.Name, "documents").Add(value => value.Embedded, true));
        page.WaitForAssertion(() => StringAssert.Contains(page.Markup, "saved exact type is unavailable"));
        StringAssert.Contains(page.Markup, TypeUid.ToString());
        Assert.IsFalse(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void LocalCreationRemainsAvailableWhenConnectedServiceMetadataFails()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new(), servicesUnavailable: true);
        var page = RenderNewEditor(context);
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
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client, selectedScope: selectedScope);
        var page = context.Render<Storages>();
        page.WaitForElement("[data-testid=storage-row]");
        Assert.AreEqual(ResourceScopeRef.Parse(selectedScope), client.ListScope);
    }

    [TestMethod]
    public void ConnectedServiceStorageDependencyLinksPreserveQualifiedIdentity()
    {
        using var culture = new TestCultureScope("en-US");
        var url = StorageLinks.Details(Resource());
        Assert.AreEqual(url, StorageLinks.FromApi("/api" + url));
    }

    [TestMethod]
    public void ClearingTheTypeSelectionClearsConnectionDraftAndDisablesSave()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new());
        var page = RenderNewEditor(context);
        page.WaitForElement("[data-testid=storage-type]");
        SelectType(page, "remote");
        SelectService(page);
        page.Find("[data-testid=storage-type]").Change(string.Empty);
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        Assert.IsEmpty(page.FindAll("[data-testid=storage-service], [data-testid=storage-export]"));
    }

    [TestMethod]
    public void DiscoverableTypeWithoutGenericUseRemainsVisibleButCannotBeSelected()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new() { TypeUseDenied = true });
        var page = RenderNewEditor(context);
        page.WaitForElement("[data-testid=storage-type]");
        var choices = page.FindAll("[data-testid=storage-type] option").Where(value => !string.IsNullOrEmpty(value.GetAttribute("value"))).ToArray();
        Assert.IsTrue(choices.Length > 0);
        Assert.IsTrue(choices.All(value => value.HasAttribute("disabled")));
        SelectType(page, "remote");
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
        StringAssert.Contains(page.Markup, "Generic Use not granted");
    }

    [TestMethod]
    [DataRow(false, StorageAvailability.Ready, false)]
    [DataRow(true, StorageAvailability.Unavailable, false)]
    [DataRow(true, StorageAvailability.Ready, true)]
    public void GenericUseNeverOverridesActivationAvailabilityOrMissingObservation(bool enabled, StorageAvailability availability, bool missingObservation)
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new() { TypeEnabled = enabled, TypeAvailability = availability, MissingUseObservation = missingObservation });
        var page = RenderNewEditor(context);
        page.WaitForElement("[data-testid=storage-type]");
        var choices = page.FindAll("[data-testid=storage-type] option").Where(value => !string.IsNullOrEmpty(value.GetAttribute("value"))).ToArray();
        Assert.IsNotEmpty(choices);
        Assert.IsTrue(choices.All(value => value.HasAttribute("disabled")));
        SelectType(page, "remote");
        Assert.IsTrue(page.Find("[data-testid=storage-save]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void BackendUseRefusalShowsBoundedGuidanceWithoutPrivateDetails()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient { Failure = new("Secret PRIVATE-PATH hidden-resource", "correlation", HttpStatusCode.Forbidden, "storage_access_denied") };
        using var context = Setup(client);
        var page = RenderNewEditor(context);
        page.WaitForElement("[data-testid=storage-type]");
        SelectType(page, "local");
        page.Find("[data-testid=storage-display-name]").Change("Denied local");
        page.Find("[data-testid=storage-name]").Change("denied-local");
        page.Find("form").Submit();
        page.WaitForAssertion(() => StringAssert.Contains(page.Find("[data-testid=storage-error]").TextContent, "resource use permissions"));
        Assert.DoesNotContain("PRIVATE-PATH", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-resource", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret PRIVATE", page.Markup, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CompactIdentityCopiesExactNameAndLocksOnlyOutsideOwner(bool inherited)
    {
        using var culture = new TestCultureScope("en-US");
        using var context = Setup(new(), selectedScope: inherited ? ResourceScopeRef.Instance.Value : Scope.Value);
        var page = context.Render<Storages>();
        page.WaitForElement("[data-testid=storage-row]");
        var row = page.Find("[data-testid=storage-row]");
        Assert.AreEqual("evidence/documents", row.QuerySelector(".copyable-value code")!.GetAttribute("title"));
        Assert.AreEqual("W", row.QuerySelector(".resource-owner-icon .disclosure-trigger")!.TextContent.Trim());
        Assert.AreEqual(inherited, row.QuerySelector(".managed-resource-badge") is not null);
        Assert.IsNotNull(row.QuerySelector(".collection-family-icon .resource-icon"));
        page.Find(".collection-view-switch button:last-child").Click();
        Assert.AreEqual(StorageLinks.Details(Resource()), page.Find("li[data-testid=storage-row] [data-testid=storage-name-link]").GetAttribute("href"));
        Assert.AreEqual("evidence/documents", page.Find("li[data-testid=storage-row] .copyable-value code").TextContent);
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
        var client = new StorageClient { Usages = usages };
        using var context = Setup(client);
        Navigate(context, Resource());
        var page = RenderDetails(context);
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
    public void WizardAllowsIncompleteNextAndRetainsTheExactTypeUntilFinalCreation()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client);
        var page = context.Render<StorageEditor>();
        var form = page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        Assert.AreEqual("false", page.Find("[data-wizard-step=identity]").GetAttribute("data-step-complete"));
        page.Find("[data-testid=creation-wizard-next]").Click();
        Assert.AreEqual("step", page.Find("[data-wizard-step=configuration]").GetAttribute("aria-current"));
        SelectType(page, "local");
        page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=storage-save]").Click();
        Assert.AreEqual("step", page.Find("[data-wizard-step=identity]").GetAttribute("aria-current"));
        Assert.HasCount(2, page.FindAll(".validation-message"));
        Assert.IsNull(client.Created);
        page.Find("[data-testid=storage-display-name]").Change("Wizard documents");
        page.Find("[data-testid=storage-name]").Change("wizard-documents");
        page.Find("[data-wizard-step=configuration]").Click();
        Assert.AreSame(form, page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.IsNull(client.Created);
        page.Find("[data-wizard-step=review]").Click();
        page.Find("[data-testid=storage-save]").Click();
        page.WaitForAssertion(() => Assert.IsNotNull(client.Created));
        Assert.IsNotNull(client.Created);
        Assert.AreEqual(TypeUid, client.Created.Properties.Type.TypeUid);
        Assert.AreEqual("1", client.Created.Properties.Type.Version);
        Assert.IsEmpty(client.Created.Properties.Bindings);
    }

    [TestMethod]
    public void StandaloneEditUsesCanonicalConfigurationWithTheExactSavedAddress()
    {
        using var context = Setup(new()); Navigate(context, Resource(), edit: true);
        var nav = context.Services.GetRequiredService<NavigationManager>();
        context.Render<StorageEditor>(p => p.Add(value => value.Name, "documents"));
        var uri = new Uri(nav.Uri);
        Assert.AreEqual("/storages/documents", uri.AbsolutePath);
        Assert.AreEqual("#definition/configuration", uri.Fragment);
        StringAssert.Contains(uri.Query, "namespace=evidence");
        StringAssert.Contains(uri.Query, "scopeRef=" + Uri.EscapeDataString(Scope.Value));
    }

    [TestMethod]
    public void HeaderActivationRetainsTheDefinitionDraftAndUsesTheReturnedEtagForItsSave()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client); Navigate(context, Resource());
        var page = RenderDetails(context);
        SelectSection(context, "definition/configuration");
        var editor = page.FindComponent<StorageEditor>().Instance;
        var form = page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance;
        page.Find("[data-testid=storage-display-name]").Change("Pending storage title");
        SelectSection(context, "overview");
        page.Find("[data-testid=storage-enabled]").Change(false);
        page.Find("[data-testid=storage-deactivate-confirmation-confirm]").Click();
        page.WaitForAssertion(() => Assert.IsFalse(client.Current.Definition.Enabled));
        Assert.AreEqual("\"revision-4\"", client.LastEtag);
        Assert.AreEqual(Resource().Definition.DisplayName, client.Current.Definition.DisplayName);
        SelectSection(context, "definition/configuration");
        Assert.AreSame(editor, page.FindComponent<StorageEditor>().Instance);
        Assert.AreSame(form, page.FindComponent<Microsoft.AspNetCore.Components.Forms.EditForm>().Instance);
        Assert.AreEqual("Pending storage title", page.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        page.Find("form").Submit();
        page.WaitForAssertion(() => Assert.AreEqual("Pending storage title", client.Current.Definition.DisplayName));
        Assert.AreEqual("\"revision-5\"", client.LastEtag);
        Assert.HasCount(2, client.Current.Definition.Bindings);
        Assert.IsFalse(client.Current.Definition.Enabled);
    }

    [TestMethod]
    public async Task RefreshRequiresExplicitDiscardBeforeReplacingADefinitionDraft()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient();
        using var context = Setup(client); Navigate(context, Resource());
        var page = RenderDetails(context);
        SelectSection(context, "definition/configuration");
        var editor = page.FindComponent<StorageEditor>().Instance;
        page.Find("[data-testid=storage-display-name]").Change("Preserve pending title");
        page.FindAll(".resource-header button").Single(button => button.TextContent.Trim() == "Refresh").Click();
        Assert.AreEqual(1, client.ResourceReads);
        await page.InvokeAsync(() => page.FindComponents<ConfirmationDialog>().Single(dialog => dialog.Instance.IsOpen).Instance.OnCancel.InvokeAsync());
        Assert.AreEqual("Preserve pending title", page.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        page.FindAll(".resource-header button").Single(button => button.TextContent.Trim() == "Refresh").Click();
        await page.InvokeAsync(() => page.FindComponents<ConfirmationDialog>().Single(dialog => dialog.Instance.IsOpen).Instance.OnConfirm.InvokeAsync());
        page.WaitForAssertion(() => Assert.AreNotSame(editor, page.FindComponent<StorageEditor>().Instance));
        Assert.AreEqual(Resource().Definition.DisplayName, page.Find("[data-testid=storage-display-name]").GetAttribute("value"));
        Assert.AreEqual(2, client.ResourceReads);
    }

    [TestMethod]
    public void FailedInitialReadKeepsTheRefreshCommandAndRecoversWithoutChangingTheAddress()
    {
        using var culture = new TestCultureScope("en-US");
        var client = new StorageClient { FailReads = true };
        using var context = Setup(client); Navigate(context, Resource());
        var page = RenderDetails(context);
        page.WaitForElement("[data-testid=storage-error]");
        Assert.HasCount(0, page.FindAll("[data-testid=storage-details]"));
        client.FailReads = false;
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Refresh").Click();
        page.WaitForElement("[data-testid=storage-details]");
        Assert.AreEqual(2, client.ResourceReads);
        StringAssert.Contains(page.Find(".resource-identity-summary").TextContent, Resource().Name);
    }

    private static BunitContext Setup(StorageClient client, bool canWrite = true, bool canRead = true, long serviceGeneration = 1, bool servicesUnavailable = false, string? selectedScope = null)
    {
        var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true).SetVoidResult();
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-form.js").Setup<bool>("focusFirstInvalid", _ => true).SetResult(true);
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IStoragesClient>(client);
        context.Services.AddSingleton<IConnectedServicesClient>(new ServiceClient(serviceGeneration, servicesUnavailable));
        context.Services.AddSingleton(new ConsoleContextState(new ContextProvider(canWrite, canRead, selectedScope)));
        return context;
    }
    private static IRenderedComponent<StorageEditor> RenderNewEditor(BunitContext context)
    {
        var page = context.Render<StorageEditor>();
        page.WaitForElement("[data-wizard-step=review]").Click();
        return page;
    }
    private static IRenderedComponent<StorageDetails> RenderDetails(BunitContext context)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        module.Mode = JSRuntimeMode.Loose;
        module.Setup<string>("currentLocation", _ => true).SetResult(nav.Uri.Split('#')[0] + "#overview");
        return context.Render<StorageDetails>(p => p.Add(value => value.Name, "documents"));
    }
    private static void SelectSection(BunitContext context, string path)
    {
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.Uri.Split('#')[0] + "#" + path);
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
        public StorageResource Current { get; private set; } = Resource();
        private int revision = 4;
        public IReadOnlyList<StorageUsage> Usages { get; init; } = [];
        public int UsageCalls { get; private set; }

        public bool TypeUseDenied { get; init; }
        public bool TypeEnabled { get; init; } = true;
        public StorageAvailability TypeAvailability { get; init; } = StorageAvailability.Ready;
        public bool MissingUseObservation { get; init; }
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
        public Task<IReadOnlyList<StorageTypeResource>> GetTypesAsync(ResourceScopeRef scope, CancellationToken token) => Task.FromResult<IReadOnlyList<StorageTypeResource>>(TypesMissing ? [] : new[] { Type(), Type(false) }.Select(type => type with
        {
            Definition = type.Definition with { Enabled = TypeEnabled },
            ObservedVersions = type.Definition.Versions.Select(version => new StorageTypeVersionAvailability(version.Version, TypeAvailability)).ToArray(),
            ObservedUse = MissingUseObservation ? null : new(scope, new(!TypeUseDenied, TypeUseDenied ? ResourceAccessDecisionReasons.GrantMissing : ResourceAccessDecisionReasons.SameScope))
        }).ToArray());
        public Task<PagedResponse<StorageResource>> GetStoragesPageAsync(ResourceScopeRef scope, int skip, int take, CancellationToken token) { ListCalls++; ListScope = scope; return Task.FromResult(new PagedResponse<StorageResource>([Resource()], null)); }
        public Task<IReadOnlyList<StorageResource>> GetStoragesAsync(ResourceScopeRef scope, CancellationToken token) { ListCalls++; ListScope = scope; return Task.FromResult<IReadOnlyList<StorageResource>>([Resource()]); }
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<ResourceScopeTargetResponse>>([new(Scope, ResourceScopeKind.Workspace, "Workspace", true)]);
        public bool FailReads { get; set; }
        public int ResourceReads { get; private set; }
        public Task<ResourceSnapshot<StorageResource>> GetAsync(ScopedResourceAddress address, CancellationToken token)
        {
            ResourceReads++;
            if (FailReads) return Task.FromException<ResourceSnapshot<StorageResource>>(new HttpRequestException("Offline fixture"));
            return Task.FromResult(new ResourceSnapshot<StorageResource>(Current, $"\"revision-{revision}\""));
        }
        public Task<IReadOnlyList<StorageUsage>> GetUsagesAsync(ScopedResourceAddress address, CancellationToken token) { UsageCalls++; return Task.FromResult(Usages); }
        public Task<ResourceSnapshot<StorageResource>> CreateAsync(CreateStorageRequest body, CancellationToken token) { Created = body; Current = Current with { Uid = Guid.NewGuid(), Metadata = new() { Name = body.Name, Namespace = body.Namespace ?? ResourceNamespace.Default }, ScopeRef = body.ScopeRef, Definition = body.Properties }; return Result(); }
        public Task<ResourceSnapshot<StorageResource>> UpdateAsync(ScopedResourceAddress address, PutStorageRequest body, string etag, CancellationToken token) { Updated = body; LastEtag = etag; LastAddress = address; return Result(body.Properties); }
        public Task<ResourceSnapshot<StorageResource>> ReconcileAsync(ScopedResourceAddress address, ReconcileStorageRequest body, string etag, CancellationToken token) { Reconciles++; Reconciled = body; LastEtag = etag; LastAddress = address; return Result(body.Properties); }
        public Task<ResourceSnapshot<StorageResource>> DeactivateAsync(ScopedResourceAddress address, string etag, CancellationToken token) { Deactivations++; LastEtag = etag; return Result(Current.Definition with { Enabled = false }); }
        private Task<ResourceSnapshot<StorageResource>> Result(StorageProperties? properties = null)
        {
            if (Failure is not null) return Task.FromException<ResourceSnapshot<StorageResource>>(Failure);
            if (properties is not null) Current = Current with { Definition = properties };
            revision++;
            return Task.FromResult(new ResourceSnapshot<StorageResource>(Current, $"\"revision-{revision}\""));
        }
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
