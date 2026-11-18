using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Functions.Abstractions;
using Agentstration.Identity.Contracts;

namespace Agentstration.Infrastructure.Functions;

internal sealed class ArtifactFunctionContentReader(ArtifactManagementService artifacts, IArtifactDurableStore durable,
    ICurrentRequestContext current, IRequestContextScopeFactory scopes)
{
    public async Task<ReadOnlyMemory<byte>> ReadAsync(JsonElement item, string id, CancellationToken ct)
    {
        var kind = item.TryGetProperty("kind", out var selectedKind) ? selectedKind.GetString() : "durable";
        using var operational = kind == "inline" ? null : scopes.Push(current.Current);
        ReadOnlyMemory<byte> bytes;
        if (kind == "inline")
        {
            var encoded = item.GetProperty("contentBase64").GetString();
            if (encoded is null || encoded.Length > 1_398_104) throw Error("function_artifact_limit");
            bytes = Convert.FromBase64String(encoded);
        }
        else if (kind == "staged")
        {
            var stage = (await artifacts.GetStagedAsync(StagedArtifactId.Parse(id), null, ArtifactLeaseOperation.Read, ct))?.Value
                ?? throw Error("function_artifact_unavailable");
            if (stage.ArtifactStatus is not (StagedArtifactStatus.Sealed or StagedArtifactStatus.Persisted) || stage.Length > 1024 * 1024)
                throw Error("function_artifact_invalid");
            using var content = new MemoryStream();
            while (content.Length < stage.Length)
            {
                var part = Convert.FromBase64String((await artifacts.ReadAsync(stage.ArtifactId, content.Length,
                    (int)Math.Min(65536, stage.Length - content.Length), null, ct)).ContentBase64);
                if (part.Length == 0) throw Error("function_artifact_incomplete");
                await content.WriteAsync(part, ct);
            }
            bytes = content.ToArray();
            if (bytes.Length != stage.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), stage.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("function_artifact_integrity");
        }
        else if (kind == "durable")
        {
            var value = (await artifacts.GetFlowRunArtifactForContentReadAsync(FlowRunArtifactId.Parse(id), true, ct))?.Value
                ?? throw Error("function_artifact_unavailable");
            if (value.Receipt.Length > 1024 * 1024) throw Error("function_artifact_limit");
            using var content = new MemoryStream();
            while (content.Length < value.Receipt.Length)
            {
                var part = await durable.ReadAsync(new(current.Current.WorkspaceId), value.Receipt.OpaqueReference,
                    content.Length, (int)Math.Min(65536, value.Receipt.Length - content.Length), ct);
                if (part.Length == 0) throw Error("function_artifact_incomplete");
                await content.WriteAsync(part, ct);
            }
            bytes = content.ToArray();
            if (bytes.Length != value.Receipt.Length || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), value.Receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Error("function_artifact_integrity");
        }
        else throw Error("function_artifact_invalid");
        if (bytes.Length > 1024 * 1024) throw Error("function_artifact_limit");
        if (item.TryGetProperty("digest", out var expected) && expected.ValueKind == JsonValueKind.String
            && !string.Equals(expected.GetString()!.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase),
                Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), StringComparison.OrdinalIgnoreCase))
            throw Error("function_artifact_integrity");
        return bytes;
    }
    private static TransformationFunctionException Error(string code) => new(code, "The exact JSON Artifact content is unavailable or invalid.");
}
