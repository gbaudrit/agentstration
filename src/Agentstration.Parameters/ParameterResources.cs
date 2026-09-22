using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

namespace Agentstration.Parameters;

public static class ParameterResourceKinds
{
    public const string Parameter = "Parameter";
}

[JsonConverter(typeof(JsonStringEnumConverter<ParameterValueType>))]
public enum ParameterValueType
{
    [JsonStringEnumMemberName("string")] Text,
    [JsonStringEnumMemberName("integer")] WholeNumber,
    [JsonStringEnumMemberName("number")] DecimalNumber,
    [JsonStringEnumMemberName("boolean")] Logical
}

public sealed record ParameterProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required ParameterValueType ValueType { get; init; }
    public required JsonElement Value { get; init; }
    public DescendantUsePolicy UsePolicy { get; init; } = new();
}

public sealed record ParameterResource : Resource
{
    public ParameterProperties Definition { get; init; } = null!;
}

/// <summary>An exact scoped Parameter reference. Parameter names are never searched through ancestors.</summary>
public sealed record ParameterReference(ResourceAddress Address, ResourceScopeRef ScopeRef);
public sealed record ParameterResolutionContext(ResourceScopeRef ConsumerScopeRef, ResourceAddress Consumer);

public sealed record ResolvedParameter(ResourceAddress Parameter, JsonElement Value)
{
    public override string ToString() => "[REDACTED]";
}

public interface IParameterResolver
{
    Task<ResolvedParameter?> ResolveAsync(ParameterReference parameter, ParameterResolutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record ParameterUsage(string Kind, string Name, string DisplayName, string Url);

public interface IParameterUsageProvider
{
    Task<IReadOnlyList<ParameterUsage>> GetUsagesAsync(ScopedResourceAddress parameter,
        CancellationToken cancellationToken = default);
}

public class ParameterManagementException(string message) : Exception(message);
public sealed class ParameterResourceNotFoundException(string name) : Exception($"Parameter '{name}' was not found.");
public sealed class ParameterInUseException(string name) : Exception($"Parameter '{name}' is referenced by one or more managed resources.");
public sealed class ParameterAccessDeniedException(ResourceAddress parameter) : Exception($"Parameter '{parameter}' is outside the execution context.");
