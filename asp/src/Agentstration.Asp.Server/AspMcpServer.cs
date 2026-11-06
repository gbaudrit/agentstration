using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Asp.Server;

public sealed class AspMcpServer
{
    private readonly Dictionary<string, AspToolBinding> bindings;
    private readonly TimeProvider clock;
    private readonly AspReferences references;

    public AspMcpServer(IEnumerable<AspToolBinding> bindings, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.bindings = bindings.ToDictionary(value => value.Name, StringComparer.Ordinal);
        AspValidation.Require(this.bindings.Count is > 0 and <= 32, "invalid_policy", "One to 32 fixed bindings are required.");
        foreach (var binding in this.bindings.Values)
        {
            AspValidation.Require(binding.Name.Length is > 0 and <= 64 && binding.Name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
                "invalid_policy", "Invalid binding Tool prefix.");
            AspValidation.Context(new(binding.Store, "validation", "validation", binding.Policy));
            ArgumentNullException.ThrowIfNull(binding.Provider);
            ArgumentNullException.ThrowIfNull(binding.Authorize);
        }
        this.clock = clock ?? TimeProvider.System;
        references = new(this.clock);
    }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = bindings.Values.SelectMany(binding => Operations(binding).Select(operation => new Tool
            {
                Name = $"{binding.Name}_{operation}",
                Description = $"ASP {AspProtocol.Profile} {operation}; fixed authorized logical store.",
                InputSchema = AspSchemas.Input(operation),
                OutputSchema = AspSchemas.Output(operation)
            })).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var requestId = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AspException("invalid_request", "Missing Tool parameters.");
            var args = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
            AspValidation.Require(args.GetRawText().Length <= 100_000, "limit_exceeded", "Request exceeds the wire limit.");
            if (args.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                requestId = id.GetString()!;
            AspValidation.RequestId(requestId);
            var split = parameters.Name.LastIndexOf('_');
            AspValidation.Require(split > 0 && bindings.ContainsKey(parameters.Name[..split]), "unsupported_operation", "Unknown ASP mapping.");
            var binding = bindings[parameters.Name[..split]];
            var operation = parameters.Name[(split + 1)..];
            AspValidation.Require(Operations(binding).Contains(operation, StringComparer.Ordinal), "unsupported_operation", "Operation is not supported.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var context = Effective(binding, await binding.Authorize(request, timeout.Token));
            var data = await ExecuteAsync(binding, context, operation, args, timeout.Token);
            return Reply(new AspReply<object>(AspProtocol.Version, requestId, data, null), false);
        }
        catch (AspException exception)
        { return Reply(new AspReply<object>(AspProtocol.Version, requestId, null, new(exception.Code, exception.Message)), true); }
        catch (OperationCanceledException)
        { return Reply(new AspReply<object>(AspProtocol.Version, requestId, null, new("cancelled", "Outcome may be unknown; retry writes with the exact original request.")), true); }
    }

    private async ValueTask<object> ExecuteAsync(AspToolBinding binding, AspAccessContext context, string operation, JsonElement args, CancellationToken ct)
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
                        context.Policy.CanWrite && provider is IAspObjectWriter, false, false, false), context.Policy, provider.ConfigurationSchema);
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
                var metadata = await provider.StatAsync(context, statRef.Key, statRef.Version!, ct);
                ValidateIdentity(context, metadata, statRef.Key, statRef.Version!);
                return PublicObject(context, metadata);
            case "read":
                AspValidation.Require(context.Policy.CanRead, "forbidden", "Read is not authorized.");
                var read = AspValidation.Parse<AspReadRequest>(args);
                AspValidation.Require(read.Offset >= 0 && read.MaxBytes > 0 && read.MaxBytes <= context.Policy.MaxReadBytes,
                    "limit_exceeded", "Invalid read range.");
                var readRef = references.Open(read.Reference, "object", context);
                var content = await provider.ReadAsync(context, readRef.Key, readRef.Version!, read.Offset, read.MaxBytes, ct);
                AspValidation.Require(content is not null && content.Content is not null, "invalid_result", "Missing provider content.");
                ValidateIdentity(context, content.Item, readRef.Key, readRef.Version!);
                AspValidation.Require(read.Offset <= content.Item.Length && content.Content.Length <= read.MaxBytes
                    && content.Content.Length == Math.Min(read.MaxBytes, content.Item.Length - read.Offset)
                    && content.EndOfObject == (read.Offset + content.Content.Length == content.Item.Length), "invalid_result", "Invalid read result.");
                if (read.Offset == 0 && content.EndOfObject)
                    AspValidation.Require(AspValidation.Digest(content.Content) == content.Item.Sha256, "invalid_result", "Content digest mismatch.");
                return new AspReadResult(PublicObject(context, content.Item), read.Offset, Convert.ToBase64String(content.Content), content.EndOfObject);
            case "write":
                AspValidation.Require(context.Policy.CanWrite && provider is IAspObjectWriter, "forbidden", "Write is not authorized.");
                var write = AspValidation.Parse<AspWriteRequest>(args);
                AspValidation.Write(write, context, clock);
                var bytes = AspValidation.Content(write.ContentBase64, context.Policy.MaxWriteBytes);
                var written = await ((IAspObjectWriter)provider).WriteAsync(context,
                    new(write.RequestId, write.NotAfter, write.Key, bytes, write.ExpectedVersion), ct);
                ValidateObject(context, written);
                AspValidation.Require(written.Key == write.Key && written.Length == bytes.Length && written.Sha256 == AspValidation.Digest(bytes),
                    "invalid_result", "Provider returned an invalid write receipt.");
                return new AspWriteResult(PublicObject(context, written));
            default: throw new AspException("unsupported_operation", "Unknown ASP operation.");
        }
    }

    private static AspAccessContext Effective(AspToolBinding binding, AspAccessContext grant)
    {
        AspValidation.Context(grant);
        AspValidation.Require(grant.Store == binding.Store, "forbidden", "Authorized binding identity does not match.");
        var fixedPolicy = binding.Policy;
        var policy = grant.Policy;
        AspValidation.Require(policy.Prefix.StartsWith(fixedPolicy.Prefix, StringComparison.Ordinal)
            || fixedPolicy.Prefix.StartsWith(policy.Prefix, StringComparison.Ordinal), "forbidden", "Disjoint authorized scope.");
        return grant with { Policy = new(
            fixedPolicy.CanList && policy.CanList, fixedPolicy.CanStat && policy.CanStat,
            fixedPolicy.CanRead && policy.CanRead, fixedPolicy.CanWrite && policy.CanWrite && binding.Provider is IAspObjectWriter,
            policy.Prefix.Length >= fixedPolicy.Prefix.Length ? policy.Prefix : fixedPolicy.Prefix,
            Math.Min(policy.MaxReadBytes, fixedPolicy.MaxReadBytes), Math.Min(policy.MaxWriteBytes, fixedPolicy.MaxWriteBytes),
            Math.Min(policy.MaxPageSize, fixedPolicy.MaxPageSize),
            MaxKey(policy.KeyFrom, fixedPolicy.KeyFrom), MinKey(policy.KeyBefore, fixedPolicy.KeyBefore)) };
    }

    private static string? MaxKey(string? left, string? right) => left is null ? right : right is null ? left : string.CompareOrdinal(left, right) >= 0 ? left : right;
    private static string? MinKey(string? left, string? right) => left is null ? right : right is null ? left : string.CompareOrdinal(left, right) <= 0 ? left : right;
    private static IEnumerable<string> Operations(AspToolBinding binding)
    {
        yield return "describe";
        if (binding.Policy.CanList) yield return "list";
        if (binding.Policy.CanStat) yield return "stat";
        if (binding.Policy.CanRead) yield return "read";
        if (binding.Policy.CanWrite && binding.Provider is IAspObjectWriter) yield return "write";
    }
    private static void ValidateIdentity(AspAccessContext context, AspProviderObject value, string key, string version)
    {
        ValidateObject(context, value);
        AspValidation.Require(value.Key == key && value.Version == version, "invalid_result", "Provider changed the requested object identity.");
    }
    private static void ValidateObject(AspAccessContext context, AspProviderObject value)
    {
        AspValidation.Item(value);
        AspValidation.DemandKey(context, value.Key);
    }
    private AspObject PublicObject(AspAccessContext context, AspProviderObject value) =>
        new(value.Key, value.Version, value.Length, value.Sha256, references.Seal("object", context, value.Key, value.Version));
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
