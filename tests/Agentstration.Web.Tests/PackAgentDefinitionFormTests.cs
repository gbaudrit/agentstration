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
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var rendered = context.Render<PackAgentDefinitionForm>(parameters => parameters.Add(component => component.Source, Source));

        await rendered.FindAll("input")[2].ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "Forked welcome" });

        Assert.IsTrue(rendered.Instance.TryBuildSource(out var updated, out var error), error);
        Assert.IsTrue(updated.Contains("displayName: Forked welcome", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains("binding: agent-model", StringComparison.Ordinal));
        Assert.IsTrue(updated.Contains("binding: local-runtime", StringComparison.Ordinal));
        Assert.IsFalse(updated.Contains("shared.models", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReadOnlyFormDisablesEveryDefinitionControl()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var rendered = context.Render<PackAgentDefinitionForm>(parameters => parameters
            .Add(component => component.Source, Source)
            .Add(component => component.ReadOnly, true));

        Assert.HasCount(0, rendered.FindAll("input:not([disabled]), textarea:not([disabled])"));
    }
}
