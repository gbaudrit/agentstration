using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.State;
using Bunit;
using Bunit.JSInterop;
using Blazor.Diagrams.Core.Geometry;
using Microsoft.Extensions.DependencyInjection;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
public sealed class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void ReadOnlyModeDisablesMutatingActions()
    {
        using var context = new BunitContext();
        var backend = new BackendStub();
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var @namespace = new ResourceNamespace("pack.sample");
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, @namespace));

        Assert.IsFalse(rendered.FindAll("button").Any(button => button.TextContent.Trim() is "Save" or "Publish" or "Run draft"));
        Assert.AreEqual(0, backend.SaveCount);
        StringAssert.Contains(rendered.Markup, "Read only");
        StringAssert.Contains(rendered.Markup, "pack.sample");
        StringAssert.Contains(rendered.Markup, "Published version 2.1.0");
        Assert.AreEqual(@namespace, backend.LoadedTarget?.Namespace);

        rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Definition").Click();
        var applyYaml = rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Apply valid YAML");
        Assert.IsTrue(applyYaml.HasAttribute("disabled"));
    }

    private sealed class ResourceProviderStub : IFlowDesignerResourceProvider
    {
        public Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerAgent>>([]);
    }

    private sealed class BackendStub : IFlowDesignerBackend
    {
        private readonly FlowDraftResponse draft = CreateDraft();
        public int SaveCount { get; private set; }
        public FlowDesignerTarget? LoadedTarget { get; private set; }
        public Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            LoadedTarget = target;
            var value = draft.Value;
            return Task.FromResult(new FlowDesignerLoadResult(new(value.FlowId, value.DisplayName, value.Description, value.Tags, value.Definition), "entryStep: input", PublishedVersion: "2.1.0"));
        }
        public Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) { SaveCount++; return Task.FromResult(draft); }
        public Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static FlowDraftResponse CreateDraft()
        {
            var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
            return new(new FlowDraft { Id = "draft", FlowId = new("sample"), DisplayName = "Sample", Definition = definition, CreatedAt = now, UpdatedAt = now }, "\"etag\"");
        }
    }
}
