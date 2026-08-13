using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
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

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.IsReadOnly, true));

        var save = rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Save");
        var publish = rendered.FindAll("button").Single(button => button.TextContent.Trim() == "Publish");
        Assert.IsTrue(save.HasAttribute("disabled"));
        Assert.IsTrue(publish.HasAttribute("disabled"));
        Assert.AreEqual(0, backend.SaveCount);
        StringAssert.Contains(rendered.Markup, "Read only");
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
        public Task<FlowDraftResponse> GetDraftAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(draft);
        public Task<FlowSourceResponse> GetSourceAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(new FlowSourceResponse("entryStep: input", "yaml", 1));
        public Task<FlowDraftResponse> SaveDraftAsync(string resourceId, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) { SaveCount++; return Task.FromResult(draft); }
        public Task<FlowDraftResponse> ReplaceSourceAsync(string resourceId, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateAsync(string resourceId, CancellationToken cancellationToken) => Task.FromResult(new FlowValidationResponse(true, []));
        public Task<FlowVersionResponse> PublishAsync(string resourceId, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> RunDraftAsync(string resourceId, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static FlowDraftResponse CreateDraft()
        {
            var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            var definition = new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
            return new(new FlowDraft { Id = "draft", FlowId = new("sample"), DisplayName = "Sample", Definition = definition, CreatedAt = now, UpdatedAt = now }, "\"etag\"");
        }
    }
}
