using System.Text.Json;
using Agentstration.Web.Components.JsonSchema;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class JsonSchemaAnyValueEditorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NonObjectRootsRequireExplicitOptIn(bool enabled)
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"array","items":{"type":"integer"}}"""))
            .Add(value => value.Value, Json("[1]"))
            .Add(value => value.AllowAnyRoot, enabled)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.AreEqual(enabled ? JsonValueKind.Array : JsonValueKind.Object, state!.Value!.Value.ValueKind);
        Assert.AreEqual(!enabled, state.UsesRawFallback);
        Assert.HasCount(enabled ? 1 : 0, rendered.FindAll("input[type='number']"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MissingValuesAreNotMaterializedFromDefaultsUntilExplicitlySet(bool required)
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var notifications = 0;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"object","properties":{"count":{"type":"integer","default":2}}}"""))
            .Add(value => value.Value, default(JsonElement))
            .Add(value => value.Required, required)
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValueChanged, _ => notifications++)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.IsNull(state!.Value);
        Assert.AreEqual(!required, state.IsValid);
        Assert.AreEqual(0, notifications);
        rendered.Find("[data-schema-path='$'] > button").Click();
        Assert.AreEqual(2, state.Value!.Value.GetProperty("count").GetInt32());
        Assert.IsTrue(state.IsValid);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void OptionalEmptyStringsAndNullsHaveSeparateRemovalActions()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"object","properties":{"name":{"type":"string"},"note":{"type":["string","null"]}}}"""))
            .Add(value => value.Value, Json("""{"name":"old","note":null}"""))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        rendered.Find("[data-schema-path='$.name'] input").Input(string.Empty);
        Assert.AreEqual(string.Empty, state!.Value!.Value.GetProperty("name").GetString());
        Assert.AreEqual(JsonValueKind.Null, state.Value.Value.GetProperty("note").ValueKind);
        rendered.Find("[data-schema-path='$.note'] > button").Click();
        Assert.IsFalse(state.Value.Value.TryGetProperty("note", out _));
        rendered.Find("[data-schema-path='$.note'] > label select").Change("1");
        Assert.AreEqual(JsonValueKind.Null, state.Value.Value.GetProperty("note").ValueKind);
        Assert.AreEqual(string.Empty, state.Value.Value.GetProperty("name").GetString());
    }

    [TestMethod]
    public void NullableUnionFormCanSelectEditAndRemoveBranches()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":["integer","null"],"minimum":1}"""))
            .Add(value => value.Value, default(JsonElement))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        var type = rendered.Find("select");
        type.Change("0");
        Assert.IsFalse(state!.IsValid);
        StringAssert.Contains(rendered.Find("input[type='number']").ParentElement!.TextContent, "JSON");
        rendered.Find("input[type='number']").Input("2");
        Assert.IsTrue(state.IsValid);
        Assert.AreEqual(2, state.Value!.Value.GetInt32());
        rendered.Find("select").Change("1");
        Assert.AreEqual(JsonValueKind.Null, state.Value!.Value.ValueKind);
        Assert.IsTrue(state.IsValid);
        rendered.Find("select").Change("-1");
        Assert.IsNull(state.Value);
        Assert.IsFalse(state.IsValid);
    }

    [TestMethod]
    public void NestedArrayConstraintsUseIndexedFieldPaths()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"array","items":{"type":"object","properties":{"name":{"type":"string","minLength":2}},"required":["name"]}}"""))
            .Add(value => value.Value, Json("""[{"name":"a"},{"name":"ok"}]"""))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.IsFalse(state!.IsValid);
        Assert.IsTrue(state.Issues.Any(issue => issue.Path == "$[0].name" && issue.Code == "MinimumLength"));
        Assert.HasCount(1, rendered.FindAll("[data-schema-path='$[0].name'] .field-validation-error"));
        rendered.Find("[data-schema-path='$[0].name'] input").Input("fixed");
        Assert.IsTrue(state.IsValid);
        Assert.AreEqual("fixed", state.Value!.Value[0].GetProperty("name").GetString());
        Assert.AreEqual("ok", state.Value.Value[1].GetProperty("name").GetString());
    }

    [TestMethod]
    public void UnsupportedSchemaFallbackStillPreservesAnyJsonRoot()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"allOf":[{"type":"array"}]}"""))
            .Add(value => value.Value, Json("[1]"))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.IsTrue(state!.UsesRawFallback);
        Assert.AreEqual(JsonValueKind.Array, state.Value!.Value.ValueKind);
        rendered.Find("textarea").Input("null");
        Assert.AreEqual(JsonValueKind.Null, state.Value!.Value.ValueKind);
        Assert.IsTrue(state.IsValid);
        Assert.HasCount(1, rendered.FindAll("[role='status']"));
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void DisabledOrReadOnlyEditorsIgnoreSyntheticMutations(bool disabled, bool readOnly)
    {
        using var context = Context();
        var changes = 0;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"string"}"""))
            .Add(value => value.Value, Json("\"\""))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.Disabled, disabled)
            .Add(value => value.ReadOnly, readOnly)
            .Add(value => value.ValueChanged, _ => changes++));
        rendered.Find("input").Input("attempt");
        rendered.Find("[data-schema-path='$'] > button").Click();
        Assert.AreEqual(0, changes);
        if (!disabled)
        {
            rendered.Find("[data-testid='schema-raw-mode']").Click();
            rendered.Find("textarea").Input("null");
            Assert.AreEqual(0, changes);
        }
    }

    [TestMethod]
    public void FreeFormArrayFallbackStillValidatesKnownConstraints()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"array","maxItems":1}"""))
            .Add(value => value.Value, Json("[1,2]"))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.IsTrue(state!.UsesRawFallback);
        Assert.IsFalse(state.IsValid);
        Assert.IsTrue(state.Issues.Any(issue => issue.Code == "MaximumItems"));
        rendered.Find("textarea").Input("[null]");
        Assert.IsTrue(state.IsValid);
        Assert.AreEqual(JsonValueKind.Null, state.Value!.Value[0].ValueKind);
    }

    [TestMethod]
    public void ArrayFormRenderingRetainsTheExistingBound()
    {
        using var context = Context();
        JsonSchemaInputValidationState? state = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(value => value.Schema, Json("""{"type":"array","items":{"type":"integer"}}"""))
            .Add(value => value.Value, JsonSerializer.SerializeToElement(Enumerable.Range(0, 101)))
            .Add(value => value.AllowAnyRoot, true)
            .Add(value => value.ValidationChanged, value => state = value));
        Assert.IsFalse(state!.IsValid);
        Assert.IsTrue(state.Issues.Any(issue => issue.Code == "TooManyItems"));
        Assert.HasCount(100, rendered.FindAll("input[type='number']"));
        Assert.AreEqual(101, state.Value!.Value.GetArrayLength());
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
