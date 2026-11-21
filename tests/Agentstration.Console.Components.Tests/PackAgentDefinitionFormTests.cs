using Agentstration.ResourceManagement;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PackAgentDefinitionFormTests
{
    private const string Source = """
        apiVersion: agentstration.io/v1
        kind: Agent
        metadata:
          name: welcome
          tags:
            role: entrypoint
        definition:
          displayName: Welcome
          description: Greets users.
          handler: prompt-agent
          instructions: Help the user.
          modelProfile:
            binding: agent-model
          runtimeProfile:
            binding: local-runtime
          tools: []
          behaviors:
            - routing
          middleware: []
          contextProviders: []
          settings: {}
        """;

    [TestMethod]
    public async Task EditableFormUpdatesTheOriginalPackManifestWithoutResolvingBindings()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var rendered = context.Render<PackAgentDefinitionForm>(parameters => parameters.Add(component => component.Source, Source));

        await rendered.FindAll("input")[0].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Forked welcome" });

        Assert.IsTrue(rendered.Instance.TryBuildSource(out var updated, out var error), error);
        Assert.IsTrue(updated.Contains("displayName: Forked welcome", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains("binding: agent-model", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains("binding: local-runtime", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("shared.models", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OptInDraftTrackingRetainsInvalidInputBeforeBlurAndAcknowledgesOnlyExplicitly()
    {
        using var context = new BunitContext().WithCollectionPresentation(); context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var dirty = false; var rendered = context.Render<PackAgentDefinitionForm>(p => p.Add(value => value.Source, Source).Add(value => value.DraftChanged, value => dirty = value));
        Assert.IsFalse(dirty); Assert.IsFalse(rendered.Instance.IsDirty);
        rendered.FindAll("textarea")[^1].Input("{ invalid settings");
        Assert.IsTrue(dirty); Assert.IsTrue(rendered.Instance.IsDirty); Assert.IsFalse(rendered.Instance.TryBuildSource(out _, out var error)); Assert.IsFalse(string.IsNullOrWhiteSpace(error));
        await rendered.InvokeAsync(() => rendered.Instance.AcknowledgeAsync(Source)); Assert.IsFalse(dirty); Assert.IsFalse(rendered.Instance.IsDirty);
        Assert.IsTrue(rendered.Instance.TryBuildSource(out _, out error), error);
    }

    [TestMethod]
    public void EditingIdentityKeepsUnchangedScopedAndExtendedReferencesExactly()
    {
        using var context = new BunitContext().WithCollectionPresentation(); context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var source = Source.Replace("binding: agent-model", "name: profile\n    namespace: shared\n    scopeRef: /instance\n    retainedEvidence: model-pin", StringComparison.Ordinal)
            .Replace("binding: local-runtime", "name: runtime\n    scopeRef: /instance\n    retainedEvidence: runtime-pin", StringComparison.Ordinal)
            .Replace("tools: []", "tools: [{name: utility, namespace: shared, scopeRef: /instance, retainedEvidence: tool-pin}]", StringComparison.Ordinal);
        var rendered = context.Render<PackAgentDefinitionForm>(p => p.Add(value => value.Source, source)); rendered.FindAll("input")[0].Change("Changed identity");
        Assert.IsTrue(rendered.Instance.TryBuildSource(out var updated, out var error), error);
        var before = System.Text.Json.JsonSerializer.SerializeToElement(ResourceManifestSerializer.FromYaml<System.Text.Json.JsonElement>(source));
        var after = ResourceManifestSerializer.FromYaml<System.Text.Json.JsonElement>(updated);
        foreach (var name in new[] { "modelProfile", "runtimeProfile", "tools" })
            Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(before.GetProperty("definition").GetProperty(name).GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(after.GetProperty("definition").GetProperty(name).GetRawText())), name);
    }

    [TestMethod]
    public void ReadOnlyFormDisablesEveryDefinitionControl()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var rendered = context.Render<PackAgentDefinitionForm>(parameters => parameters
            .Add(component => component.Source, Source)
            .Add(component => component.ReadOnly, true));

        Assert.HasCount(0, rendered.FindAll("input:not([disabled]), textarea:not([disabled])"));
    }
}
