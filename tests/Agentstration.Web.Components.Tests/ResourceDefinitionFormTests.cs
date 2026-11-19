using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq.Expressions;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceDefinitionFormTests
{
    [TestMethod]
    [DataRow("en-US", "Display name is required.")]
    [DataRow("fr-FR", "Le champ Nom d’affichage est obligatoire.")]
    public async Task InvalidSubmissionUsesDeclaredSectionsAndFocusesAfterTheirRender(string culture, string error)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        try
        {
            using var context = Context();
            var focus = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-form.js")
                .Setup<bool>("focusFirstInvalid", _ => true);
            focus.SetResult(true);
            var model = new Sample();
            var edit = new EditContext(model);
            var state = new ResourceSectionState();
            state.Select("definition/configuration");
            var saved = 0;
            var invalidDraft = false;
            var cut = context.Render<ResourceDefinitionForm>(p => p.Add(c => c.Id, "resource-form").Add(c => c.EditContext, edit)
                .Add(c => c.FieldLabels, Labels(culture)).Add(c => c.SectionState, state).Add(c => c.Sections, Sections())
                .Add(c => c.SectionContent, path => Field(model, path == "definition/identity"))
                .Add(c => c.SectionRequested, path => state.Select(path)).Add(c => c.OnValidSubmit, _ => saved++)
                .Add(c => c.InvalidInputChanged, value => invalidDraft = value));
            var form = cut.FindComponent<EditForm>().Instance;
            Assert.IsEmpty(cut.FindAll("#Name"));
            await cut.Find("form").SubmitAsync();
            Assert.AreEqual("definition/identity", state.CurrentPath);
            Assert.AreEqual(error, cut.Find("#Name").Closest(".form-field")!.QuerySelector(".validation-message")!.TextContent);
            Assert.AreSame(form, cut.FindComponent<EditForm>().Instance);
            Assert.HasCount(1, focus.Invocations);
            Assert.AreEqual("resource-form", focus.Invocations.Single().Arguments[0]);
            Assert.AreEqual(cut.Find("form").GetAttribute("data-validation-context"), focus.Invocations.Single().Arguments[1]);
            Assert.AreEqual(0, saved);
            cut.Find("#Name").Input("Draft name");
            Assert.IsTrue(invalidDraft);
            await cut.Find("form").SubmitAsync();
            Assert.AreEqual("definition/configuration", state.CurrentPath);
            Assert.HasCount(2, focus.Invocations);
            Assert.AreEqual("Draft name", cut.Find("#Name").GetAttribute("value"));
            cut.Find("#Instructions").Input("Instructions");
            await cut.Find("form").SubmitAsync();
            Assert.AreEqual(1, saved);
            Assert.IsFalse(invalidDraft);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    public async Task LockedFormsCannotSubmitEvenWhenTheCallbackIsInvoked(bool busy, bool readOnly, bool disabled)
    {
        using var context = Context();
        var model = new Sample { Name = "Name", Instructions = "Instructions" };
        var saved = 0;
        var cut = context.Render<ResourceDefinitionForm>(p => p.Add(c => c.Id, "resource-form").Add(c => c.EditContext, new EditContext(model))
            .Add(c => c.FieldLabels, Labels("en-US")).Add(c => c.ChildContent, Field(model, true))
            .Add(c => c.Busy, busy).Add(c => c.ReadOnly, readOnly).Add(c => c.Disabled, disabled)
            .Add(c => c.OnValidSubmit, _ => saved++));
        Assert.IsTrue(cut.Find("fieldset").HasAttribute("disabled"));
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual(0, saved);
        cut.Render(p => p.Add(c => c.Busy, false).Add(c => c.ReadOnly, false).Add(c => c.Disabled, false));
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual(1, saved);
    }

    [TestMethod]
    public async Task ReadOnlySectionsKeepTheirFieldsAndExternalErrorsButCannotSubmit()
    {
        using var context = Context();
        var model = new Sample { Name = "Name", Instructions = "Instructions" };
        var edit = new EditContext(model);
        var state = new ResourceSectionState(); state.Select("definition/identity");
        var sections = Sections(); sections["definition/identity"] = sections["definition/identity"] with { ReadOnly = true };
        var saved = 0;
        var cut = context.Render<ResourceDefinitionForm>(p => p.Add(c => c.Id, "resource-form").Add(c => c.EditContext, edit)
            .Add(c => c.FieldLabels, Labels("en-US")).Add(c => c.SectionState, state).Add(c => c.Sections, sections)
            .Add(c => c.SectionContent, path => Field(model, path == "definition/identity"))
            .Add(c => c.OnValidSubmit, _ => saved++));
        Assert.IsTrue(cut.Find("[data-definition-section]").HasAttribute("disabled"));
        await cut.Find("form").SubmitAsync();
        Assert.AreEqual(0, saved);
        var external = new ValidationMessageStore(edit); external.Add(edit.Field(nameof(Sample.Name)), "External parser error");
        await cut.InvokeAsync(edit.NotifyValidationStateChanged);
        Assert.Contains("External parser error", cut.Markup);
        await cut.InvokeAsync(() => Assert.IsFalse(edit.Validate()));
        Assert.Contains("External parser error", edit.GetValidationMessages().ToArray());
    }

    [TestMethod]
    [DataRow("Missing")]
    [DataRow(nameof(Sample.Name))]
    public void InvalidOrAmbiguousFieldDeclarationsAreRejected(string field)
    {
        using var context = Context();
        var sections = Sections();
        sections["definition/configuration"] = new() { Fields = [field] };
        Assert.Throws<ArgumentException>(() => context.Render<ResourceDefinitionForm>(p => p.Add(c => c.Id, "resource-form")
            .Add(c => c.EditContext, new EditContext(new Sample())).Add(c => c.FieldLabels, Labels("en-US")).Add(c => c.Sections, sections)));
    }

    [TestMethod]
    public async Task ReplacingTheContextResetsFieldsAndIgnoresTheOldContextsChanges()
    {
        using var context = Context();
        var model = new Sample(); var old = new EditContext(model);
        var cut = context.Render<ResourceDefinitionForm>(p => p.Add(c => c.Id, "resource-form").Add(c => c.EditContext, old)
            .Add(c => c.FieldLabels, Labels("en-US")).Add(c => c.ChildContent, Field(model, true)));
        cut.Find("#Name").Input("Old draft");
        var previousToken = cut.Find("form").GetAttribute("data-validation-context");
        var replacement = new Sample { Name = "Saved name", Instructions = "Saved instructions" };
        var next = new EditContext(replacement);
        cut.Render(p => p.Add(c => c.EditContext, next).Add(c => c.ChildContent, Field(replacement, true)));
        Assert.AreNotEqual(previousToken, cut.Find("form").GetAttribute("data-validation-context"));
        await cut.InvokeAsync(() => old.NotifyFieldChanged(old.Field(nameof(Sample.Name))));
        Assert.AreSame(next, cut.FindComponent<EditForm>().Instance.EditContext);
        Assert.AreEqual("Saved name", cut.Find("#Name").GetAttribute("value"));
        Assert.IsEmpty(next.GetValidationMessages());
    }

    private static Dictionary<string, ResourceSectionPresentation> Sections() => new()
    {
        ["definition/identity"] = new() { Fields = [nameof(Sample.Name)] },
        ["definition/configuration"] = new() { Fields = [nameof(Sample.Instructions)] }
    };
    private static Dictionary<string, string> Labels(string culture) => new()
    {
        [nameof(Sample.Name)] = culture == "fr-FR" ? "Nom d’affichage" : "Display name",
        [nameof(Sample.Instructions)] = "Instructions"
    };
    private static RenderFragment Field(Sample model, bool identity) => builder =>
    {
        Expression<Func<string>> expression = identity ? () => model.Name : () => model.Instructions;
        var name = identity ? nameof(Sample.Name) : nameof(Sample.Instructions);
        builder.OpenComponent<FormField>(0); builder.AddAttribute(1, nameof(FormField.Label), name); builder.AddAttribute(2, nameof(FormField.Id), name);
        builder.AddAttribute(3, nameof(FormField.ChildContent), (RenderFragment)(child =>
        {
            child.OpenComponent<ClearableText>(0); child.AddAttribute(1, nameof(ClearableText.Value), identity ? model.Name : model.Instructions);
            child.AddAttribute(2, nameof(ClearableText.ValueExpression), expression);
            child.AddAttribute(3, nameof(ClearableText.ValueChanged), EventCallback.Factory.Create<string?>(model, value => { if (identity) model.Name = value ?? string.Empty; else model.Instructions = value ?? string.Empty; }));
            child.CloseComponent();
        }));
        builder.AddAttribute(4, nameof(FormField.Validation), (RenderFragment)(validation =>
        {
            validation.OpenComponent<ValidationMessage<string>>(0); validation.AddAttribute(1, nameof(ValidationMessage<string>.For), expression); validation.CloseComponent();
        }));
        builder.CloseComponent();
    };
    private static BunitContext Context()
    {
        var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
    public sealed class Sample
    {
        [Required] public string Name { get; set; } = string.Empty;
        [Required] public string Instructions { get; set; } = string.Empty;
    }
}
