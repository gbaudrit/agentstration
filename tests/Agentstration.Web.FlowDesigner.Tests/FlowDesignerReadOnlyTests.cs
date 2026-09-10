using System.Globalization;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Resources;
using Agentstration.Web.FlowDesigner.Backend;
using Agentstration.Web.FlowDesigner.Components;
using Agentstration.Web.FlowDesigner.State;
using AngleSharp.Dom;
using Blazor.Diagrams.Core.Geometry;
using Bunit;
using Bunit.JSInterop;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using FlowDesignerComponent = Agentstration.Web.FlowDesigner.Components.FlowDesigner;

namespace Agentstration.Web.FlowDesigner.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void ReadOnlyModeDisablesMutatingActions()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext();
        var backend = new BackendStub();
        context.Services.AddSingleton<IFlowDesignerBackend>(backend);
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
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

    [TestMethod]
    public void ReadOnlyModeUsesTheSelectedFrenchCulture()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = new BunitContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub());
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "sample")
            .Add(component => component.Namespace, new ResourceNamespace("pack.sample")));

        StringAssert.Contains(rendered.Markup, "Lecture seule");
        StringAssert.Contains(rendered.Markup, "Version publiée 2.1.0");
        StringAssert.Contains(rendered.Markup, "Ajuster");

        var strings = context.Services.GetRequiredService<IStringLocalizer<FlowDesignerStrings>>();
        Assert.AreEqual("Appliquer le YAML valide", strings["ApplyValidYaml"].Value);
        Assert.AreEqual("2 agents disponibles. Les routes sont explicites et restent des références immuables après publication.", strings["AvailableAgents.Many", 2].Value);
    }

    [TestMethod]
    public void EditablePaletteUsesOneGenericFlowCardAndShowsTheSelectedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Flow", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Flow");
            StringAssert.Contains(rendered.Markup, "article");
            StringAssert.Contains(rendered.Markup, "summary");
        });

        rendered.Find("[data-testid='flow-input-source']").Change("input");
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.String, call.InputMapping?.ValueKind);
        Assert.AreEqual("${input}", call.InputMapping?.GetString());
        StringAssert.Contains(rendered.Markup, "Initial Flow input");

        rendered.Find("[data-testid='flow-input-source']").Change(string.Empty);
        call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Type() == "flow"));
        Assert.AreEqual(JsonValueKind.Object, call.InputMapping?.ValueKind);
        rendered.WaitForAssertion(() => StringAssert.Contains(rendered.Markup, "article"));
    }

    [TestMethod]
    public void EditablePaletteUsesOneGenericToolCardAndShowsTheSelectedSchema()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext();
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        IElement[] cards = [];
        rendered.WaitForAssertion(() =>
        {
            cards = rendered.FindAll(".step-palette button").Where(button => button.TextContent.Trim().EndsWith("Tool", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, cards);
        });
        cards[0].Click();
        rendered.WaitForAssertion(() =>
        {
            StringAssert.Contains(rendered.Markup, "Called Tool");
            StringAssert.Contains(rendered.Markup, "message");
            StringAssert.Contains(rendered.Markup, "deliveryId");
        });
    }

    [TestMethod]
    public async Task ExistingFlowCardLoadsItsContractWhenSelectedAndPersistsPassthrough()
    {
        using var culture = new CultureScope("en-US");
        using var context = new BunitContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition { Name = "analyze", DisplayName = "Analyze news", Agent = new("news-agent") },
                new FlowCallStepDefinition
                {
                    Name = "deliver",
                    Flow = new("analysis", Namespace: new("pack.news")),
                    InputMapping = JsonSerializer.SerializeToElement(new { })
                }
            ],
            Transitions =
            [
                new("input-analyze", "input", "completed", "analyze"),
                new("analyze-deliver", "analyze", "completed", "deliver")
            ]
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true)
            .SetResult(new Rectangle(0, 0, 1024, 768));

        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters
            .Add(component => component.ResourceId, "parent"));
        var canvas = rendered.FindComponent<FlowCanvas>();
        await rendered.InvokeAsync(() => canvas.Instance.SelectedStepChanged.InvokeAsync("deliver"));

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid='flow-input-source']")));
        rendered.Find("[data-testid='flow-input-source']").Change("transition");
        var call = Assert.IsInstanceOfType<FlowCallStepDefinition>(context.Services.GetRequiredService<FlowEditorStore>()
            .State.Resource!.Definition.Steps.Single(step => step.Name == "deliver"));
        Assert.AreEqual("${transition.output}", call.InputMapping?.GetString());
    }

    private sealed class ResourceProviderStub : IFlowDesignerResourceProvider
    {
        public Task<IReadOnlyList<FlowDesignerAgent>> GetAgentsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerAgent>>([]);
        public Task<IReadOnlyList<FlowDesignerFlow>> GetFlowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerFlow>>([new("analysis", "News analysis", new("pack.news"), "2.0.0")]);
        public Task<IReadOnlyList<FlowDesignerFlowVersion>> GetFlowVersionsAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerFlowVersion>>([new("2.0.0",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { article = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { summary = new { type = "string" } } }))]);
        public Task<IReadOnlyList<FlowDesignerTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FlowDesignerTool>>([new(
                "notification.send",
                "Send notification",
                ResourceNamespace.Default,
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } } }),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { deliveryId = new { type = "string" } } }),
                Enabled: true,
                Available: true,
                RequiresApproval: false)]);
    }

    private sealed class BackendStub : IFlowDesignerBackend
    {
        private readonly bool readOnly;
        private readonly FlowDraftResponse draft;
        public BackendStub(bool readOnly = true, FlowGraphDefinition? definition = null)
        {
            this.readOnly = readOnly;
            draft = CreateDraft(definition);
        }
        public int SaveCount { get; private set; }
        public FlowDesignerTarget? LoadedTarget { get; private set; }
        public Task<FlowDesignerLoadResult> LoadAsync(FlowDesignerTarget target, CancellationToken cancellationToken)
        {
            LoadedTarget = target;
            var value = draft.Value;
            return Task.FromResult(new FlowDesignerLoadResult(new(value.FlowId, value.DisplayName, value.Description, value.Tags, value.Definition), "entryStep: input", ETag: readOnly ? null : draft.ETag, PublishedVersion: readOnly ? "2.1.0" : null));
        }
        public Task<FlowSourceResponse> GetSourceAsync(FlowDesignerTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowDraftResponse> SaveDraftAsync(FlowDesignerTarget target, UpdateFlowDraftRequest request, string etag, CancellationToken cancellationToken) { SaveCount++; return Task.FromResult(draft); }
        public Task<FlowDraftResponse> ReplaceSourceAsync(FlowDesignerTarget target, ReplaceFlowSourceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowValidationResponse> ValidateAsync(FlowDesignerTarget target, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowVersionResponse> PublishAsync(FlowDesignerTarget target, PublishFlowDraftRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FlowRun> RunDraftAsync(FlowDesignerTarget target, CreateFlowRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        private static FlowDraftResponse CreateDraft(FlowGraphDefinition? definition = null)
        {
            var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
            definition ??= new FlowGraphDefinition { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input" }], Transitions = [] };
            return new(new FlowDraft { WorkspaceId = WorkspaceId, Id = "draft", FlowId = new("sample"), DisplayName = "Sample", Definition = definition, CreatedAt = now, UpdatedAt = now }, "\"etag\"");
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
