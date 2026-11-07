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

public sealed partial class FlowDesignerReadOnlyTests
{
    [TestMethod]
    public void DesignerWaitsForToolContractsBeforeShowingTheCanvas()
    {
        using var context = CreateContext();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resources = new ResourceProviderStub { PendingTools = pending.Task };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(resources);
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        try
        {
            rendered.WaitForAssertion(() => Assert.AreEqual(TestScope, resources.ToolContextScope));
            Assert.HasCount(0, rendered.FindComponents<FlowCanvas>());
        }
        finally
        {
            pending.SetResult();
        }

        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindComponents<FlowCanvas>()));
    }

    [TestMethod]
    public async Task FlowCallCanReplaceAnExistingParentInputContractWithItsSharedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { request = new { type = "string" } } });
        var reference = new SchemaVersionReference
        {
            Name = "analysis-request",
            Version = "1.2.0",
            Namespace = new("pack.news"),
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition
                {
                    Name = "input",
                    SchemaBinding = new SchemaBinding
                    {
                        Inline = new InlineSchemaBinding
                        {
                            Format = SchemaFormatIds.JsonSchema,
                            Dialect = "https://json-schema.org/draft/2020-12/schema",
                            Content = JsonSerializer.SerializeToElement(new { type = "string" })
                        }
                    }
                },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0", new("pack.news")),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            SchemaVersions =
            [
                new("analysis-request", "Analysis request", new("pack.news"), reference.ScopeRef!.Value,
                    reference.Version, "json-schema", "https://json-schema.org/draft/2020-12/schema", schema)
            ],
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["pack.news|analysis"] = [new("1.0.0", schema, null, InputSchemaReference: reference)]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-child", "input", "completed", "child")));
        var connectionDialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(connectionDialog.TextContent, "Use the child Flow input contract?");
        StringAssert.Contains(connectionDialog.TextContent, "The current input contract will be replaced.");
        StringAssert.Contains(connectionDialog.TextContent, "Reusable shared contract");
        StringAssert.Contains(connectionDialog.TextContent, "pack.news/analysis-request · 1.2.0");
        CollectionAssert.Contains(connectionDialog.ParentElement!.ClassList.ToArray(), "connection-contract-backdrop");
        CollectionAssert.Contains(rendered.Find("[data-testid='connection-contract-apply']").ClassList.ToArray(), "button-primary");
        rendered.Find("[data-testid='connection-contract-apply']").Click();

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();
        Assert.HasCount(0, rendered.FindAll(".step-contract-source"));
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-shared-input-contract']").TextContent, "pack.news/analysis-request · 1.2.0");

        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.AreEqual(reference, input.SchemaBinding?.Reference);
        Assert.IsNull(input.Schema);
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='use-called-flow-shared-input']"));
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-shared-input-contract']").TextContent, "Parent input already configured");

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("input"));
        rendered.Find("[data-testid='step-settings-tab-output']").Click();
        StringAssert.Contains(rendered.Find("[data-testid='input-output-contract']").TextContent, "Same as input contract");
        Assert.AreEqual("string", rendered.FindComponent<SchemaDocumentEditor>().Instance.Value
            .GetProperty("properties").GetProperty("request").GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task FlowCallCanCopyAnInlineInputContractIndependently()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new FlowCallStepDefinition
                {
                    Name = "child",
                    Flow = new("analysis", FlowCallVersionStrategy.Exact, "1.0.0"),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            FlowVersions = new Dictionary<string, IReadOnlyList<FlowDesignerFlowVersion>>(StringComparer.Ordinal)
            {
                ["default|analysis"] = [new("1.0.0", schema, null)]
            }
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-child", "input", "completed", "child")));
        var connectionDialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(connectionDialog.TextContent, "Independent inline copy");
        StringAssert.Contains(connectionDialog.TextContent, "make it shared manually in the child Flow");
        rendered.Find("[data-testid='connection-contract-later']").Click();
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.StepSettingsRequested.InvokeAsync("child"));
        rendered.Find("[data-testid='step-settings-tab-input']").Click();

        var inlineContract = rendered.Find("[data-testid='flow-call-inline-input-contract']");
        StringAssert.Contains(inlineContract.TextContent, "later changes will not be synchronized");
        StringAssert.Contains(inlineContract.TextContent, "make it shared manually in the child Flow");
        CollectionAssert.Contains(rendered.Find("[data-testid='copy-called-flow-inline-input']").ClassList.ToArray(), "button-secondary");
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='use-called-flow-shared-input']"));

        rendered.Find("[data-testid='copy-called-flow-inline-input']").Click();

        var input = (InputFlowStepDefinition)context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .Single(step => step.Name == "input");
        Assert.IsNotNull(input.SchemaBinding?.Inline);
        Assert.IsNull(input.SchemaBinding.Reference);
        Assert.AreEqual(SchemaFormatIds.JsonSchema, input.SchemaBinding.Inline.Format);
        Assert.AreEqual("object", input.SchemaBinding.Inline.Content.GetProperty("type").GetString());
        StringAssert.Contains(rendered.Find("[data-testid='flow-call-inline-input-contract']").TextContent, "Parent input already configured");
        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='copy-called-flow-inline-input']"));
    }

    [TestMethod]
    public async Task AgentConnectionCanReplaceTheFlowInputWithItsSharedContract()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { prompt = new { type = "string" } } });
        var reference = new SchemaVersionReference
        {
            Name = "agent-request",
            Version = "2.1.0",
            Namespace = new("pack.agents"),
            ScopeRef = ResourceScopeRef.Workspace(Guid.Parse("11111111-1111-1111-1111-111111111111"))
        };
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", Schema = JsonSerializer.SerializeToElement(new { type = "string" }) },
                new AgentFlowStepDefinition
                {
                    Name = "writer",
                    Agent = new("writer", Namespace: new("pack.agents")),
                    InputMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new FlowDesignerAgent("writer", "Editorial writer", TestScope)
                {
                    Namespace = new("pack.agents"),
                    InputSchema = schema,
                    InputSchemaReference = reference
                }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-writer", "input", "completed", "writer")));

        var dialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(dialog.TextContent, "Use the Agent input contract?");
        StringAssert.Contains(dialog.TextContent, "Editorial writer");
        StringAssert.Contains(dialog.TextContent, "The current input contract will be replaced.");
        StringAssert.Contains(dialog.TextContent, "pack.agents/agent-request · 2.1.0");
        rendered.Find("[data-testid='connection-contract-apply']").Click();

        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.AreEqual(reference, input.SchemaBinding?.Reference);
        Assert.IsNull(input.Schema);
    }

    [TestMethod]
    public async Task ToolConnectionCanDeferAnIndependentInlineContractCopy()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new ToolFlowStepDefinition
                {
                    Name = "notify",
                    Tool = new("notification.send"),
                    ArgumentsMapping = JsonSerializer.SerializeToElement("${transition.output}")
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub());
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-notify", "input", "completed", "notify")));

        var dialog = rendered.Find("[data-testid='connection-contract-dialog']");
        StringAssert.Contains(dialog.TextContent, "Use the Tool input contract?");
        StringAssert.Contains(dialog.TextContent, "Send notification");
        StringAssert.Contains(dialog.TextContent, "later changes will not be synchronized");
        rendered.Find("[data-testid='connection-contract-later']").Click();

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
        var input = context.Services.GetRequiredService<FlowEditorStore>().State.Resource!.Definition.Steps
            .OfType<InputFlowStepDefinition>().Single();
        Assert.IsNull(input.Schema);
        Assert.IsNull(input.SchemaBinding);
    }

    [TestMethod]
    public async Task AgentConnectionDoesNotPromptWhenItsMappingIsNotAPassthrough()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var definition = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input" },
                new AgentFlowStepDefinition
                {
                    Name = "writer",
                    Agent = new("writer"),
                    InputMapping = JsonSerializer.SerializeToElement(new { prompt = "${transition.output.prompt}" })
                }
            ],
            Transitions = []
        };
        context.Services.AddSingleton<IFlowDesignerBackend>(new BackendStub(readOnly: false, definition));
        context.Services.AddSingleton<IFlowDesignerResourceProvider>(new ResourceProviderStub
        {
            Agents =
            [
                new FlowDesignerAgent("writer", "Writer", TestScope)
                {
                    InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
                }
            ]
        });
        context.Services.AddSingleton<FlowEditorStore>();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1024, 768));
        var rendered = context.Render<FlowDesignerComponent>(parameters => parameters.Add(component => component.ResourceId, "parent"));

        await rendered.InvokeAsync(() => rendered.FindComponent<FlowCanvas>().Instance.TransitionCreated.InvokeAsync(
            new FlowTransitionDefinition("input-writer", "input", "completed", "writer")));

        Assert.ThrowsExactly<ElementNotFoundException>(() => rendered.Find("[data-testid='connection-contract-dialog']"));
    }
}
