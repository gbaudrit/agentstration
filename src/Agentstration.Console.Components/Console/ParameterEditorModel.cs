using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;

namespace Agentstration.Web.Console;

public sealed class ParameterEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ParameterValueType ValueType { get; set; } = ParameterValueType.Text;
    [Required] public string Value { get; set; } = string.Empty;
    public List<DescendantUseGrant> UseGrants { get; set; } = [];

    public ParameterProperties Properties() => new()
    {
        DisplayName = DisplayName.Trim(),
        Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        ValueType = ValueType,
        Value = ParseValue(),
        UsePolicy = new() { Grants = UseGrants.ToArray() }
    };

    public static ParameterEditorModel From(ParameterResource resource) => new()
    {
        Name = resource.Name,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        ValueType = resource.Definition.ValueType,
        Value = DisplayValue(resource.Definition.Value),
        UseGrants = resource.Definition.UsePolicy.Grants.ToList()
    };

    private JsonElement ParseValue() => ValueType switch
    {
        ParameterValueType.Text => JsonSerializer.SerializeToElement(Value),
        ParameterValueType.WholeNumber when long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
            JsonSerializer.SerializeToElement(integer),
        ParameterValueType.DecimalNumber when decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) =>
            JsonSerializer.SerializeToElement(number),
        ParameterValueType.Logical when bool.TryParse(Value, out var logical) => JsonSerializer.SerializeToElement(logical),
        _ => throw new ArgumentException($"The Parameter value is not a valid {ValueType.ToString().ToLowerInvariant()} value.")
    };

    private static string DisplayValue(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : value.GetRawText();
}
