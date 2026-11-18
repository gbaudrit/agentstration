using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Web.Components.Pages;
using Bunit;
using Microsoft.AspNetCore.Components.Web;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    public async Task AQueuedAddDuringCatalogRefreshCannotReadAPartialCatalogOrMutateTheSequence()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = ManifestValueSchema, parameters = new { type = "object" } }
        });
        var client = new FlowClientStub
        {
            SequenceGraph = new() { EntryStep = "input", InputSchema = input, OutputSchema = ManifestValueSchema }
        };
        var summaries = await client.GetFlowsAsync(default);
        using var context = CreateContext(new KnowledgeClientStub(), flows: client);
        IReadOnlyList<FlowCompositionCall> calls = [];
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.ValueChanged, value => calls = value));
        Assert.IsFalse(rendered.Find("[data-testid=flow-sequence-add]").HasAttribute("disabled"));
        var previousLoad = rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("data-load-revision");
        Assert.AreEqual("false", rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("aria-busy"));
        client.PendingListing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = rendered.Find("[data-testid=flow-sequence-refresh]").ClickAsync(new MouseEventArgs());
        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Find("[data-testid=flow-sequence-add]").HasAttribute("disabled")));

        Assert.AreEqual("true", rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("aria-busy"));
        Assert.AreEqual(previousLoad, rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("data-load-revision"));
        Exception? staleClickError = null;
        try
        {
            // A previously enabled browser control can have an event already in flight.
            await rendered.Find("[data-testid=flow-sequence-add]").ClickAsync(new MouseEventArgs());
        }
        catch (Exception exception) { staleClickError = exception; }
        finally
        {
            client.PendingListing.SetResult(summaries);
            await refresh;
        }

        Assert.AreEqual("false", rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("aria-busy"));
        Assert.AreNotEqual(previousLoad, rendered.Find("[data-testid=flow-sequence-editor]").GetAttribute("data-load-revision"));
        Assert.IsNull(staleClickError, "An in-flight Add must not index the cleared catalog during refresh.");
        Assert.IsEmpty(calls, "Loading must not mutate the edited sequence.");
        await rendered.Find("[data-testid=flow-sequence-add]").ClickAsync(new MouseEventArgs());
        Assert.HasCount(1, calls);
        Assert.AreEqual("artifact-transform-builtin", calls[0].Flow?.ResourceId);
    }

    [TestMethod]
    public async Task AQueuedAddCannotMutateAReadOnlySequence()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { value = ManifestValueSchema, parameters = new { type = "object" } }
        });
        var client = new FlowClientStub
        {
            SequenceGraph = new() { EntryStep = "input", InputSchema = input, OutputSchema = ManifestValueSchema }
        };
        using var context = CreateContext(new KnowledgeClientStub(), flows: client);
        var changes = 0;
        var rendered = context.Render<FlowSequenceEditor>(parameters => parameters
            .Add(value => value.ReadOnly, true).Add(value => value.ValueChanged, _ => changes++));
        await rendered.Find("[data-testid=flow-sequence-add]").ClickAsync(new MouseEventArgs());
        Assert.AreEqual(0, changes);
    }
}
