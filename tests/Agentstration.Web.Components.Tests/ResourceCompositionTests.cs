using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceCompositionTests
{
    [TestMethod]
    public async Task HeaderCopiesTheQualifiedIdentityAndKeepsUnqualifiedCallersCompatible()
    {
        using var context = Context();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
        var copy = module.SetupVoid("copyText", "support/welcome-agent");
        copy.SetVoidResult();
        var cut = context.Render<ResourceHeader>(parameters => parameters
            .Add(value => value.Name, "Welcome agent")
            .Add(value => value.ResourceType, "Agent")
            .Add(value => value.TechnicalName, "welcome-agent")
            .Add(value => value.Namespace, "support"));
        Assert.AreEqual("support/welcome-agent", cut.Find(".resource-header-technical code").TextContent);
        StringAssert.Contains(cut.Find(".copy-value").GetAttribute("title")!, "support/welcome-agent");
        await cut.Find(".copy-value").ClickAsync(new());
        Assert.HasCount(1, copy.Invocations);
        cut.Render(parameters => parameters.Add(value => value.Namespace, (string?)null));
        Assert.AreEqual("welcome-agent", cut.Find(".resource-header-technical code").TextContent);
        var unqualifiedCopy = module.SetupVoid("copyText", "welcome-agent");
        unqualifiedCopy.SetVoidResult();
        await cut.Find(".copy-value").ClickAsync(new());
        Assert.HasCount(1, unqualifiedCopy.Invocations);
    }

    [TestMethod]
    public async Task LabelRowsValidateDuplicatesAndIncompleteValuesWithoutLosingTheDraft()
    {
        using var context = Context();
        var model = new LabelsModel { Labels = "environment=production" };
        var edit = new EditContext(model);
        var cut = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, edit)
            .AddChildContent<LabelsEditor>(labels => labels
                .Add(value => value.Value, model.Labels)
                .Add(value => value.ValueChanged, value => model.Labels = value)
                .Add(value => value.ValueExpression, () => model.Labels)));
        await cut.Find("[data-testid='label-add']").ClickAsync(new());
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        Assert.AreEqual("environment=production", model.Labels);
        Assert.IsFalse(edit.IsModified());
        var keys = cut.FindAll("[data-testid='label-key']");
        keys[1].Input("environment");
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        Assert.AreEqual("environment=production", model.Labels, "An incomplete row must not corrupt the resource binding.");
        cut.FindAll("[data-testid='label-value']")[1].Input("staging");
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        Assert.HasCount(2, cut.FindAll(".label-row-error"));
        Assert.AreEqual("environment=production", model.Labels, "Duplicate keys must remain a local draft.");
        keys[1].Input("team");
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        StringAssert.Contains(model.Labels, "team=staging");
        await cut.FindAll(".label-remove")[1].ClickAsync(new());
        Assert.AreEqual("environment=production", model.Labels);
        Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        Assert.IsTrue(edit.IsModified());
    }

    [TestMethod]
    public async Task ImageFailureFallsBackAndANewResourceImageCanLoad()
    {
        using var context = Context();
        var cut = context.Render<ResourceIcon>(parameters => parameters
            .Add(value => value.Source, "/images/service.svg")
            .Add(value => value.DarkSource, "/images/service-dark.svg")
            .Add(value => value.IconName, "layers"));
        Assert.HasCount(2, cut.FindAll("img"));
        Assert.IsTrue(cut.FindAll("img").All(image => image.GetAttribute("alt") == string.Empty));
        await cut.Find("img").TriggerEventAsync("onerror", new Microsoft.AspNetCore.Components.Web.ErrorEventArgs());
        Assert.IsEmpty(cut.FindAll("img"));
        Assert.HasCount(1, cut.FindAll("svg"));
        cut.Render(parameters => parameters.Add(value => value.Source, "/images/other.svg"));
        Assert.HasCount(2, cut.FindAll("img"));
        cut.Render(parameters => parameters.Add(value => value.Source, "javascript:alert(1)"));
        Assert.IsEmpty(cut.FindAll("img"));
    }

    [TestMethod]
    public async Task IdentityKeepsMissingDescriptionExplicitAndCopiesOnlyTheRequestedValue()
    {
        using var context = Context();
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-ui.js");
        var copy = module.SetupVoid("copyText", "welcome-agent");
        copy.SetVoidResult();
        var cut = context.Render<ResourceIdentitySummary>(parameters => parameters
            .Add(value => value.DisplayName, "Welcome agent")
            .Add(value => value.TechnicalName, "welcome-agent")
            .Add(value => value.Namespace, "default"));
        StringAssert.Contains(cut.Markup, "Not provided");
        await cut.Find("button").ClickAsync(new());
        Assert.HasCount(1, copy.Invocations);
        Assert.AreEqual("Copied", cut.Find("[role='status']").TextContent);
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
    private sealed class LabelsModel { public string Labels { get; set; } = string.Empty; }
}
