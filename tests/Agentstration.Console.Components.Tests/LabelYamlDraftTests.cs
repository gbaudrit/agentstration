using Agentstration.ResourceManagement;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class LabelYamlDraftTests
{
    [TestMethod]
    public async Task IncompleteLabelsNeverReplaceTheSerializableAgentDefinition()
    {
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var model = new AgentEditorModel
        {
            Name = "welcome", DisplayName = "Welcome", Instructions = "Hello",
            ModelProfileName = "default", RuntimeProfileName = "maf-builtin", Tags = "environment=production"
        };
        var edit = new EditContext(model);
        var cut = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, edit)
            .AddChildContent<LabelsEditor>(labels => labels
                .Add(value => value.Value, model.Tags)
                .Add(value => value.ValueChanged, value => model.Tags = value)
                .Add(value => value.ValueExpression, () => model.Tags)));
        await cut.Find("[data-testid='label-add']").ClickAsync(new());
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        cut.FindAll("[data-testid='label-key']")[1].Input("team");
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        var request = model.ToRequest();
        Assert.HasCount(1, request.Metadata.Tags);
        Assert.AreEqual("production", request.Metadata.Tags["environment"]);
        StringAssert.Contains(ResourceManifestSerializer.ToYaml(request), "production");
        cut.FindAll("[data-testid='label-value']")[1].Input("support");
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        Assert.AreEqual("support", model.ToRequest().Metadata.Tags["team"]);
        await cut.FindAll(".label-remove")[1].ClickAsync(new());
        Assert.HasCount(1, model.ToRequest().Metadata.Tags);
    }
}
