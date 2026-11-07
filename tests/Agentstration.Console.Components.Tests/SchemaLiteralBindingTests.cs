using System.Text.Json;
using Agentstration.Web.Components.JsonSchema;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Agentstration.Web.Tests;

public sealed partial class ModelProviderNavigationTests
{
    [TestMethod]
    [DataRow("{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}}},\"required\":[\"items\"]}", "{\"items\":[{\"name\":\"first\"}]}")]
    [DataRow("{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}}", "[[1,2],[3]]")]
    public void SchemaLiteralPreservesNestedObjectsAndArrays(string schema, string json)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, schema, value => selected = value);
        Assert.IsFalse(editContext.Validate());

        EnterSchemaJson(rendered, json);

        Assert.IsNotNull(selected);
        Assert.IsTrue(selected.IsConfigured);
        Assert.IsNull(selected.Resource);
        Assert.IsTrue(JsonElement.DeepEquals(ParseLiteral(json), selected.Literal!.Value));
        Assert.IsTrue(editContext.Validate());
        rendered.Find("[data-testid='schema-form-mode']").Click();
        Assert.HasCount(0, rendered.FindAll("textarea"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SchemaLiteralDistinguishesMissingEmptyAndNull(bool required)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":["string","null"]}""", value => selected = value, required: required);
        Assert.IsNull(rendered.FindComponent<ValueBindingRequirementEditor>().Instance.Selection.Literal);
        Assert.AreEqual(!required, editContext.Validate());

        EnterSchemaJson(rendered, "\"\"");
        Assert.IsTrue(selected!.IsConfigured);
        Assert.AreEqual(JsonValueKind.String, selected.Literal!.Value.ValueKind);
        Assert.AreEqual(string.Empty, selected.Literal.Value.GetString());
        Assert.IsTrue(editContext.Validate());

        rendered.Find("textarea").Input("null");
        Assert.IsTrue(selected.IsConfigured);
        Assert.AreEqual(JsonValueKind.Null, selected.Literal!.Value.ValueKind);
        Assert.IsTrue(editContext.Validate());

        rendered.Find("textarea").Input(string.Empty);
        Assert.IsFalse(selected.IsConfigured);
        Assert.IsNull(selected.Literal);
        Assert.AreEqual(!required, editContext.Validate());
    }

    [TestMethod]
    [DataRow("{\"type\":\"string\",\"minLength\":2}", "\"\"", "\"ok\"")]
    [DataRow("{\"type\":\"integer\",\"minimum\":1,\"maximum\":3}", "0", "2")]
    [DataRow("{\"type\":\"string\",\"enum\":[\"red\",\"blue\"]}", "\"green\"", "\"red\"")]
    [DataRow("{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":1,\"maxItems\":2}", "[]", "[1]")]
    [DataRow("{\"type\":[\"integer\",\"null\"],\"minimum\":1}", "\"bad\"", "null")]
    [DataRow("{\"anyOf\":[{\"type\":\"string\",\"minLength\":2},{\"type\":\"null\"}]}", "\"x\"", "null")]
    [DataRow("{\"oneOf\":[{\"type\":\"integer\"},{\"type\":\"number\"}]}", "1", "1.5")]
    [DataRow("{\"type\":\"string\"}", "null", "\"ok\"")]
    public void SchemaLiteralReusesConstraintValidation(string schema, string invalid, string valid)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var rendered = RenderSchemaLiteral(context, editContext, schema, _ => { });

        EnterSchemaJson(rendered, invalid);
        Assert.IsFalse(editContext.Validate());
        Assert.IsTrue(rendered.FindAll("[role='alert']").Count > 0);
        rendered.Find("textarea").Input(valid);
        Assert.IsTrue(editContext.Validate());
    }

    [TestMethod]
    public void InvalidRawJsonBlocksSavingWithoutReplacingTheLastLiteral()
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":"array","items":{"type":"integer"}}""", value => selected = value);
        EnterSchemaJson(rendered, "[1]");
        var previous = selected;
        rendered.Find("textarea").Input("[broken");
        Assert.AreEqual(previous, selected);
        Assert.IsFalse(editContext.Validate());
        rendered.Find("textarea").Input("[2]");
        Assert.IsTrue(editContext.Validate());
        Assert.AreEqual(2, selected!.Literal!.Value[0].GetInt32());
    }

    [TestMethod]
    [DataRow("Parameter")]
    [DataRow("Secret")]
    [DataRow("Caller")]
    public void SchemaLiteralSourceChangesClearThePayload(string source)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":"object","properties":{"count":{"type":"integer"}}}""", value => selected = value);
        EnterSchemaJson(rendered, "{\"count\":2}");
        rendered.Find("[data-testid='value-binding-binding-kind']").Change(source);
        Assert.AreEqual(Enum.Parse<ValueBindingSourceKind>(source), selected!.Kind);
        Assert.IsNull(selected.Literal);
        Assert.IsNull(selected.Resource);
        Assert.HasCount(0, rendered.FindAll("[data-testid='schema-input-editor']"));
        rendered.Find("[data-testid='value-binding-binding-kind']").Change("Literal");
        Assert.IsNull(selected.Literal);
        Assert.IsFalse(selected.IsConfigured);
        Assert.IsFalse(editContext.Validate());
    }

    [TestMethod]
    public void ANewLiteralSchemaRevalidatesTheExistingValue()
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":"string","minLength":1}""", _ => { }, initial: ParseLiteral("\"ok\""));
        Assert.IsTrue(editContext.Validate());
        rendered.FindComponent<ValueBindingRequirementEditor>().Render(parameters => parameters.Add(value => value.Requirement,
            new("payload", true, "string", LiteralSchema: ParseLiteral("""{"type":"string","minLength":3}"""))));
        Assert.IsFalse(editContext.Validate());
    }

    [TestMethod]
    public void SchemaLiteralDisabledAndSecuredStatesPreserveSourceRestrictions()
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var changes = 0;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":["string","null"]}""", _ => changes++, disabled: true);
        var input = rendered.FindComponent<JsonSchemaInputEditor>();
        Assert.IsTrue(input.Instance.Disabled);
        rendered.Find("[data-testid='value-binding-binding-kind']").Change("Caller");
        Assert.AreEqual(0, changes);
        rendered.FindComponent<ValueBindingRequirementEditor>().Render(parameters => parameters.Add(value => value.Requirement,
            new("payload", true, "string", Secured: true, LiteralSchema: ParseLiteral("""{"type":["string","null"]}"""))));
        Assert.HasCount(0, rendered.FindAll("[data-testid='schema-input-editor']"));
        Assert.IsFalse(editContext.Validate());
    }

    [TestMethod]
    [DataRow("en-US", "Set value", "Clear value")]
    [DataRow("fr-FR", "Définir une valeur", "Effacer la valeur")]
    public void SchemaLiteralPresenceControlsAreLocalized(string culture, string set, string clear)
    {
        using var cultureScope = new TestCultureScope(culture);
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":"null"}""", value => selected = value);
        rendered.FindAll("button").Single(button => button.TextContent == set).Click();
        Assert.AreEqual(JsonValueKind.Null, selected!.Literal!.Value.ValueKind);
        Assert.IsTrue(editContext.Validate());
        rendered.FindAll("button").Single(button => button.TextContent == clear).Click();
        Assert.IsNull(selected.Literal);
        Assert.IsFalse(editContext.Validate());
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("null")]
    public void SchemaLessScalarRequirementsRejectStructuredAndNullValues(string json)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var rendered = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<ValueBindingRequirementEditor>(editor => editor
                .Add(value => value.Requirement, new("endpoint", true, "string"))
                .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
                .Add(value => value.Selection, new(ValueBindingSourceKind.Literal, Literal: ParseLiteral(json)))));
        Assert.IsFalse(editContext.Validate());
        Assert.HasCount(0, rendered.FindAll("[data-testid='schema-input-editor']"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UndefinedElementsRemainUnconfiguredWithoutRenderingErrors(bool withSchema)
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var rendered = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<ValueBindingRequirementEditor>(editor => editor
                .Add(value => value.Requirement, new("text", true, "string", LiteralSchema: withSchema ? ParseLiteral("""{"type":"string"}""") : null))
                .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
                .Add(value => value.Selection, new(ValueBindingSourceKind.Literal, Literal: default(JsonElement)))));
        Assert.IsFalse(editContext.Validate());
        Assert.IsFalse(rendered.FindComponent<ValueBindingRequirementEditor>().Instance.Selection.IsConfigured);
    }

    [TestMethod]
    public void SchemaLiteralEnumCanSelectAnExplicitEmptyString()
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        ValueBindingSelection? selected = null;
        var rendered = RenderSchemaLiteral(context, editContext, """{"type":"string","enum":["","ok"]}""", value => selected = value);
        rendered.Find("[data-testid='schema-input-editor'] select").Change("\"\"");
        Assert.IsTrue(selected!.IsConfigured);
        Assert.AreEqual(string.Empty, selected.Literal!.Value.GetString());
        Assert.IsTrue(editContext.Validate());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SchemaLessEmptyStringAndExplicitClearRemainDistinct(bool allowedValues)
    {
        using var context = CreateContext(out _);
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("text", true, "string", AllowedValues: allowedValues ? [ParseLiteral("\"\""), ParseLiteral("\"ok\"")] : null))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Literal))
            .Add(value => value.SelectionChanged, value => selected = value));
        rendered.Find("[data-testid='value-binding-binding-literal']").Change(string.Empty);
        Assert.IsTrue(selected!.IsConfigured);
        Assert.AreEqual(string.Empty, selected.Literal!.Value.GetString());
        rendered.Find("[data-testid='value-binding-binding-literal-clear']").Click();
        Assert.IsFalse(selected.IsConfigured);
        Assert.IsNull(selected.Literal);
    }

    private static IRenderedComponent<CascadingValue<EditContext>> RenderSchemaLiteral(
        BunitContext context, EditContext editContext, string schema, Action<ValueBindingSelection> changed,
        JsonElement? initial = null, bool required = true, bool disabled = false) => context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<ValueBindingRequirementEditor>(editor => editor
                .Add(value => value.Requirement, new("payload", required, "object", LiteralSchema: ParseLiteral(schema)))
                .Add(value => value.AllowedSources, Enum.GetValues<ValueBindingSourceKind>())
                .Add(value => value.Selection, new(ValueBindingSourceKind.Literal, Literal: initial))
                .Add(value => value.Disabled, disabled)
                .Add(value => value.SelectionChanged, changed)));

    private static void EnterSchemaJson(IRenderedComponent<CascadingValue<EditContext>> rendered, string json)
    {
        rendered.Find("[data-testid='schema-raw-mode']").Click();
        rendered.Find("textarea").Input(json);
    }

    private static JsonElement ParseLiteral(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
