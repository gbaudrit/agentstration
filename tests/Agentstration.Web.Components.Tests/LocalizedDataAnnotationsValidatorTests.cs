using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class LocalizedDataAnnotationsValidatorTests
{
    [TestMethod]
    [DataRow("en-US", "Identifier is required.")]
    [DataRow("fr-FR", "Le champ Identifiant est obligatoire.")]
    public async Task InlineValidationUsesLocalizedLabelsAndPreservesOtherMessageStores(string culture, string expected)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var model = new Sample();
            var edit = new EditContext(model);
            var external = new ValidationMessageStore(edit);
            external.Add(edit.Field(nameof(Sample.Email)), "Existing parser feedback");
            var cut = context.Render<CascadingValue<EditContext>>(p => p.Add(c => c.Value, edit)
                .AddChildContent<FormGrid>(grid => grid
                    .AddChildContent<LocalizedDataAnnotationsValidator>(validator => validator.Add(c => c.FieldLabels, Labels(culture)))
                    .AddChildContent<FormField>(field => field.Add(c => c.Label, Labels(culture)[nameof(Sample.Code)])
                        .Add(c => c.Validation, builder =>
                        {
                            builder.OpenComponent<ValidationMessage<string>>(0);
                            builder.AddAttribute(1, "For", (System.Linq.Expressions.Expression<Func<string>>)(() => model.Code));
                            builder.CloseComponent();
                        })
                        .AddChildContent<ClearableText>(input => input.Add(c => c.Value, model.Code)
                            .Add(c => c.ValueChanged, value => model.Code = value ?? string.Empty).Add(c => c.ValueExpression, () => model.Code)))));
            cut.Find("input").Input(string.Empty);
            Assert.AreEqual(expected, cut.Find(".form-field-messages .validation-message").TextContent);
            Assert.HasCount(1, edit.GetValidationMessages(edit.Field(nameof(Sample.Code))).ToArray(), "Required must short-circuit other rules on an empty field.");
            Assert.AreEqual(cut.Find(".form-field-messages").Id, cut.Find("input").GetAttribute("aria-describedby"));
            Assert.Contains("Existing parser feedback", edit.GetValidationMessages().ToArray());
            cut.Find("input").Input("valid");
            Assert.IsEmpty(edit.GetValidationMessages(edit.Field(nameof(Sample.Code))).ToArray());
            Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
            external.Clear();
            Assert.IsTrue(await cut.InvokeAsync(edit.Validate));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task ModelRulesRetainTheirLimitsAndUseLocalizedFieldNames(string culture)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var context = Context();
            var model = new Sample { Code = "bad!", Count = 20, Title = "far too long", Email = "invalid", Confirmation = "different" };
            var edit = new EditContext(model);
            var cut = Render(context, edit, Labels(culture));
            Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
            var expected = culture == "fr-FR"
                ? new[] { "Le format du champ Identifiant n’est pas valide.", "Le champ Nombre doit être compris entre 1 et 9.", "Le champ Titre doit contenir entre 2 et 6 caractères.", "Le champ Courriel doit contenir une adresse e-mail valide.", "Le champ Confirmation doit correspondre au champ Identifiant." }
                : new[] { "Identifier has an invalid format.", "Count must be between 1 and 9.", "Title must contain between 2 and 6 characters.", "Email must contain a valid email address.", "Confirmation must match Identifier." };
            CollectionAssert.AreEquivalent(expected, edit.GetValidationMessages().ToArray());
            model.Code = model.Confirmation = "valid"; model.Count = 3; model.Title = "Title"; model.Email = "a@example.test";
            await cut.InvokeAsync(() => edit.NotifyFieldChanged(edit.Field(nameof(Sample.Code))));
            Assert.IsEmpty(edit.GetValidationMessages().ToArray());
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public async Task LabelsCanChangeWithoutLosingValidationAndReplacedContextsAreDetached()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            using var context = Context();
            var first = new EditContext(new Sample { Code = string.Empty });
            var cut = Render(context, first, Labels("en-US"));
            await cut.InvokeAsync(() => first.NotifyFieldChanged(first.Field(nameof(Sample.Code))));
            Assert.Contains("Identifier is required.", first.GetValidationMessages().ToArray());
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            cut.Render(p => p.Add(c => c.Value, first).AddChildContent<LocalizedDataAnnotationsValidator>(validator => validator.Add(c => c.FieldLabels, Labels("fr-FR"))));
            Assert.Contains("Le champ Identifiant est obligatoire.", first.GetValidationMessages().ToArray());
            var second = new EditContext(new Sample());
            cut.Render(p => p.Add(c => c.Value, second));
            Assert.IsEmpty(first.GetValidationMessages().ToArray());
            Assert.IsTrue(await cut.InvokeAsync(first.Validate));
            Assert.IsTrue(await cut.InvokeAsync(second.Validate));
            cut.Dispose();
            ((Sample)second.Model).Code = string.Empty;
            Assert.IsTrue(second.Validate(), "A disposed validator must not remain subscribed.");
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    public async Task ExplicitAndObjectLevelMessagesKeepTheirMeaning()
    {
        using var context = Context();
        var model = new CustomModel();
        var edit = new EditContext(model);
        var cut = Render(context, edit, new Dictionary<string, string> { [nameof(CustomModel.Value)] = "Value" });
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        CollectionAssert.AreEqual(new[] { "Explicit guidance" }, edit.GetValidationMessages().ToArray());
        model.Value = "reserved";
        Assert.IsFalse(await cut.InvokeAsync(edit.Validate));
        CollectionAssert.AreEqual(new[] { "Reserved value" }, edit.GetValidationMessages(edit.Field(nameof(CustomModel.Value))).ToArray());
    }

    private static IRenderedComponent<CascadingValue<EditContext>> Render(BunitContext context, EditContext edit, IReadOnlyDictionary<string, string> labels) =>
        context.Render<CascadingValue<EditContext>>(p => p.Add(c => c.Value, edit)
            .AddChildContent<LocalizedDataAnnotationsValidator>(validator => validator.Add(c => c.FieldLabels, labels)));

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static IReadOnlyDictionary<string, string> Labels(string culture) => new Dictionary<string, string>
    {
        [nameof(Sample.Code)] = culture == "fr-FR" ? "Identifiant" : "Identifier",
        [nameof(Sample.Count)] = culture == "fr-FR" ? "Nombre" : "Count",
        [nameof(Sample.Title)] = culture == "fr-FR" ? "Titre" : "Title",
        [nameof(Sample.Email)] = culture == "fr-FR" ? "Courriel" : "Email",
        [nameof(Sample.Confirmation)] = "Confirmation"
    };

    public sealed class Sample
    {
        [Required, RegularExpression("^[a-z]+$")] public string Code { get; set; } = "valid";
        [Range(1, 9)] public int Count { get; set; } = 3;
        [StringLength(6, MinimumLength = 2)] public string Title { get; set; } = "Title";
        [EmailAddress] public string Email { get; set; } = "a@example.test";
        [Compare(nameof(Code))] public string Confirmation { get; set; } = "valid";
    }

    public sealed class CustomModel : IValidatableObject
    {
        [Required(ErrorMessage = "Explicit guidance")] public string Value { get; set; } = string.Empty;
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
            Value == "reserved" ? [new("Reserved value", [nameof(Value)])] : [];
    }
}
