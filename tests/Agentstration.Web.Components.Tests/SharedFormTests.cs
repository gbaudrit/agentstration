using System.ComponentModel.DataAnnotations;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class SharedFormTests
{
    [TestMethod]
    public async Task RequiredNumberAndEnumClearToInvalidInputWithoutSubstitutingDefaults()
    {
        using var context = Context();
        var model = new RequiredChoices { Number = 7, Choice = DayOfWeek.Friday };
        var editContext = new EditContext(model);
        var invalid = false;
        var cut = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<FormGrid>(grid => grid
                .AddChildContent<FormDraftMonitor>(monitor => monitor.Add(value => value.InvalidInputChanged, value => invalid = value))
                .AddChildContent<ClearableNumber<int>>(number => number.Add(value => value.Value, model.Number)
                    .Add(value => value.ValueChanged, value => model.Number = value).Add(value => value.ValueExpression, () => model.Number))
                .AddChildContent<ClearableSelect<DayOfWeek>>(choice => choice.Add(value => value.Value, model.Choice)
                    .Add(value => value.ValueChanged, value => model.Choice = value).Add(value => value.ValueExpression, () => model.Choice)
                    .AddChildContent("<option value='Sunday'>Sunday</option><option value='Friday'>Friday</option>"))));
        var buttons = cut.FindAll("button");
        await buttons[0].ClickAsync(new());
        await buttons[1].ClickAsync(new());
        Assert.AreEqual(7, model.Number, "Invalid input must not silently become zero.");
        Assert.AreEqual(DayOfWeek.Friday, model.Choice, "Invalid input must not select the first enum option.");
        Assert.AreEqual(string.Empty, cut.Find("input").GetAttribute("value"));
        Assert.AreEqual(string.Empty, cut.Find("select").GetAttribute("value"));
        Assert.HasCount(2, editContext.GetValidationMessages().ToArray());
        Assert.IsTrue(invalid, "An invalid draft must stay protected even while the typed model retains its last valid value.");
    }

    [TestMethod]
    public async Task ClearingRequiredTextNotifiesValidationAndReturnsFocusWithoutSubmitting()
    {
        using var context = Context();
        var model = new TextModel { Name = "Initial" };
        var editContext = new EditContext(model);
        var validation = editContext.EnableDataAnnotationsValidation(context.Services);
        var notifications = 0;
        editContext.OnFieldChanged += (_, _) => notifications++;
        var cut = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<ClearableText>(input => input
                .Add(value => value.Value, model.Name)
                .Add(value => value.ValueChanged, (string? value) => model.Name = value ?? string.Empty)
                .Add(value => value.ValueExpression, () => model.Name)));

        Assert.AreEqual("button", cut.Find("button").GetAttribute("type"));
        await cut.Find("button").ClickAsync(new());

        Assert.AreEqual(string.Empty, model.Name);
        Assert.AreEqual(1, notifications);
        Assert.IsTrue(editContext.GetValidationMessages().Any());
        Assert.AreEqual(0, cut.FindAll("button").Count);
        Assert.IsTrue(context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"));
        await cut.InvokeAsync(validation.Dispose);
    }

    [TestMethod]
    public async Task ClearingNullableNumberKeepsAbsenceDistinctFromZero()
    {
        using var context = Context();
        int? number = 0;
        var cut = context.Render<ClearableNumber<int?>>(parameters => parameters
            .Add(value => value.Value, number)
            .Add(value => value.ValueChanged, (int? value) => number = value)
            .Add(value => value.ValueExpression, () => number));

        await cut.Find("button").ClickAsync(new());

        Assert.IsNull(number);
        Assert.AreEqual(string.Empty, cut.Find("input").GetAttribute("value") ?? string.Empty);
    }

    [TestMethod]
    public async Task ClearingSelectionDoesNotSelectTheFirstOption()
    {
        using var context = Context();
        string? value = "second";
        var cut = context.Render<ClearableSelect<string?>>(parameters => parameters
            .Add(input => input.Value, value)
            .Add(input => input.ValueChanged, (string? updated) => value = updated)
            .Add(input => input.ValueExpression, () => value)
            .AddChildContent("<option value=''>Choose</option><option value='first'>First</option><option value='second'>Second</option>"));

        await cut.Find("button").ClickAsync(new());

        Assert.AreEqual(string.Empty, value);
    }

    [TestMethod]
    public void ReadOnlyAndDisabledFieldsCannotBeCleared()
    {
        using var context = Context();
        var cut = context.Render<ClearableText>(parameters => parameters.Add(input => input.Value, "Stored").Add(input => input.ReadOnly, true));
        Assert.AreEqual(0, cut.FindAll("button").Count);
        cut.Render(parameters => parameters.Add(input => input.ReadOnly, false).Add(input => input.Disabled, true));
        Assert.AreEqual(0, cut.FindAll("button").Count);
    }

    [TestMethod]
    public void FieldLabelsAndHelpReferenceTheirOwnControl()
    {
        using var context = Context();
        var cut = context.Render<FormGrid>(parameters => parameters
            .AddChildContent<FormField>(field => field.Add(value => value.Label, "First").Add(value => value.Help, "Help").AddChildContent<ClearableText>())
            .AddChildContent<FormField>(field => field.Add(value => value.Label, "Second").Add(value => value.Help, "Other help").AddChildContent<ClearableText>()));
        var inputs = cut.FindAll("input");
        Assert.AreNotEqual(inputs[0].Id, inputs[1].Id);
        foreach (var input in inputs)
        {
            Assert.IsNotNull(cut.Find($"label[for='{input.Id}']"));
            Assert.IsNotNull(cut.Find($"#{input.GetAttribute("aria-describedby")}"));
        }
    }

    [TestMethod]
    public void UnknownTotalIsNotPresentedAsAnExactCount()
    {
        using var context = Context();
        var cut = context.Render<CollectionPager>(parameters => parameters.Add(value => value.Page, 2).Add(value => value.HasMore, true));
        Assert.AreEqual(0, cut.FindAll("[role='status']").Count);
        Assert.IsFalse(cut.FindAll("button").Any(button => button.HasAttribute("disabled")));
        cut.Render(parameters => parameters.Add(value => value.Busy, true));
        Assert.IsTrue(cut.FindAll("button").All(button => button.HasAttribute("disabled")));
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js").Mode = JSRuntimeMode.Loose;
        return context;
    }

    [TestMethod]
    public async Task DirtyNavigationRequiresAnExplicitDiscardAndCancelKeepsTheDraft()
    {
        using var context = Context();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var initial = navigation.Uri;
        var cut = context.Render<UnsavedNavigationGuard>(parameters => parameters.Add(value => value.IsDirty, true));
        await cut.InvokeAsync(() => navigation.NavigateTo("/agents"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[role='alertdialog']")));
        Assert.AreEqual(initial, navigation.Uri);
        await cut.Find(".button-secondary").ClickAsync(new());
        Assert.AreEqual(initial, navigation.Uri);
        Assert.IsEmpty(cut.FindAll("[role='alertdialog']"));
        await cut.InvokeAsync(() => navigation.NavigateTo("/agents"));
        await cut.Find(".button-danger").ClickAsync(new());
        Assert.IsTrue(navigation.Uri.EndsWith("/agents", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ResourceTabsExposeOneTabStopAndKeyboardSelection()
    {
        using var context = Context();
        string? selected = null;
        var cut = context.Render<ResourcePageTabs>(parameters => parameters
            .Add(value => value.Items, new[] { new ResourcePageTab("overview", "Overview"), new ResourcePageTab("edit", "Edit") })
            .Add(value => value.Value, "overview")
            .Add(value => value.ValueChanged, value => selected = value)
            .Add(value => value.Label, "Resource")
            .Add(value => value.PanelId, "panel")
            .Add(value => value.IdPrefix, "tabs"));
        Assert.HasCount(1, cut.FindAll("[role='tab'][tabindex='0']"));
        Assert.AreEqual("panel", cut.Find("[aria-selected='true']").GetAttribute("aria-controls"));
        await cut.Find("#tabs-overview").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "End" });
        Assert.AreEqual("edit", selected);
        Assert.IsTrue(context.JSInterop.Invocations.Any(call => call.Identifier == "Blazor._internal.domWrapper.focus"));
    }

    [TestMethod]
    public async Task ViewQueryChangesPreserveTheDraftWhileOwnershipChangesRequireConfirmation()
    {
        using var context = Context();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/modelprofiles/shared?scopeRef=%2Finstance&tab=edit");
        var cut = context.Render<UnsavedNavigationGuard>(parameters => parameters
            .Add(value => value.IsDirty, true).Add(value => value.ViewQueryParameters, new[] { "tab" }));
        await cut.InvokeAsync(() => navigation.NavigateTo("/modelprofiles/shared?scopeRef=%2Finstance&tab=usages"));
        Assert.IsTrue(navigation.Uri.EndsWith("tab=usages", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("[role='alertdialog']"));
        await cut.InvokeAsync(() => navigation.NavigateTo("/modelprofiles/shared?scopeRef=%2Ftenants%2Fother&tab=edit"));
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("[role='alertdialog']")));
        Assert.IsTrue(navigation.Uri.EndsWith("tab=usages", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CompletedSaveCanNavigateBeforeTheNextParentRender()
    {
        using var context = Context();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/new");
        var dirty = true;
        var cut = context.Render<UnsavedNavigationGuard>(parameters => parameters
            .Add(value => value.IsDirty, true).Add(value => value.HasUnsavedChanges, () => dirty));
        await cut.InvokeAsync(() =>
        {
            dirty = false;
            navigation.NavigateTo("/agents/saved?scopeRef=%2Finstance");
        });
        Assert.IsTrue(navigation.Uri.Contains("/agents/saved?", StringComparison.Ordinal));
        Assert.IsEmpty(cut.FindAll("[role='alertdialog']"));
    }

    private sealed class TextModel
    {
        [Required] public string Name { get; set; } = string.Empty;
    }
    private sealed class RequiredChoices
    {
        public int Number { get; set; }
        public DayOfWeek Choice { get; set; }
    }
}
