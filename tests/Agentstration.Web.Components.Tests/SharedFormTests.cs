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
        return context;
    }

    private sealed class TextModel
    {
        [Required] public string Name { get; set; } = string.Empty;
    }
}
