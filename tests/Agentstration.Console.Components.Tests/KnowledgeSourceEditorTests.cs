using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Knowledge.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class KnowledgeSourceEditorTests
{
    [TestMethod]
    public void CreationDerivesTechnicalNameUntilTheUserOverridesIt()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub());

        var rendered = context.Render<KnowledgeSourceEditor>();

        rendered.WaitForAssertion(() =>
        {
            var fields = rendered.FindAll(".form-grid > label");
            Assert.AreEqual("knowledge-source-display-name", fields[0].QuerySelector("input")?.GetAttribute("data-testid"));
            Assert.AreEqual("knowledge-source-name", fields[1].QuerySelector("input")?.GetAttribute("data-testid"));
            Assert.IsTrue(rendered.Find("[data-testid='knowledge-source-display-name']").HasAttribute("autofocus"));
        });

        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Documentation Générale !");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("documentation-generale", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));

        rendered.Find("[data-testid='knowledge-source-name']").Change("docs-custom");
        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Documentation Interne");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("docs-custom", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));
    }

    [TestMethod]
    public void EditionKeepsTechnicalNameVisibleAndImmutable()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource()));
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/knowledge-sources/agentstration-documentation/edit?namespace=default");

        var rendered = context.Render<KnowledgeSourceEditor>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            var technicalName = rendered.Find("[data-testid='knowledge-source-name']");
            Assert.AreEqual("agentstration-documentation", technicalName.GetAttribute("value"));
            Assert.IsTrue(technicalName.HasAttribute("readonly"));
            Assert.IsTrue(technicalName.HasAttribute("disabled"));
            Assert.IsFalse(rendered.Find("[data-testid='knowledge-source-display-name']").HasAttribute("autofocus"));
        });

        rendered.Find("[data-testid='knowledge-source-display-name']").Change("Updated documentation");
        rendered.WaitForAssertion(() =>
            Assert.AreEqual("agentstration-documentation", rendered.Find("[data-testid='knowledge-source-name']").GetAttribute("value")));
    }

    [TestMethod]
    public void DetailsOrganizesTheLifecycleIntoAccessibleTabs()
    {
        using var culture = new TestCultureScope("en-US");
        var acquisition = ExistingAcquisition();
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource(), [acquisition]));

        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(component => component.Name, "agentstration-documentation"));

        rendered.WaitForAssertion(() =>
        {
            var tabs = rendered.FindAll("[role='tab']");
            Assert.HasCount(5, tabs);
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-overview-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll(".metric-grid"));
            Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-acquisitions']"));
            Assert.AreEqual("/flows/knowledge-ingestion-builtin", rendered.Find("[data-testid='knowledge-ingestion-flow-link']").GetAttribute("href"));
            Assert.AreEqual("/flows/knowledge-retrieval-builtin", rendered.Find("[data-testid='knowledge-retrieval-flow-link']").GetAttribute("href"));
        });

        rendered.Find("[data-testid='knowledge-source-delete']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));
        rendered.Find("[data-testid='knowledge-source-delete-dialog'] .button-secondary").Click();
        rendered.WaitForAssertion(() => Assert.IsEmpty(rendered.FindAll("[data-testid='knowledge-source-delete-dialog']")));

        rendered.Find("[data-testid='knowledge-source-acquisitions-tab']").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("true", rendered.Find("[data-testid='knowledge-source-acquisitions-tab']").GetAttribute("aria-selected"));
            Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-acquisitions']"));
            Assert.IsEmpty(rendered.FindAll(".metric-grid"));
            Assert.AreEqual("knowledge-source-acquisitions-tab", rendered.Find("[role='tabpanel']").GetAttribute("aria-labelledby"));
            var flowRunLink = rendered.Find(".knowledge-flow-run-link");
            Assert.AreEqual(acquisition.FlowRunId, flowRunLink.GetAttribute("title"));
            Assert.EndsWith("…", flowRunLink.TextContent);
            Assert.AreEqual("Attempt 1", rendered.Find(".knowledge-acquisition-identity small").TextContent);
        });

        rendered.Find("[data-testid='knowledge-source-snapshots-tab']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-snapshots']")));

        rendered.Find("[data-testid='knowledge-source-retrieval-tab']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-retrieval-verification']")));

        rendered.Find("[data-testid='knowledge-source-tools-tab']").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='knowledge-source-tool-exposure']")));
    }

    [TestMethod]
    public void ListMakesTheKnowledgeSourceNameClickable()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(new KnowledgeClientStub(ExistingSource()));

        var rendered = context.Render<KnowledgeSources>();

        rendered.WaitForAssertion(() =>
        {
            var link = rendered.Find("[data-testid='knowledge-source-name-link']");
            Assert.AreEqual("Agentstration documentation", link.TextContent);
            Assert.AreEqual("/knowledge-sources/agentstration-documentation", link.GetAttribute("href"));
        });
    }

    private static BunitContext CreateContext(IKnowledgeSourcesClient knowledge)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton(knowledge);
        context.Services.AddSingleton<IFlowApiClient>(new FlowClientStub());
        return context;
    }

    private static KnowledgeSourceResource ExistingSource() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeSource,
        Metadata = new() { Name = "agentstration-documentation" },
        ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
        Definition = new()
        {
            DisplayName = "Agentstration documentation",
            IngestionFlow = new() { Name = "knowledge-ingestion-builtin" },
            RetrievalFlow = new() { Name = "knowledge-retrieval-builtin" }
        }
    };

    private static KnowledgeAcquisitionResource ExistingAcquisition() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = KnowledgeResourceKinds.KnowledgeAcquisition,
        Metadata = new() { Name = "acquisition-b70e6bffe11a4af291349d8ef86e5166" },
        KnowledgeSourceUid = Guid.NewGuid(),
        KnowledgeSourceName = "agentstration-documentation",
        KnowledgeSourceNamespace = ResourceNamespace.Default,
        KnowledgeSourceGeneration = 1,
        IngestionFlow = new ResolvedKnowledgeFlowBinding("knowledge-ingestion-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Ingestion),
        FlowRunId = "flowrun-knowledge-b70e6bffe11a4af291349d8ef86e5166",
        State = KnowledgeAcquisitionState.Pending,
        CorrelationId = "correlation-1",
        RequestHash = "request-hash",
        CreatedBy = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class KnowledgeClientStub(KnowledgeSourceResource? source = null, IReadOnlyList<KnowledgeAcquisitionResource>? acquisitions = null) : IKnowledgeSourcesClient
    {
        public Task<IReadOnlyList<KnowledgeSourceResource>> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<KnowledgeSourceResource>>(source is null ? [] : [source]);
        public Task<ResourceSnapshot<KnowledgeSourceResource>> GetAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            source is not null ? Task.FromResult(new ResourceSnapshot<KnowledgeSourceResource>(source, "\"etag-1\"")) : throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceResource>> CreateAsync(CreateKnowledgeSourceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceResource>> UpdateAsync(ResourceNamespace @namespace, string name, PutKnowledgeSourceRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSourceResource>> SetEnabledAsync(ResourceNamespace @namespace, string name, bool enabled, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeSourceReadiness> GetReadinessAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(new KnowledgeSourceReadiness(true, true,
                new ResolvedKnowledgeFlowBinding("knowledge-ingestion-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Ingestion),
                new ResolvedKnowledgeFlowBinding("knowledge-retrieval-builtin", ResourceNamespace.Default, "1.0.0", true, null, null, KnowledgeFlowContracts.Retrieval),
                []));
        public Task<KnowledgeSourceToolExposureResource?> GetExposureAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult<KnowledgeSourceToolExposureResource?>(null);
        public Task<ResourceSnapshot<KnowledgeSourceToolExposureResource>> PublishExposureAsync(ResourceNamespace @namespace, string name, PublishKnowledgeSourceToolExposureRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeAcquisitionResource>> GetAcquisitionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult(acquisitions ?? []);
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> StartAcquisitionAsync(ResourceNamespace @namespace, string name, StartKnowledgeAcquisitionRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> CancelAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeAcquisitionResource>> RetryAcquisitionAsync(ResourceNamespace @namespace, string acquisitionId, RetryKnowledgeAcquisitionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeSnapshotView>> GetSnapshotsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<KnowledgeSnapshotView>>([]);
        public Task<KnowledgeSnapshotView> SelectActiveSnapshotAsync(ResourceNamespace @namespace, string name, string snapshotName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<KnowledgeSnapshotResource>> PublishSnapshotAsync(ResourceNamespace @namespace, string acquisitionId, PublishKnowledgeSnapshotRequest request, string? idempotencyKey = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> SearchAsync(ResourceNamespace @namespace, string name, SearchKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> QueryAsync(ResourceNamespace @namespace, string name, QueryKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnowledgeRetrievalResult> ReadAsync(ResourceNamespace @namespace, string name, ReadKnowledgeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FlowClientStub : IFlowApiClient
    {
        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FlowSummary>>([]);
        public Task<FlowResponse> GetFlowAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> GetFlowSnapshotAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> CreateFlowAsync(CreateFlowRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowResourceSnapshot> UpdateFlowAsync(string flowId, UpdateFlowRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> CreateFlowVersionAsync(string flowId, CreateFlowVersionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRun>> GetFlowRunsAsync(string? flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> GetFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FlowRunEvent>> GetFlowRunEventsAsync(string runId, long afterSequence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<InputRequest>> GetFlowRunInputsAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InputRequest> RespondToFlowRunInputAsync(string runId, string inputId, JsonElement value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateFlowRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CancelFlowRunAsync(string runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public async IAsyncEnumerable<FlowRun> ObserveFlowRunAsync(string runId, [EnumeratorCancellation] CancellationToken cancellationToken) { await Task.CompletedTask; yield break; }
        public Task<FlowDraftResponse> CreateDraftAsync(CreateFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> GetDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(string flowId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateDraftAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowSourceResponse> GetDraftSourceAsync(string flowId, string format, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> ReplaceDraftSourceAsync(string flowId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishDraftAsync(string flowId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> CreateDraftRunAsync(string flowId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> CreateDraftFromVersionAsync(string flowId, string version, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
