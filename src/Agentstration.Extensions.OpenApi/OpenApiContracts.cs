using System.Text.Json;

namespace Agentstration.Extensions.OpenApi;

public sealed record OpenApiDiagnostic(string Path, string Code, string Message);

public sealed record OpenApiOperationDescriptor(
    string? OperationId, string Method, string Path, string? Summary, bool Eligible, bool ReadOnly,
    JsonElement? RequestShape, JsonElement? ResponseShape, IReadOnlyList<OpenApiDiagnostic> Diagnostics);

public sealed record OpenApiContractDescriptor(
    string Uri, string OpenApiVersion, string? Title, string? Description, string ContentType, string Content,
    string Sha256, IReadOnlyList<string> Servers, IReadOnlyList<OpenApiOperationDescriptor> Operations,
    IReadOnlyList<OpenApiDiagnostic> Diagnostics);

public sealed record OpenApiDiscoveryChoice(
    string Id, string DisplayName, string? Description, string? ParentId, bool Eligible, JsonElement Selector,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record OpenApiDiscoveryResult(
    string CorrelationId, IReadOnlyList<OpenApiContractDescriptor> Contracts,
    IReadOnlyList<OpenApiDiscoveryChoice> Choices, IReadOnlyList<OpenApiDiagnostic> Diagnostics);

public sealed record OpenApiOperationSelector(
    string ContractUri, string ContractDigest, string Server, string OperationId, string Method, string Path);

public sealed record OpenApiFixedBindings(
    IReadOnlyDictionary<string, string>? Path = null,
    IReadOnlyDictionary<string, string>? Query = null,
    IReadOnlyDictionary<string, string>? Header = null,
    JsonElement? Body = null);

public sealed record OpenApiInvocationResult(
    string CorrelationId, int StatusCode, string MediaType, string ContentBase64, long Length, string Sha256, int Pages);

public sealed class OpenApiExtensionException(string code, string message, Exception? inner = null)
    : Exception($"{code}: {message}", inner)
{
    public string Code { get; } = code;
}
