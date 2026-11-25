using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Schemas;

namespace Agentstration.ConnectedServices;

/// <summary>Validates administrative native-content adoption independently of the transport contract.</summary>
public static class OperationContentValidation
{
    public static async Task<SchemaFormatValidationResult> ValidateAsync(OperationContentContract? contract,
        JsonElement response, ISchemaFormatRegistry formats, CancellationToken ct)
    {
        if (contract is null) return SchemaFormatValidationResult.Valid;
        var content = response;
        if (contract.ContentPointer == "/content")
        {
            if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("content", out content)
                || !response.TryGetProperty("mediaType", out var media) || media.ValueKind != JsonValueKind.String
                || !string.Equals(media.GetString()?.Split(';')[0].Trim(), contract.MediaType, StringComparison.OrdinalIgnoreCase))
                return new([new("/content", "operation_content_boundary_invalid", "The response does not match the adopted content boundary.")]);
        }
        if (contract.ContentPointer == "/content" && contract.Schema.Format == SchemaFormatIds.JsonSchema && content.ValueKind == JsonValueKind.String)
        {
            try { using var json = JsonDocument.Parse(content.GetString()!, new JsonDocumentOptions { MaxDepth = 32 }); content = json.RootElement.Clone(); }
            catch (JsonException) { return new([new("/content", "operation_content_json_invalid", "The native content is not valid bounded JSON.")]); }
        }
        var handler = await formats.ResolveAsync(contract.Schema.Format, SchemaFormatCapabilities.ValidateInstance, cancellationToken: ct);
        return await handler.ValidateInstanceAsync(contract.Schema.Content, content, contract.Schema.Dialect, ct);
    }
}
