using System.Collections.Concurrent;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Schemas;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.AI;

namespace Agentstration.Schemas.Aep;

public sealed class AepSchemaFormatOptions
{
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class AepSchemaFormatHandlerSource(
    IResourceStore store,
    AepMcpTransport transport,
    AepSchemaFormatOptions options,
    ISecretResolver? secrets = null) : ISchemaFormatHandlerSource
{
    private readonly ConcurrentDictionary<Guid, CachedContribution[]> cache = new();

    public async ValueTask<IReadOnlyList<ISchemaFormatHandler>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var registrations = (await store.ListAllAsync<ExtensionRegistrationResource>(
                ExtensionKinds.ExtensionRegistration, cancellationToken))
            .Select(value => value.Value)
            .Where(value => value.Definition.Enabled && value.ScopeRef is { } scope && scope != default)
            .ToArray();
        var handlers = new List<ISchemaFormatHandler>();
        foreach (var registration in registrations)
        {
            try
            {
                var resolved = new ResolvedAepExtension(
                    registration,
                    AepExtensionCredentials.Create(registration, secrets));
                var manifest = await transport.DiscoverAsync(resolved,
                    registration.Definition.ExpectedExtensionId, cancellationToken);
                var contributions = (manifest.Contributions.SchemaFormats ?? [])
                    .Select(value => new CachedContribution(manifest.Extension.Id, value))
                    .ToArray();
                cache[registration.Uid] = contributions;
                handlers.AddRange(contributions.Select(value => CreateHandler(
                    resolved, value, isAvailable: true, unavailableReason: null)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                if (!cache.TryGetValue(registration.Uid, out var contributions)) continue;
                var resolved = new ResolvedAepExtension(registration, null);
                handlers.AddRange(contributions.Select(value => CreateHandler(
                    resolved, value, isAvailable: false,
                    unavailableReason: SafeUnavailableReason(exception))));
            }
        }
        return handlers;
    }

    private AepSchemaFormatHandler CreateHandler(
        ResolvedAepExtension extension,
        CachedContribution cached,
        bool isAvailable,
        string? unavailableReason) => new(
            extension,
            cached.ExtensionId,
            cached.Contribution,
            transport,
            options.OperationTimeout,
            isAvailable,
            unavailableReason);

    private static string SafeUnavailableReason(Exception exception) => exception switch
    {
        AepProtocolException protocol => $"AEP schema format provider is unavailable ({protocol.Code}).",
        HttpRequestException => "AEP schema format provider is unreachable.",
        TaskCanceledException => "AEP schema format provider discovery timed out.",
        _ => "AEP schema format provider is unavailable."
    };

    private sealed record CachedContribution(string ExtensionId, AepSchemaFormatContribution Contribution);
}

internal sealed class AepSchemaFormatHandler : ISchemaFormatHandler
{
    private const int MaximumIssues = 1_000;
    private readonly ResolvedAepExtension extension;
    private readonly AepSchemaFormatContribution contribution;
    private readonly AepMcpTransport transport;
    private readonly TimeSpan operationTimeout;

    public AepSchemaFormatHandler(
        ResolvedAepExtension extension,
        string extensionId,
        AepSchemaFormatContribution contribution,
        AepMcpTransport transport,
        TimeSpan operationTimeout,
        bool isAvailable,
        string? unavailableReason)
    {
        this.extension = extension;
        this.contribution = contribution;
        this.transport = transport;
        this.operationTimeout = operationTimeout;
        Descriptor = new(
            contribution.Format,
            contribution.DisplayName,
            new($"{extensionId}/{contribution.Id}", contribution.Version),
            contribution.Dialects.ToArray(),
            SchemaFormatCapabilities.ValidateDefinition | SchemaFormatCapabilities.ValidateInstance,
            contribution.IsDefault,
            isAvailable,
            unavailableReason);
    }

    public SchemaFormatDescriptor Descriptor { get; }

    public ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(
        JsonElement definition,
        string? dialect,
        CancellationToken cancellationToken) => InvokeAsync(
            contribution.Operations.ValidateDefinition,
            new AepSchemaDefinitionValidationRequest(definition.Clone(), dialect),
            cancellationToken);

    public ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(
        JsonElement definition,
        JsonElement instance,
        string? dialect,
        CancellationToken cancellationToken) => InvokeAsync(
            contribution.Operations.ValidateInstance,
            new AepSchemaInstanceValidationRequest(definition.Clone(), instance.Clone(), dialect),
            cancellationToken);

    private async ValueTask<SchemaFormatValidationResult> InvokeAsync<TRequest>(
        AepMcpToolMapping mapping,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (!Descriptor.IsAvailable)
            throw new SchemaFormatOperationException("schema_format_provider_unavailable",
                Descriptor.UnavailableReason ?? "AEP schema format provider is unavailable.");
        if (operationTimeout <= TimeSpan.Zero)
            throw new InvalidOperationException("The AEP schema format operation timeout must be positive.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(operationTimeout);
        try
        {
            var server = (await DiscoverAsync(timeout.Token)).Mcp?.Servers
                .SingleOrDefault(value => string.Equals(value.Id, mapping.Server, StringComparison.Ordinal))
                ?? throw new SchemaFormatOperationException("schema_format_provider_invalid",
                    $"AEP schema format operation references missing MCP server '{mapping.Server}'.");
            await using var client = await transport.ConnectAsync(extension, server, timeout.Token);
            var tool = (await client.ListToolsAsync(cancellationToken: timeout.Token))
                .SingleOrDefault(value => string.Equals(value.ProtocolTool.Name, mapping.Tool,
                    StringComparison.Ordinal))
                ?? throw new SchemaFormatOperationException("schema_format_operation_missing",
                    $"AEP schema format MCP tool '{mapping.Tool}' is unavailable.");
            var arguments = JsonSerializer.SerializeToElement(request, AepProtocol.JsonOptions)
                .EnumerateObject()
                .ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
            var result = JsonSerializer.SerializeToElement(
                await tool.InvokeAsync(new AIFunctionArguments(arguments), timeout.Token),
                AepProtocol.JsonOptions);
            return ParseResult(result);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            throw new SchemaFormatOperationException("schema_format_provider_timeout",
                "The AEP schema format operation exceeded its time limit.", exception);
        }
        catch (SchemaFormatOperationException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            throw new SchemaFormatOperationException("schema_format_provider_failed",
                "The AEP schema format operation failed.", exception);
        }
    }

    private async Task<AepManifest> DiscoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await transport.DiscoverAsync(extension,
                extension.Registration.Definition.ExpectedExtensionId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            throw new SchemaFormatOperationException("schema_format_provider_unavailable",
                "The AEP schema format provider is unavailable.", exception);
        }
    }

    private static SchemaFormatValidationResult ParseResult(JsonElement envelope)
    {
        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("isError", out var isError)
            && isError.ValueKind == JsonValueKind.True)
            throw new SchemaFormatOperationException("schema_format_provider_failed", ReadError(envelope));

        if (TryReadPayload(envelope, out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("issues", out var issues)
            && issues.ValueKind == JsonValueKind.Array)
        {
            if (issues.GetArrayLength() > MaximumIssues)
                throw new SchemaFormatOperationException("schema_format_result_invalid",
                    "The AEP schema format provider returned too many validation issues.");
            var mapped = new List<SchemaFormatValidationIssue>();
            foreach (var issue in issues.EnumerateArray())
            {
                if (issue.ValueKind != JsonValueKind.Object
                    || !ReadBoundedString(issue, "path", 2_048, out var path)
                    || !ReadBoundedString(issue, "code", 128, out var code)
                    || !ReadBoundedString(issue, "message", 2_048, out var message))
                    throw new SchemaFormatOperationException("schema_format_result_invalid",
                        "The AEP schema format provider returned an invalid validation issue.");
                mapped.Add(new(path, code, message));
            }
            return new(mapped);
        }
        throw new SchemaFormatOperationException("schema_format_result_invalid",
            "The AEP schema format provider returned an invalid structured result.");
    }

    private static bool TryReadPayload(JsonElement envelope, out JsonElement payload)
    {
        if (envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("issues", out _))
        {
            payload = envelope;
            return true;
        }
        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("structuredContent", out var structured)
            && structured.ValueKind == JsonValueKind.Object)
        {
            payload = structured;
            return true;
        }
        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("result", out var result))
        {
            if (result.ValueKind == JsonValueKind.Object)
                return TryReadPayload(result, out payload);
            if (result.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var document = JsonDocument.Parse(result.GetString()!);
                    return TryReadPayload(document.RootElement.Clone(), out payload);
                }
                catch (JsonException) { }
            }
        }
        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("text", out var directText)
            && directText.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var document = JsonDocument.Parse(directText.GetString()!);
                return TryReadPayload(document.RootElement.Clone(), out payload);
            }
            catch (JsonException) { }
        }
        if (envelope.ValueKind == JsonValueKind.Object
            && envelope.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("text", out var text)
                    || text.ValueKind != JsonValueKind.String) continue;
                try
                {
                    using var document = JsonDocument.Parse(text.GetString()!);
                    payload = document.RootElement.Clone();
                    return true;
                }
                catch (JsonException) { }
            }
        }
        payload = default;
        return false;
    }

    private static bool ReadBoundedString(
        JsonElement value,
        string propertyName,
        int maximumLength,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || property.GetString() is not { Length: > 0 } text
            || text.Length > maximumLength) return false;
        result = text;
        return true;
    }

    private static string ReadError(JsonElement envelope)
    {
        if (envelope.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { Length: > 0 } message)
                    return message[..Math.Min(message.Length, 512)];
        }
        return "The AEP schema format MCP operation reported a failure.";
    }
}
