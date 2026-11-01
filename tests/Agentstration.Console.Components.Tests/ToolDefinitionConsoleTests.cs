using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolDefinitionConsoleTests
{
    [TestMethod]
    public void DefinitionsPageListsFlowBackedMcpTools()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());

        var rendered = context.Render<ToolDefinitions>();

        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Send notification");
            StringAssert.Contains(rendered.Markup, "notification-flow");
            StringAssert.Contains(rendered.Markup, "/tools/definitions/notification.send?namespace=default");
        });
    }

    [TestMethod]
    public void EditorLoadsReadOnlyContractsWhenAFlowIsSelected()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolDefinitionsClient>(new DefinitionClient());
        var flows = new FlowClient();
        context.Services.AddSingleton<IFlowApiClient>(flows);
        context.Services.AddSingleton<ISchemasClient>(new SchemasClient());

        var rendered = context.Render<ToolDefinitionEditor>();
        rendered.WaitForElement("select").Change("/workspaces/11111111-1111-1111-1111-111111111111|default|contract-flow");

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("1.2.0", flows.RequestedVersion);
            StringAssert.Contains(rendered.Markup, "Read-only contracts provided by published Flow version 1.2.0");
            Assert.HasCount(0, rendered.FindAll(".flow-schema-mode"));
            Assert.IsTrue(rendered.Find("[data-testid='tool-definition-input-schema']") is not null);
        });

        rendered.Find("[data-testid='tool-definition-contract-output-tab']").Click();
        rendered.WaitForAssertion(() =>
            Assert.IsTrue(rendered.Find("[data-testid='tool-definition-output-schema']") is not null));
    }

    private sealed class DefinitionClient : IToolDefinitionsClient
    {
        public Task<IReadOnlyList<ToolDefinitionResource>> GetAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ToolDefinitionResource>>([
            new ToolDefinitionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.ToolDefinition,
                Metadata = new ResourceMetadata { Name = "notification.send" },
                ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
                Definition = new ToolDefinitionProperties
                {
                    DisplayName = "Send notification",
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                    Flow = new ToolDefinitionFlowTarget { Name = "notification-flow" }
                }
            }
        ]);

        public Task<ResourceSnapshot<ToolDefinitionResource>> GetAsync(string name, ResourceNamespace @namespace, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> CreateAsync(Agentstration.Tools.Contracts.CreateToolDefinitionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> UpdateAsync(string name, ResourceNamespace @namespace, Agentstration.Tools.Contracts.PutToolDefinitionRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolDefinitionResource>> SetEnabledAsync(string name, ResourceNamespace @namespace, bool enabled, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string name, ResourceNamespace @namespace, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FlowClient : IFlowApiClient
    {
        private static readonly JsonElement InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { query = new { type = "string" } }
        });
        private static readonly JsonElement OutputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { result = new { type = "string" } }
        });
        private static readonly FlowGraphDefinition Graph = new()
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = InputSchema },
                new OutputFlowStepDefinition { Name = "completed", Outcome = FlowOutputOutcome.Success, Schema = OutputSchema }
            ]
        };

        public string? RequestedVersion { get; private set; }

        public Task<IReadOnlyList<FlowSummary>> GetFlowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowSummary>>([
                new("contract-flow", "Contract Flow", "flow", "1.2.0", "Published", 0, 0, DateTimeOffset.UtcNow)
                {
                    ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                    ActiveVersion = "1.2.0"
                }
            ]);

        public Task<IReadOnlyList<FlowVersionResponse>> GetFlowVersionsAsync(ResourceNamespace @namespace,
            string flowId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowVersionResponse>>([
                new(flowId, "1.2.0", null, new WorkflowFlowDefinition("input", [], [], []),
                    new Dictionary<string, string>(), DateTimeOffset.UtcNow, Graph)
            ]);

        public async Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId,
            string version, CancellationToken cancellationToken)
        {
            RequestedVersion = version;
            return (await GetFlowVersionsAsync(@namespace, flowId, cancellationToken)).Single();
        }

        public Task<FlowVersionResponse> GetFlowVersionAsync(ResourceNamespace @namespace, string flowId,
            string version, ResourceScopeRef? scopeRef, CancellationToken cancellationToken) =>
            GetFlowVersionAsync(@namespace, flowId, version, cancellationToken);

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

    private sealed class SchemasClient : ISchemasClient
    {
        public Task<IReadOnlyList<SchemaResource>> GetSchemasAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SchemaResource>>([]);
        public Task<SchemaFormatValidationResult> ValidateDefinitionAsync(ValidateSchemaDefinitionRequest request, CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<SchemaFormatValidationResult> ValidateInstanceAsync(ValidateSchemaInstanceRequest request, CancellationToken cancellationToken = default) => Task.FromResult(SchemaFormatValidationResult.Valid);
        public Task<IReadOnlyList<SchemaFormatDescriptor>> GetFormatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> GetSchemaAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> CreateAsync(CreateSchemaRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SchemaResource>> UpdateAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PutSchemaRequest request, string etag, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SchemaVersionResource>> GetVersionsAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaVersionResource> GetVersionAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchemaVersionResource> PublishAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, PublishSchemaVersionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
