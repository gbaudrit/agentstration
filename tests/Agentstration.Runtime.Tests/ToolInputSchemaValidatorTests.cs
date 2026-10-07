using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class ToolInputSchemaValidatorTests
{
    private static JsonElement CreateSchema() => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            prompt = new { type = "string", minLength = 1, maxLength = 16_000 },
            reference = new { type = "string", pattern = "^[A-Z]{3}[0-9]{4}$" },
            callback = new { type = "string", format = "uri" }
        },
        required = new[] { "prompt" },
        additionalProperties = false
    });

    [TestMethod]
    public void OptionalConstrainedFieldsMayBeOmitted()
    {
        ToolInputSchemaValidator.Validate(CreateSchema(), JsonSerializer.SerializeToElement(new { prompt = "hello" }));
    }

    [TestMethod]
    public void PresentFieldsMustRespectPatternAndFormat()
    {
        var pattern = Assert.ThrowsExactly<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(
            CreateSchema(),
            JsonSerializer.SerializeToElement(new { prompt = "hello", reference = "invalid" })));
        Assert.AreEqual("tool_argument_pattern_invalid", pattern.Code);
        Assert.AreEqual("$.reference", pattern.Path);

        var format = Assert.ThrowsExactly<ToolInputValidationException>(() => ToolInputSchemaValidator.Validate(
            CreateSchema(),
            JsonSerializer.SerializeToElement(new { prompt = "hello", callback = "/relative" })));
        Assert.AreEqual("tool_argument_format_invalid", format.Code);
        Assert.AreEqual("$.callback", format.Path);
    }

    [TestMethod]
    public void UnknownAndMissingFieldsAreRejected()
    {
        Assert.AreEqual("tool_argument_required", Assert.ThrowsExactly<ToolInputValidationException>(() =>
            ToolInputSchemaValidator.Validate(CreateSchema(), JsonSerializer.SerializeToElement(new { }))).Code);
        Assert.AreEqual("tool_argument_unknown", Assert.ThrowsExactly<ToolInputValidationException>(() =>
            ToolInputSchemaValidator.Validate(CreateSchema(), JsonSerializer.SerializeToElement(new { prompt = "hello", extra = true }))).Code);
    }
}
