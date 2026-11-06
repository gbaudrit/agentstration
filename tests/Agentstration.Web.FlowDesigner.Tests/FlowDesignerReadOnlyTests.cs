using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.Components.Schemas;
using Agentstration.Web.Components.State;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.Diagramming;
using Agentstration.Web.FlowDesigner.State;
using AngleSharp.Dom;
using Blazor.Diagrams;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Geometry;
using BlazorMonaco.Editor;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

// Shared setup for the behavior-focused partials; retain the class name for existing MSTest filters.
[TestClass]
[DoNotParallelize]
public sealed partial class FlowDesignerReadOnlyTests
{
    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddSingleton(new UserPreferencesState(new PreferencesClientStub()));
        return context;
    }

    private static readonly ResourceScopeRef TestScope = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static BlazorDiagram GetDiagram(FlowCanvas canvas) =>
        (BlazorDiagram)typeof(FlowCanvas).GetField("diagram", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(canvas)!;

    private sealed class PreferencesClientStub : IUserPreferencesClient
    {
        public Task<UserPreferences> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferences(UserTheme.Light, null, DateTimeOffset.UnixEpoch));

        public Task<UserPreferences> UpdateAsync(UserTheme theme, string? language, CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferences(theme, language, DateTimeOffset.UnixEpoch));
    }

    private sealed class ResourceProviderStub : IFlowDesignerResourceProvider
    {
        private readonly IReadOnlyList<string> publishedVersions;
        public IReadOnlyList<FlowDesignerAgent> Agents { get; init; } = [];
        public IReadOnlyList<FlowDesignerFlow> Flows { get; init; } = [new("analysis", "News analysis", new("pack.news"), TestScope, "2.0.0")];
        public IReadOnlyDictionary<string, IReadOnlyList<FlowDesignerFlowVersion>> FlowVersions { get; init; } = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal);
        public ResourceScopeRef? AgentContextScope { get; private set; }
        public ResourceScopeRef? FlowContextScope { get; private set; }
        public ResourceScopeRef? ToolContextScope { get; private set; }
        public IReadOnlyList<FlowDesignerSchemaVersion> SchemaVersions { get; init; } = [];
        public IReadOnlyList<FlowDesignerTool> Tools { get; init; } =
        [
            new(
                "notification.send",
                "Send notification",
                ResourceNamespace.Default,
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { deliveryId = new { type = "string" } } }),
                Enabled: true,
                Available: true,
                RequiresApproval: false,
                TestScope),
            new(
                "warehouse.lookup",
                "Look up warehouse",
                new("pack.tools"),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { requestId = new { type = "string" } } }),
                null,
                Enabled: true,
                Available: true,
                RequiresApproval: false,
                TestScope)
        ];

        public ResourceProviderStub(params string[] publishedVersions) => this.publishedVersions = publishedVersions;

        public Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
        {
            AgentContextScope = contextScope;
            return Task.FromResult(Agents);
        }
        public Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
        {
            FlowContextScope = contextScope;
            return Task.FromResult<IReadOnlyList<FlowDesignerFlow>>(Flows.Select(flow => flow with { ScopeRef = contextScope }).ToArray());
        }
        public Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            FlowVersions.TryGetValue($"{@namespace.Value}|{name}", out var configured)
                ? Task.FromResult(configured)
                : name == "sample" && @namespace.IsDefault
                ? Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>(publishedVersions.Select(version => new FlowDesignerFlowVersion(version, null, null)).ToArray())
                : Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([new("2.0.0",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { summary = new { type = "string" } } }),
                [new("approved", "Approved", FlowOutputOutcome.Success, null), new("rejected", "Rejected", FlowOutputOutcome.Error, null)])]);
        public Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(ResourceScopeRef contextScope, CancellationToken cancellationToken)
        {
            ToolContextScope = contextScope;
            return Task.FromResult<IReadOnlyList<FlowDesignerTool>>(Tools.Select(tool => tool with { ScopeRef = contextScope }).ToArray());
        }
        public Task<IReadOnlyList<FlowDesignerSchemaVersion>> GetSchemaVersionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SchemaVersions);
    }

    private sealed class BackendStub : IFlowDesignerBackend
    {
        private readonly bool readOnly;
        private FlowDraftResponse draft;
        private string source = "entryStep: input";
        public BackendStub(bool readOnly = true, FlowGraphDefinition? definition = null)
        {
            this.readOnly = readOnly;
            draft = CreateDraft(definition);
        }
        public Func<Task>? BeforeSave { get; set; }
        public Func<Task>? BeforeReplace { get; set; }
        public Func<FlowDesignerTarget, Task<FlowSourceResponse>>? ReadSource { get; set; }
        public List<string> SavedETags { get; } = [];
        public int SaveCount { get; private set; }
        public int ValidationCount { get; private set; }
        public FlowGraphDefinition? LastSavedDefinition { get; private set; }
        public string? LastReplacementSource { get; private set; }
        public string? ReplacementError { get; set; }
        public FlowGraphDefinition? ReplacementDefinition { get; set; }
        public bool VersionConflict { get; set; }
        public string? LastPublishVersion { get; private set; }
        public FlowDesignerTarget? LoadedTarget { get; private set; }
        public Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            LoadedTarget = target;
            var value = draft.Value;
            return Task.FromResult(new FlowDesignerLoadResult(new(value.FlowId, value.DisplayName, value.Description, value.Tags, value.Definition) { ScopeRef = value.ScopeRef }, source, ETag: readOnly ? null : draft.ETag, PublishedVersion: readOnly ? "2.1.0" : null));
        }
        public Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken) =>
            ReadSource?.Invoke(target) ?? Task.FromResult(new FlowSourceResponse(source, "yaml", draft.Value.Revision));
        public async Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken)
        {
            SaveCount++;
            SavedETags.Add(etag);
            if (BeforeSave is not null) await BeforeSave();
            LastSavedDefinition = request.Definition;
            draft = new(draft.Value with { Definition = request.Definition, Revision = draft.Value.Revision + 1 }, $"\"etag-{draft.Value.Revision + 1}\"");
            return draft;
        }
        public async Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken)
        {
            SavedETags.Add(etag);
            LastReplacementSource = request.Source;
            if (BeforeReplace is not null) await BeforeReplace();
            if (ReplacementError is not null) throw new InvalidOperationException(ReplacementError);
            source = request.Source;
            draft = new(draft.Value with { Definition = ReplacementDefinition ?? draft.Value.Definition, Revision = draft.Value.Revision + 1 }, "\"next-etag\"");
            return draft;
        }
        public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            ValidationCount++;
            return Task.FromResult(new FlowValidationResponse(true, []));
        }
        public Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken)
        {
            LastPublishVersion = request.Version;
            if (VersionConflict)
                throw new FlowDesignerVersionAlreadyPublishedException(request.Version, new InvalidOperationException("duplicate"));
            return Task.FromResult(new FlowVersionResponse(target.ResourceId, request.Version, null,
                new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "agent-id")),
                new Dictionary<string, string>(), DateTimeOffset.UnixEpoch));
        }
        public Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static FlowDraftResponse CreateDraft(FlowGraphDefinition? definition = null)
        {
            var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            definition ??= new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
            return new(new FlowDraft { WorkspaceId = WorkspaceId, ScopeRef = ResourceScopeRef.Workspace(WorkspaceId.Value), Id = "draft", FlowId = new("sample"), DisplayName = "Sample", Definition = definition, CreatedAt = now, UpdatedAt = now }, "\"etag\"");
        }

        private static readonly Agentstration.Resources.WorkspaceId WorkspaceId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
