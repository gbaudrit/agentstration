using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Extensions.Contracts;

namespace Agentstration.Tools.Mcp;

public static class ToolInvocationFingerprint
{
    public static string AepRoute(ExtensionRegistrationResource registration, AepManifest manifest, AepToolContribution contribution) =>
        Digest(JsonSerializer.SerializeToElement(new
        {
            registration.Uid,
            registration.Generation,
            registration.Definition,
            Manifest = manifest,
            Server = manifest.Mcp?.Servers.Single(server => server.Id == contribution.Mcp.Server),
            Contribution = contribution
        }));

    public static string Schemas(JsonElement input, JsonElement? output) =>
        Digest(JsonSerializer.SerializeToElement(new { input, output }));

    private static string Digest(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
