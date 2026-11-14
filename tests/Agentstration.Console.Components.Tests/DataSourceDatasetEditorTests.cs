using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class DataSourceDatasetEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Freshness duration (hh:mm:ss)")]
    [DataRow("fr-FR", "Durée de fraîcheur (hh:mm:ss)")]
    public async Task DirectModeRequiresExplicitPermissionAndRejectsRetentionWithoutStorage(string cultureName, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DatasetHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForElement("[data-testid='dataset-service']");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync($"{handler.Operation.Uid}:{handler.Operation.Generation}:{handler.Operation.Definition.DefinitionDigest}");
        await rendered.Find("[data-testid='data-source-display-name']").ChangeAsync("Direct inventory");
        await rendered.Find("[data-testid='data-source-name']").ChangeAsync("direct-inventory");
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        await rendered.Find("[data-testid='dataset-allow-direct']").ChangeAsync(true);
        rendered.WaitForAssertion(() => Assert.Contains(expected, rendered.Markup));
        await rendered.Find("[data-testid='lifecycle-ttl']").ChangeAsync("01:00:00");
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        Assert.IsNull(handler.Created);
        await rendered.Find("[data-testid='lifecycle-ttl']").ChangeAsync("");
        Assert.IsFalse(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        await rendered.Find("form").SubmitAsync();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        Assert.IsNull(handler.Created!.Properties.Dataset!.Storage);
        Assert.IsTrue(handler.Created.Properties.Dataset.Lifecycle!.AllowDirect);
        Assert.IsNull(handler.Created.Properties.Dataset.Lifecycle.TimeToLive);
    }

    [TestMethod]
    [DataRow("en-US", "No configuration")]
    [DataRow("fr-FR", "Sans configuration")]
    public async Task EmptyInputShowsNoArtificialFormAndCreationPinsReviewedOperationAndStorage(string cultureName, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
        using var handler = new DatasetHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://agentstration.test/") };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(new ContextProvider());
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(http));
        context.Services.AddSingleton<IConnectedServiceOperationsClient>(new ConnectedServiceOperationsApiClient(http));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new ResourceAccessPolicyApiClient(http));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ResourceScopeInventoryApiClient(http));
        var rendered = context.Render<DataSourceDatasetEditor>(parameters => parameters.Add(component => component.ScopeRef, "/instance"));
        rendered.WaitForElement("[data-testid='dataset-operation']");
        var storage = handler.Storage;
        var operation = handler.Operation;
        await rendered.Find("[data-testid='dataset-storage']").ChangeAsync($"{storage.Uid}:{storage.Generation}:{storage.ObservedState!.BindingDigest}");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync($"{operation.Uid}:{operation.Generation}:{operation.Definition.DefinitionDigest}");
        rendered.WaitForAssertion(() => Assert.Contains(expected, rendered.Find("[data-testid='dataset-parameters']").TextContent));
        Assert.HasCount(0, rendered.FindAll("[data-testid='dataset-parameters'] input, [data-testid='dataset-parameters'] textarea"));
        Assert.IsNull(handler.Created);
        await rendered.Find("[data-testid='data-source-display-name']").ChangeAsync("Inventory");
        await rendered.Find("[data-testid='data-source-name']").ChangeAsync("inventory");
        await rendered.Find("form").SubmitAsync();
        rendered.WaitForAssertion(() => Assert.IsNotNull(handler.Created));
        Assert.IsNull(handler.Created!.Properties.Profile);
        Assert.AreEqual(storage.Uid, handler.Created.Properties.Dataset!.Storage!.StorageUid);
        Assert.AreEqual(operation.Uid, handler.Created.Properties.Dataset.Acquisition.Operation.OperationUid);
        Assert.AreEqual(operation.Generation, handler.Created.Properties.Dataset.Acquisition.Operation.Generation);
        Assert.AreEqual("{}", handler.Created.Properties.Dataset.Acquisition.Parameters.GetRawText());
        Assert.AreEqual("{}", handler.Created.Properties.Configuration.GetRawText());
        Assert.AreEqual(0, handler.Activations);
        Assert.AreEqual(0, handler.Discoveries);
        Assert.IsEmpty(rendered.FindAll("[data-testid='connected-service-start-discovery'], [data-testid='dataset-operation-enabled']"));
        rendered.WaitForAssertion(() => Assert.IsFalse(rendered.Find("[data-testid='data-source-start-acquisition']").HasAttribute("disabled")));
    }

    [TestMethod]
    public async Task ServiceSelectionFiltersOperationsAndChangingParentClearsTheSavedCall()
    {
        using var handler = new DatasetHandler(withParameters: true);
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForElement("[data-testid='dataset-service']");
        Assert.IsTrue(rendered.Find("[data-testid='dataset-operation']").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll("[data-testid='dataset-parameters'] textarea"));
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        var operationKey = $"{handler.Operation.Uid}:{handler.Operation.Generation}:{handler.Operation.Definition.DefinitionDigest}";
        var otherKey = $"{handler.OtherOperation.Uid}:{handler.OtherOperation.Generation}:{handler.OtherOperation.Definition.DefinitionDigest}";
        rendered.WaitForAssertion(() =>
        {
            Assert.HasCount(1, rendered.FindAll($"[data-testid='dataset-operation'] option[value='{operationKey}']"));
            Assert.IsEmpty(rendered.FindAll($"[data-testid='dataset-operation'] option[value='{otherKey}']"));
        });
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync(operationKey);
        await rendered.Find("[data-testid='dataset-parameters'] [data-schema-path='$.query'] input").InputAsync("first-service-query");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.OtherService.Uid.ToString());
        Assert.AreEqual("", rendered.Find("[data-testid='dataset-operation']").GetAttribute("value"));
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        Assert.IsEmpty(rendered.FindAll($"[data-testid='dataset-operation'] option[value='{operationKey}']"));
        Assert.HasCount(1, rendered.FindAll($"[data-testid='dataset-operation'] option[value='{otherKey}']"));
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync(otherKey);
        Assert.AreEqual("", rendered.Find("[data-testid='dataset-parameters'] [data-schema-path='$.query'] input").GetAttribute("value"));
        Assert.IsNull(handler.Created);
        Assert.AreEqual(0, handler.Discoveries);
    }

    [TestMethod]
    public async Task EditingAnOlderOperationPinSelectsItsParentWithoutAdoptingTheNewRevision()
    {
        using var handler = new DatasetHandler { Existing = true, NewerOperation = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(handler.Service.Uid.ToString(), rendered.Find("[data-testid='dataset-service']").GetAttribute("value"));
            Assert.AreEqual($"{handler.Operation.Uid}:{handler.Operation.Generation}:{handler.Operation.Definition.DefinitionDigest}", rendered.Find("[data-testid='dataset-operation']").GetAttribute("value"));
            Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
            Assert.IsNull(handler.Created);
        });
    }

    [TestMethod]
    [DataRow("en-US", "does not provide an acquisition Flow")]
    [DataRow("fr-FR", "ne fournit pas de Flow d’acquisition")]
    public async Task MissingAdapterExplainsIneligibilityAndPreventsEnabledSave(string cultureName, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DatasetHandler { MissingAdapter = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        await SelectAsync(rendered, handler);
        rendered.WaitForAssertion(() => Assert.Contains(expected, rendered.Find("[data-testid='dataset-operation-ineligible']").TextContent));
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        await rendered.Find("form").SubmitAsync();
        Assert.IsNull(handler.Created);
    }

    [TestMethod]
    public async Task ChangedTypeDigestCannotSilentlySupplyAnAcquisitionAdapter()
    {
        using var handler = new DatasetHandler { StaleType = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        await SelectAsync(rendered, handler);
        Assert.IsTrue(rendered.Find("[data-testid='data-source-save']").HasAttribute("disabled"));
        Assert.IsNotNull(rendered.Find("[data-testid='dataset-operation-ineligible']"));
        Assert.IsNull(handler.Created);
    }

    [TestMethod]
    [DataRow("en-US", "data_source_rule_invalid", "relative directory prefix")]
    [DataRow("fr-FR", "data_source_rule_invalid", "préfixe de répertoire relatif")]
    [DataRow("en-US", "data_source_composition_invalid", "input, parameter and output contracts are compatible")]
    [DataRow("fr-FR", "data_source_composition_invalid", "contrats d’entrée, de paramètres et de sortie sont compatibles")]
    public async Task SaveFailureShowsLocalizedReasonAndCorrelationWithoutRawValues(string cultureName, string code, string expected)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DatasetHandler { FailSave = true, SaveFailureCode = code };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        await SelectAsync(rendered, handler);
        await rendered.Find("form").SubmitAsync();
        rendered.WaitForAssertion(() => Assert.AreEqual(code, rendered.Find("[data-testid='dataset-error-code']").TextContent));
        Assert.Contains(expected, rendered.Find("[role='alert']").TextContent);
        Assert.Contains("fixture-error-reference", rendered.Find("[role='alert']").TextContent);
        if (code == "data_source_composition_invalid") Assert.AreEqual("flow_composition_function_input_incompatible", rendered.Find("[data-testid='dataset-composition-error-code']").TextContent);
        Assert.DoesNotContain("private-saved-value", rendered.Markup);
    }

    [TestMethod]
    [DataRow("en-US", "Overview", "Definition", "Content")]
    [DataRow("fr-FR", "Vue d’ensemble", "Définition", "Contenu")]
    public async Task TabsKeepDraftInputsWhileOverviewAndYamlShowSavedDefinitionAndCancelRestoresIt(string cultureName, string overview, string definition, string content)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DatasetHandler(withParameters: true) { Existing = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForAssertion(() => Assert.HasCount(6, rendered.FindAll("[role=tab]")));
        Assert.AreEqual(overview, rendered.Find("[data-testid=dataset-overview-tab]").TextContent);
        Assert.AreEqual(definition, rendered.Find("[data-testid=dataset-definition-tab]").TextContent);
        Assert.AreEqual(content, rendered.Find("[data-testid=dataset-content-tab]").TextContent);
        Assert.IsTrue(rendered.Find("#dataset-panel-definition").HasAttribute("hidden"));
        await rendered.Find("[data-testid=dataset-definition-tab]").ClickAsync();
        Assert.IsFalse(rendered.Find("#dataset-panel-definition").HasAttribute("hidden"));
        await rendered.Find("[data-testid=data-source-display-name]").ChangeAsync("Unsaved draft");
        await rendered.Find("[data-testid=dataset-prefix]").ChangeAsync("draft/");
        await rendered.Find("[data-testid=dataset-parameters] [data-schema-path='$.query'] input").InputAsync("saved-call-draft");
        await rendered.Find("[data-testid=dataset-overview-tab]").ClickAsync();
        Assert.DoesNotContain("Unsaved draft", rendered.Find("#dataset-panel-overview").TextContent);
        Assert.DoesNotContain("draft/", rendered.Find("[data-testid=dataset-saved-configuration]").TextContent);
        await rendered.Find("[data-testid=dataset-yaml-tab]").ClickAsync();
        Assert.Contains("Inventory", rendered.Find("[data-testid=dataset-yaml]").TextContent);
        Assert.DoesNotContain("Unsaved draft", rendered.Find("[data-testid=dataset-yaml]").TextContent);
        await rendered.Find("[data-testid=dataset-definition-tab]").ClickAsync();
        Assert.AreEqual("Unsaved draft", rendered.Find("[data-testid=data-source-display-name]").GetAttribute("value"));
        Assert.AreEqual("draft/", rendered.Find("[data-testid=dataset-prefix]").GetAttribute("value"));
        Assert.AreEqual("saved-call-draft", rendered.Find("[data-testid=dataset-parameters] [data-schema-path='$.query'] input").GetAttribute("value"));
        await rendered.Find("#dataset-panel-definition button[type=button]").ClickAsync();
        rendered.WaitForAssertion(() => Assert.AreEqual("true", rendered.Find("[data-testid=dataset-overview-tab]").GetAttribute("aria-selected")));
        await rendered.Find("[data-testid=dataset-definition-tab]").ClickAsync();
        Assert.AreEqual("Inventory", rendered.Find("[data-testid=data-source-display-name]").GetAttribute("value"));
        Assert.AreEqual("", rendered.Find("[data-testid=dataset-prefix]").GetAttribute("value"));
        Assert.IsNull(handler.Created);
        Assert.AreEqual(0, handler.Discoveries);
    }

    [TestMethod]
    public async Task OverviewOrdersAndLimitsHistoryAndReadingAnAcquisitionOpensTheContentTab()
    {
        using var handler = new DatasetHandler { Existing = true, WithHistory = true };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var rendered = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.Name, "inventory").Add(c => c.ScopeRef, "/instance"));
        rendered.WaitForAssertion(() => Assert.HasCount(3, rendered.FindAll("[data-testid=data-source-acquisition-row]")));
        Assert.Contains("flow-3", rendered.FindAll("[data-testid=data-source-acquisition-row]")[0].TextContent);
        Assert.Contains("flow-3", rendered.Find("[data-testid=dataset-latest-acquisition] a").GetAttribute("href")!);
        await rendered.Find("[data-testid=dataset-acquisitions-tab]").ClickAsync();
        Assert.HasCount(4, rendered.FindAll("[data-testid=data-source-acquisition-row]"));
        await rendered.FindAll("[data-testid=dataset-read-content]")[1].ClickAsync();
        rendered.WaitForAssertion(() => Assert.AreEqual("true", rendered.Find("[data-testid=dataset-content-tab]").GetAttribute("aria-selected")));
        Assert.Contains("acq-2", rendered.Find("[data-testid=dataset-content]").TextContent);
        Assert.AreEqual(1, handler.ContentReads);
        Assert.IsEmpty(rendered.FindAll("[data-testid=data-source-acquisition-row]"));
        await rendered.Find("[data-testid=dataset-content] button").ClickAsync();
        Assert.IsEmpty(rendered.FindAll("[data-testid=dataset-content]"));
        await rendered.Find(".dataset-content-picker select").ChangeAsync(handler.Acquisitions[0].Uid.ToString());
        rendered.WaitForAssertion(() => Assert.Contains("acq-0", rendered.Find("[data-testid=dataset-content]").TextContent));
        Assert.AreEqual(2, handler.ContentReads);
        Assert.IsEmpty(rendered.FindAll("[role=alert]"));
    }

    [TestMethod]
    [DataRow("en-US", false)]
    [DataRow("fr-FR", false)]
    [DataRow("en-US", true)]
    [DataRow("fr-FR", true)]
    public async Task DocumentAcquisitionRequiresAnExplicitCollectionAndPinsItsIdentity(string cultureName, bool hasCollection)
    {
        using var culture = new TestCultureScope(cultureName);
        using var documents = new StructuredStorageUiFixture(hasCollection);
        using var handler = new DatasetHandler { Storage = documents.Storage, StorageTypes = [documents.Type] };
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http);
        var page = context.Render<DataSourceDatasetEditor>(p => p.Add(c => c.ScopeRef, "/instance"));
        await SelectAsync(page, handler);
        await page.Find("[data-testid=data-source-display-name]").ChangeAsync("Acquired alerts");
        await page.Find("[data-testid=data-source-name]").ChangeAsync("acquired-alerts");
        Assert.IsTrue(page.Find("[data-testid=data-source-save]").HasAttribute("disabled"));
        Assert.IsNotNull(page.Find("[data-testid=dataset-document-collection-required] a[href*='view=definition']"));
        Assert.IsNull(handler.Created);
        if (!hasCollection) return;
        await page.Find("[data-testid=dataset-document-collection]").ChangeAsync(documents.Collection.ToString());
        Assert.IsFalse(page.Find("[data-testid=data-source-save]").HasAttribute("disabled"));
        await page.Find("form").SubmitAsync();
        page.WaitForAssertion(() => Assert.AreEqual(documents.Collection, handler.Created?.Properties.Dataset?.DocumentCollectionUid));
    }

    private static async Task SelectAsync(IRenderedComponent<DataSourceDatasetEditor> rendered, DatasetHandler handler)
    {
        rendered.WaitForElement("[data-testid='dataset-storage']");
        var storage = handler.Storage;
        await rendered.Find("[data-testid='dataset-storage']").ChangeAsync($"{storage.Uid}:{storage.Generation}:{storage.ObservedState!.BindingDigest}");
        await rendered.Find("[data-testid='dataset-service']").ChangeAsync(handler.Service.Uid.ToString());
        var operation = handler.Operation;
        await rendered.Find("[data-testid='dataset-operation']").ChangeAsync($"{operation.Uid}:{operation.Generation}:{operation.Definition.DefinitionDigest}");
    }

    private static async Task<BunitContext> SetupAsync(HttpClient http)
    {
        var context = new BunitContext();
        context.JSInterop.SetupVoid("agentstrationDataSources.focusTab", _ => true).SetVoidResult();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(new ContextProvider());
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IDataSourcesClient>(new DataSourcesApiClient(http));
        context.Services.AddSingleton<IStoragesClient>(new StoragesApiClient(http));
        context.Services.AddSingleton<IConnectedServicesClient>(new ConnectedServicesApiClient(http));
        context.Services.AddSingleton<IConnectedServiceOperationsClient>(new ConnectedServiceOperationsApiClient(http));
        context.Services.AddSingleton<IResourceAccessPolicyClient>(new ResourceAccessPolicyApiClient(http));
        context.Services.AddSingleton<IResourceScopeInventoryClient>(new ResourceScopeInventoryApiClient(http));
        return context;
    }

    private sealed class DatasetHandler : HttpMessageHandler
    {
        public DatasetHandler(bool withParameters = false)
        {
            if (withParameters) Operation = Operation with
            {
                Definition = Operation.Definition with
                {
                    Definition = JsonSerializer.SerializeToElement(new
                    {
                        isReadOnly = true,
                        responseShape = new { type = "object" },
                        requestShape = new
                        { type = "object", properties = new { query = new { type = "string" } }, required = new[] { "query" }, additionalProperties = false }
                    })
                }
            };
        }
        public IReadOnlyList<StorageTypeResource> StorageTypes { get; set; } = [];
        public bool MissingAdapter { get; set; }
        public bool FailSave { get; set; }
        public string SaveFailureCode { get; set; } = "data_source_rule_invalid";
        public bool StaleType { get; set; }
        public ConnectedServiceOperationTypeResource OperationType => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedServiceOperationType",
            Uid = Operation.Definition.Type.TypeUid,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "fixture-operation" },
            Generation = 1,
            Definition = new()
            {
                DisplayName = "Fixture operation",
                PreferredVersion = "1.0.0",
                Versions = [new("1.0.0", StaleType ? "other-digest" : "type-digest", JsonSerializer.SerializeToElement(new { type = "object" }),
                    ConnectedServiceOperationInteraction.RequestResponse, new("fixture.read", "1.0.0"))
                { Acquisition = MissingAdapter ? null : new(new()
                    { Flow = new("fixture-acquire", ResourceScopeRef.Instance), FlowUid = Guid.NewGuid(), Version = "1.0.0", DefinitionHash = "flow-hash", Contract = "connected-service-operation-acquisition/v1" },
                    InputSchemaProperty: "requestShape", OutputSchemaProperty: "responseShape", ReadOnlyProperty: "isReadOnly") }]
            }
        };
        public CreateDataSourceRequest? Created { get; private set; }
        public int Activations { get; private set; }
        public int Discoveries { get; private set; }
        public bool NewerOperation { get; set; }
        public bool Existing { get; set; }
        public bool WithHistory { get; set; }
        public int ContentReads { get; private set; }
        public IReadOnlyList<DataSourceAcquisitionResource> Acquisitions => Enumerable.Range(0, 4).Select(index => new DataSourceAcquisitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "DataSourceAcquisition",
            Uid = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"),
            Metadata = new() { Name = $"acq-{index}" },
            ScopeRef = ResourceScopeRef.Instance,
            DataSourceScopeRef = ResourceScopeRef.Instance,
            DataSourceUid = Source.Uid,
            DataSourceName = Source.Name,
            DataSourceNamespace = Source.Namespace,
            DataSourceGeneration = 1,
            FlowRunId = $"flow-{index}",
            State = DataSourceAcquisitionState.Succeeded,
            CorrelationId = "fixture",
            RequestHash = "fixture",
            CreatedBy = Guid.Empty,
            TenantId = Guid.Empty,
            WorkspaceId = Guid.Empty,
            CreatedAt = DateTimeOffset.Parse("2026-11-09T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture).AddHours(index),
            Content = new($"acq-{index}", "v1", 20, "digest", "reference")
        }).ToArray();
        public ConnectedServiceResource Service => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedService",
            Uid = Operation.Definition.Service.ServiceUid,
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Metadata = new() { Name = "fixture" },
            Definition = new() { DisplayName = "Fixture", Type = null!, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        public ConnectedServiceResource OtherService { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedService",
            Uid = Guid.NewGuid(),
            ScopeRef = ResourceScopeRef.Instance,
            Generation = 1,
            Metadata = new() { Name = "other" },
            Definition = new() { DisplayName = "Fixture", Type = null!, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        public ConnectedServiceOperationResource OtherOperation => Operation with
        {
            Uid = OtherService.Uid,
            Metadata = new() { Name = "other-operation" },
            Definition = Operation.Definition with
            {
                DisplayName = "Other operation",
                Service = new()
                { ServiceUid = OtherService.Uid, Generation = 1, Service = new(OtherService.Address, OtherService.ScopeRef!.Value) }
            }
        };
        public DataSourceResource Source => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "DataSource",
            Uid = Service.Uid,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "inventory" },
            Generation = 1,
            Definition = new()
            {
                DisplayName = "Inventory",
                Dataset = new(
                new(new(Storage.ScopeRef!.Value, Storage.Namespace, Storage.Kind, Storage.Name), Storage.Uid, Storage.Generation, Storage.ObservedState!.BindingDigest!),
                new(Write: true), new(new(new(Operation.ScopeRef!.Value, Operation.Namespace, Operation.Kind, Operation.Name), Operation.Uid, Operation.Generation, Operation.Definition.DefinitionDigest), JsonSerializer.SerializeToElement(new { })))
            }
        };
        public StorageResource Storage { get; set; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "Storage",
            Uid = Guid.NewGuid(),
            Generation = 3,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "local" },
            Definition = new() { DisplayName = "Local", Type = null!, Configuration = JsonSerializer.SerializeToElement(new { }), Envelope = JsonSerializer.SerializeToElement(new { }) },
            ObservedState = new(StorageAvailability.Ready, DateTimeOffset.UtcNow, "binding", [])
        };
        public ConnectedServiceOperationResource Operation { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedServiceOperation",
            Uid = Guid.NewGuid(),
            Generation = 7,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "inventory" },
            Enabled = true,
            Definition = new()
            {
                DisplayName = "Inventory",
                ExternalId = "inventory",
                Type = new() { Type = new("fixture-operation", ResourceScopeRef.Instance), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "type-digest" },
                DefinitionDigest = "operation-digest",
                Service = new() { Service = new(new(ResourceNamespace.Default, "ConnectedService", "fixture"), ResourceScopeRef.Instance), ServiceUid = Guid.NewGuid(), Generation = 1 },
                Definition = JsonSerializer.SerializeToElement(new { requestShape = new { type = "object", additionalProperties = false }, responseShape = new { type = "object" }, isReadOnly = true }),
                Provenance = new() { Discovery = new(ResourceScopeRef.Instance, ResourceNamespace.Default, "ConnectedServiceOperationDiscovery", "discovery"), DiscoveryUid = Guid.NewGuid(), ManifestDigest = "manifest", DiscoveredAt = DateTimeOffset.UtcNow }
            },
            ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UtcNow }
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (request.Method == HttpMethod.Post && path == "/api/datasources")
            {
                if (FailSave)
                {
                    var failure = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                    { Content = JsonContent.Create(new { title = "Invalid Data Source", type = "https://agentstration.dev/problems/" + SaveFailureCode, detail = "private-saved-value",
                        compositionErrorCode = SaveFailureCode == "data_source_composition_invalid" ? "flow_composition_function_input_incompatible" : null }) };
                    failure.Headers.Add("X-Correlation-ID", "fixture-error-reference");
                    return failure;
                }
                Created = await request.Content!.ReadFromJsonAsync<CreateDataSourceRequest>(cancellationToken);
                body = new DataSourceResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = "DataSource",
                    Uid = Guid.NewGuid(),
                    Metadata = new() { Name = Created!.Name },
                    ScopeRef = ResourceScopeRef.Instance,
                    Generation = 1,
                    Definition = Created.Properties
                };
            }
            else if (path == "/api/connected-service-operation-types") body = new[] { OperationType };
            else if (path == "/api/connected-service-operations") body = new[] { NewerOperation ? Operation with { Generation = Operation.Generation + 1 } : Operation, OtherOperation };
            else if (path == "/api/connected-services/fixture/operations") body = new[] { NewerOperation ? Operation with { Generation = Operation.Generation + 1 } : Operation, OtherOperation };
            else if (path == "/api/connected-services/other/operations") body = new[] { OtherOperation, Operation };
            else if (path == "/api/connected-services") body = new[] { Service, OtherService };
            else if (path == "/api/datasources/inventory" && Existing) body = Source;
            else if (path.EndsWith("/operation-discoveries", StringComparison.Ordinal)) { Discoveries++; body = Array.Empty<ConnectedServiceOperationDiscoveryResource>(); }
            else if (path == "/api/storage-types") body = new { value = StorageTypes };
            else if (path == "/api/storages") body = new { value = new[] { Storage } };
            else if (path.EndsWith("/readiness", StringComparison.Ordinal)) body = new DataSourceReadiness(true, true, null, []) { CanAcquire = true };
            else if (path.EndsWith("/acquisitions", StringComparison.Ordinal)) body = WithHistory ? Acquisitions : Array.Empty<DataSourceAcquisitionResource>();
            else if (path.StartsWith("/api/datasourceacquisitions/", StringComparison.Ordinal) && path.EndsWith("/content", StringComparison.Ordinal))
            {
                ContentReads++;
                var acquisition = Acquisitions.Single(item => path.Contains($"/{item.Name}/", StringComparison.Ordinal));
                body = new StorageObjectContent(acquisition.Content!, 0, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"name\":\"{acquisition.Name}\"}}")), true);
            }
            else if (path.EndsWith("/enabled", StringComparison.Ordinal)) { Activations++; body = Operation; }
            else return new(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            response.Headers.ETag = new("\"fixture\"");
            return response;
        }
    }

    private sealed class ContextProvider : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Administrator", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "instance", "Instance",
            Guid.NewGuid(), "operations", "Operations", new HashSet<string> { AuthorizationPermissions.ResourcesWrite }, [], []));
    }
}
