using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Server;

public sealed class AspMcpServer : IAsyncDisposable
{
    private readonly Dictionary<AspStoreRef, AspStoreBinding> bindings;
    private readonly string toolPrefix;
    private readonly TimeProvider clock;
    private readonly AspReferences references;
    private readonly AspTransfers transfers;
    private readonly AspSessionLimits limits;
    private readonly Func<RequestContext<CallToolRequestParams>, AspStoreRef, CancellationToken, ValueTask<AspStoreBinding>>? resolveBinding;

    /// <summary>The host resolves only previously configured bindings, from trusted transport context on every call.</summary>
    public AspMcpServer(Func<RequestContext<CallToolRequestParams>, AspStoreRef, CancellationToken, ValueTask<AspStoreBinding>> resolveBinding,
        TimeProvider? clock = null, string toolPrefix = "asp", AspSessionLimits? sessionLimits = null)
    {
        ArgumentNullException.ThrowIfNull(resolveBinding);
        AspValidation.Require(toolPrefix.Length is > 0 and <= 64 && toolPrefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
            "invalid_policy", "Invalid Tool prefix.");
        bindings = [];
        this.resolveBinding = resolveBinding;
        this.toolPrefix = toolPrefix;
        this.clock = clock ?? TimeProvider.System;
        references = new(this.clock);
        limits = sessionLimits ?? new();
        transfers = new(this.clock, references, limits);
    }

    public AspMcpServer(IEnumerable<AspStoreBinding> bindings, TimeProvider? clock = null, string toolPrefix = "asp", AspSessionLimits? sessionLimits = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.bindings = bindings.ToDictionary(value => value.Store);
        AspValidation.Require(toolPrefix.Length is > 0 and <= 64 && toolPrefix.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
            "invalid_policy", "Invalid Tool prefix.");
        this.toolPrefix = toolPrefix;
        AspValidation.Require(this.bindings.Count is > 0 and <= 32, "invalid_policy", "One to 32 configured logical targets are required.");
        foreach (var binding in this.bindings.Values)
        {
            AspValidation.Context(new(binding.Store, "validation", "validation", binding.Policy));
            ArgumentNullException.ThrowIfNull(binding.Provider);
            ArgumentNullException.ThrowIfNull(binding.Authorize);
        }
        this.clock = clock ?? TimeProvider.System;
        references = new(this.clock);
        limits = sessionLimits ?? new();
        transfers = new(this.clock, references, limits);
    }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = Operations().Select(operation => new Tool
            {
                Name = $"{toolPrefix}_{operation}",
                Description = $"ASP {AspProtocol.Profile} {operation}; select an authorized logical storage target.",
                InputSchema = AspSchemas.Input(operation),
                OutputSchema = AspSchemas.Output(operation)
            }).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var requestId = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AspException("invalid_request", "Missing Tool parameters.");
            var wire = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
            AspValidation.Require(wire.GetRawText().Length <= 100_000, "limit_exceeded", "Request exceeds the wire limit.");
            var invocation = AspValidation.Parse<AspInvocation<JsonElement>>(wire);
            var args = invocation.Arguments;
            AspValidation.Require(args.ValueKind == JsonValueKind.Object, "invalid_request", "Operation arguments must be an object.");
            if (args.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                requestId = id.GetString()!;
            AspValidation.RequestId(requestId);
            AspValidation.Require(parameters.Name.StartsWith(toolPrefix + "_", StringComparison.Ordinal), "unsupported_operation", "Unknown ASP mapping.");
            var operation = parameters.Name[(toolPrefix.Length + 1)..];
            AspValidation.Require(Operations().Contains(operation, StringComparer.Ordinal), "unsupported_operation", "Operation is not supported.");
            AspValidation.Store(invocation.Store);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var binding = resolveBinding is null ? bindings.GetValueOrDefault(invocation.Store)
                : await resolveBinding(request, invocation.Store, timeout.Token);
            AspValidation.Require(binding is not null && binding.Store == invocation.Store,
                "forbidden", "The selected storage target is not configured or accessible.");
            var context = Effective(binding, await binding.Authorize(request, timeout.Token));
            AspValidation.Require(Operations(binding).Contains(operation, StringComparer.Ordinal), "unsupported_operation", "The selected target does not support this operation.");
            var data = await ExecuteAsync(binding, context, operation, args, timeout.Token);
            return Reply(new AspReply<object>(AspProtocol.Version, requestId, data, null), false);
        }
        catch (AspException exception)
        { return Reply(new AspReply<object>(AspProtocol.Version, requestId, null, new(exception.Code, exception.Message, exception.SessionRef)), true); }
        catch (OperationCanceledException)
        { return Reply(new AspReply<object>(AspProtocol.Version, requestId, null, new("cancelled", "Outcome may be unknown; retry writes with the exact original request.")), true); }
    }

    private async ValueTask<object> ExecuteAsync(AspStoreBinding binding, AspAccessContext context, string operation, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var provider = binding.Provider;
        switch (operation)
        {
            case "describe":
                _ = AspValidation.Parse<AspDescribeRequest>(args);
                AspValidation.RequestId(provider.ProviderId);
                AspValidation.RequestId(provider.ProviderVersion);
                AspValidation.Require(provider.ConfigurationSchema.ValueKind == JsonValueKind.Object
                    && provider.ConfigurationSchema.GetRawText().Length <= 16_384, "invalid_result", "Invalid configuration schema.");
                return new AspDescription(AspProtocol.Version, AspProtocol.Profile, provider.ProviderId, provider.ProviderVersion,
                    context.Store, new(context.Policy.CanList, context.Policy.CanStat, context.Policy.CanRead,
                        context.Policy.CanWrite && provider is IAspObjectWriter, false, false, false), context.Policy, provider.ConfigurationSchema, provider.Transfers, limits);
            case "list":
                AspValidation.Require(context.Policy.CanList, "forbidden", "List is not authorized.");
                var list = AspValidation.Parse<AspListRequest>(args);
                AspValidation.Prefix(list.Prefix);
                AspValidation.Require(list.Prefix.StartsWith(context.Policy.Prefix, StringComparison.Ordinal), "forbidden", "Prefix widens the fixed scope.");
                AspValidation.Require(list.Limit > 0 && list.Limit <= context.Policy.MaxPageSize, "limit_exceeded", "Invalid page size.");
                var query = JsonSerializer.Serialize(new { list.Prefix, list.Limit });
                var after = list.Cursor is null ? null : references.Open(list.Cursor, "cursor", context, query).Key;
                var page = await provider.ListAsync(context, list.Prefix, after, list.Limit, ct);
                AspValidation.Require(page is not null && page.Objects is not null && page.Objects.Count <= list.Limit, "invalid_result", "Provider exceeded page size.");
                var previous = after;
                foreach (var value in page.Objects)
                {
                    ValidateObject(context, value);
                    AspValidation.Require(value.Key.StartsWith(list.Prefix, StringComparison.Ordinal)
                        && (previous is null || string.CompareOrdinal(value.Key, previous) > 0), "invalid_result", "Invalid page order or prefix.");
                    previous = value.Key;
                }
                AspValidation.Require(page.NextKey is null || page.Objects.Count > 0 && page.NextKey == previous,
                    "invalid_result", "Provider returned an invalid continuation.");
                return new AspListResult(page.Objects.Select(value => PublicObject(context, value)).ToArray(),
                    page.NextKey is null ? null : references.Seal("cursor", context, page.NextKey, query: query));
            case "stat":
                AspValidation.Require(context.Policy.CanStat, "forbidden", "Stat is not authorized.");
                var stat = AspValidation.Parse<AspReferenceRequest>(args);
                var statRef = references.Open(stat.Reference, "object", context);
                var metadata = await provider.StatAsync(context, statRef.Key, statRef.Version, statRef.ETag, ct);
                ValidateIdentity(context, metadata, statRef.Key, statRef.Version);
                AspValidation.Require(statRef.ETag is null || metadata.ETag == statRef.ETag, "version_unavailable", "The selected ETag changed.");
                return PublicObject(context, metadata);
            case "read_text":
            case "read_blob":
            case "write_text":
            case "write_blob":
            case "close_read":
            case "abort_write":
            case "write_status":
                return await transfers.ExecuteAsync(binding, context, operation, args, value => PublicObject(context, value), ct);
            default: throw new AspException("unsupported_operation", "Unknown ASP operation.");
        }
    }

    private static AspAccessContext Effective(AspStoreBinding binding, AspAccessContext grant)
    {
        AspValidation.Context(grant);
        AspValidation.Require(grant.Store == binding.Store, "forbidden", "Authorized binding identity does not match.");
        var fixedPolicy = binding.Policy;
        var policy = grant.Policy;
        AspValidation.Require(policy.Prefix.StartsWith(fixedPolicy.Prefix, StringComparison.Ordinal)
            || fixedPolicy.Prefix.StartsWith(policy.Prefix, StringComparison.Ordinal), "forbidden", "Disjoint authorized scope.");
        return grant with
        {
            Policy = new(
            fixedPolicy.CanList && policy.CanList, fixedPolicy.CanStat && policy.CanStat,
            fixedPolicy.CanRead && policy.CanRead, fixedPolicy.CanWrite && policy.CanWrite && binding.Provider is IAspObjectWriter,
            policy.Prefix.Length >= fixedPolicy.Prefix.Length ? policy.Prefix : fixedPolicy.Prefix,
            Math.Min(policy.MaxReadBytes, fixedPolicy.MaxReadBytes), Math.Min(policy.MaxWriteBytes, fixedPolicy.MaxWriteBytes),
            Math.Min(policy.MaxPageSize, fixedPolicy.MaxPageSize),
            MaxKey(policy.KeyFrom, fixedPolicy.KeyFrom), MinKey(policy.KeyBefore, fixedPolicy.KeyBefore))
        };
    }

    private static string? MaxKey(string? left, string? right) => left is null ? right : right is null ? left : string.CompareOrdinal(left, right) >= 0 ? left : right;
    private static string? MinKey(string? left, string? right) => left is null ? right : right is null ? left : string.CompareOrdinal(left, right) <= 0 ? left : right;
    private IEnumerable<string> Operations() => resolveBinding is null
        ? bindings.Values.SelectMany(Operations).Distinct(StringComparer.Ordinal)
        : ["describe", "list", "stat", "read_text", "read_blob", "write_text", "write_blob", "close_read", "abort_write", "write_status"];
    private static IEnumerable<string> Operations(AspStoreBinding binding)
    {
        yield return "describe";
        if (binding.Policy.CanList) yield return "list";
        if (binding.Policy.CanStat) yield return "stat";
        if (binding.Policy.CanRead) { yield return "read_text"; yield return "read_blob"; yield return "close_read"; }
        if (binding.Policy.CanWrite && binding.Provider is IAspObjectWriter) { yield return "write_text"; yield return "write_blob"; yield return "abort_write"; yield return "write_status"; }
    }
    private static void ValidateIdentity(AspAccessContext context, AspProviderObject value, string key, string? version)
    {
        ValidateObject(context, value);
        AspValidation.Require(value.Key == key && (version is null || value.Version == version), "invalid_result", "Provider changed the requested object identity.");
    }
    private static void ValidateObject(AspAccessContext context, AspProviderObject value)
    {
        AspValidation.Item(value);
        AspValidation.DemandKey(context, value.Key);
    }
    private AspObject PublicObject(AspAccessContext context, AspProviderObject value) =>
        new(value.Key, value.Version, value.Length, value.Sha256, references.Seal("object", context, value.Key, value.Version, etag: value.ETag), value.ETag, value.Metadata);
    public ValueTask DisposeAsync() => transfers.DisposeAsync();

    private static CallToolResult Reply(AspReply<object> value, bool error)
    {
        var json = JsonSerializer.SerializeToElement(value, AspProtocol.JsonOptions);
        AspValidation.Require(json.GetRawText().Length <= 200_000, "limit_exceeded", "Result exceeds the wire limit; reduce the page or read size.");
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}

public static class AspMcpServerExtensions
{
    public static IMcpServerBuilder WithAsp(this IMcpServerBuilder builder, AspMcpServer server)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(server);
        return builder.WithListToolsHandler(server.ListToolsAsync).WithCallToolHandler(server.CallToolAsync);
    }
}
