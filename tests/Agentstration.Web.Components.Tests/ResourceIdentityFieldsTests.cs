using Agentstration.Web.Components.Naming;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceIdentityFieldsTests
{
    [TestMethod]
    public async Task CreateModeDerivesUntilTechnicalNameIsEdited()
    {
        using var context = Context();
        var displayName = string.Empty;
        var technicalName = string.Empty;
        var rendered = context.Render<ResourceIdentityFields>(parameters => parameters
            .Add(value => value.DisplayName, displayName)
            .Add(value => value.DisplayNameChanged, (string value) => displayName = value)
            .Add(value => value.TechnicalName, technicalName)
            .Add(value => value.TechnicalNameChanged, (string value) => technicalName = value)
            .Add(value => value.Policy, ResourceNamePolicy.Trigger)
            .Add(value => value.IsNew, true)
            .Add(value => value.DisplayNameTestId, "display")
            .Add(value => value.TechnicalNameTestId, "technical"));

        await rendered.Find("[data-testid='display']").InputAsync(new ChangeEventArgs { Value = "Crème brûlée!" });
        Assert.AreEqual("creme-brulee", technicalName);

        await rendered.Find("[data-testid='technical']").InputAsync(new ChangeEventArgs { Value = string.Empty });
        await rendered.Find("[data-testid='display']").InputAsync(new ChangeEventArgs { Value = "Another name" });
        Assert.AreEqual(string.Empty, technicalName, "An intentional clear is a manual override.");
    }

    [TestMethod]
    public async Task ExplicitPrefillIsNeverRegenerated()
    {
        using var context = Context();
        var technicalName = "clone-copy";
        var rendered = context.Render<ResourceIdentityFields>(parameters => parameters
            .Add(value => value.DisplayName, "Clone")
            .Add(value => value.TechnicalName, technicalName)
            .Add(value => value.TechnicalNameChanged, (string value) => technicalName = value)
            .Add(value => value.IsNew, true)
            .Add(value => value.DisplayNameTestId, "display"));

        await rendered.Find("[data-testid='display']").InputAsync(new ChangeEventArgs { Value = "Changed clone" });

        Assert.AreEqual("clone-copy", technicalName);
    }

    [TestMethod]
    public async Task EditModeShowsDisplayFirstAndKeepsTechnicalNameReadOnly()
    {
        using var context = Context();
        var technicalName = "stable-id";
        var rendered = context.Render<ResourceIdentityFields>(parameters => parameters
            .Add(value => value.DisplayName, "Current display")
            .Add(value => value.TechnicalName, technicalName)
            .Add(value => value.TechnicalNameChanged, (string value) => technicalName = value)
            .Add(value => value.IsNew, false)
            .Add(value => value.DisplayNameTestId, "display")
            .Add(value => value.TechnicalNameTestId, "technical"));

        var labels = rendered.FindAll("label");
        Assert.IsNotNull(labels[0].QuerySelector("[data-testid='display']"));
        Assert.IsNotNull(labels[1].QuerySelector("[data-testid='technical']"));
        Assert.IsTrue(rendered.Find("[data-testid='technical']").HasAttribute("readonly"));
        Assert.IsTrue(rendered.Find("[data-testid='technical']").HasAttribute("disabled"));

        await rendered.Find("[data-testid='display']").InputAsync(new ChangeEventArgs { Value = "Renamed display" });
        Assert.AreEqual("stable-id", technicalName);
    }

    [TestMethod]
    public void CreateModeFocusesDisplayName()
    {
        using var context = Context();
        var rendered = context.Render<ResourceIdentityFields>(parameters => parameters
            .Add(value => value.DisplayName, string.Empty)
            .Add(value => value.TechnicalName, string.Empty)
            .Add(value => value.IsNew, true)
            .Add(value => value.DisplayNameTestId, "display"));

        Assert.IsTrue(rendered.Find("[data-testid='display']").HasAttribute("autofocus"));
    }

    [TestMethod]
    public void InitialDisplayNameDerivesAnEmptyTechnicalName()
    {
        using var context = Context();
        var technicalName = string.Empty;
        _ = context.Render<ResourceIdentityFields>(parameters => parameters
            .Add(value => value.DisplayName, "Initial résumé")
            .Add(value => value.TechnicalName, technicalName)
            .Add(value => value.TechnicalNameChanged, (string value) => technicalName = value)
            .Add(value => value.IsNew, true));

        Assert.AreEqual("initial-resume", technicalName);
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
}
