using System.Text.Json;
using Agentstration.Atc.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Atc.Server;

public sealed record AtcBinding(AtcTargetRef Target, IAtcProvider Provider,
    Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<AtcAccessContext>> Authorize);

public sealed class AtcMcpServer
{
    private readonly IReadOnlyDictionary<AtcTargetRef, AtcBinding> bindings;
    private readonly AtcDeclaration declaration;
    private readonly TimeProvider clock;

    public AtcMcpServer(IEnumerable<AtcBinding> bindings, string prefix = "atc", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.bindings = bindings.ToDictionary(b => b.Target);
        AtcValidation.Require(this.bindings.Count is > 0 and <= 32, "invalid_binding");
        foreach (var binding in this.bindings.Values)
        {
            AtcValidation.Target(binding.Target);
            ArgumentNullException.ThrowIfNull(binding.Provider);
            ArgumentNullException.ThrowIfNull(binding.Authorize);
            AtcValidation.Capabilities(binding.Provider.Capabilities);
        }
        declaration = AtcSchemas.Declaration(prefix);
        this.clock = clock ?? TimeProvider.System;
    }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = AtcContract.Tools.Select(op => new Tool
            {
                Name = declaration.ToolPrefix + "_" + op,
                Description = $"ATC {AtcContract.Id} {op}; select an independently authorized logical tracking target.",
                InputSchema = AtcSchemas.Input(op),
                OutputSchema = AtcSchemas.Output(op)
            }).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var requestId = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AtcException("invalid_request");
            var wire = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
            AtcValidation.Bytes(wire, AtcContract.MaximumRequestBytes);
            var invocation = AtcValidation.Parse<AtcInvocation<JsonElement>>(wire);
            AtcValidation.Target(invocation.Target);
            var args = invocation.Arguments;
            AtcValidation.Require(args.ValueKind == JsonValueKind.Object, "invalid_request");
            AtcValidation.Require(args.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String, "invalid_request");
            requestId = id.GetString()!; AtcValidation.Text(requestId);
            var prefix = declaration.ToolPrefix + "_";
            AtcValidation.Require(parameters.Name.StartsWith(prefix, StringComparison.Ordinal), "unsupported_operation");
            var operation = parameters.Name[prefix.Length..];
            AtcValidation.Require(AtcContract.Tools.Contains(operation), "unsupported_operation");
            AtcValidation.Require(bindings.TryGetValue(invocation.Target, out var binding), "forbidden");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            // Reauthorize even describe, recovery and cached terminal receipts.
            var context = await binding.Authorize(request, linked.Token);
            AtcValidation.Context(context);
            AtcValidation.Require(context.Target == binding.Target, "forbidden");
            var capabilities = binding.Provider.Capabilities;
            AtcValidation.Capabilities(capabilities);
            var operations = capabilities.Operations.Intersect(context.Operations, StringComparer.Ordinal).ToArray();
            var effective = capabilities with
            {
                Operations = operations,
                UpdateFields = operations.Contains("update", StringComparer.Ordinal) ? capabilities.UpdateFields : [],
                RelationKinds = operations.Contains("read", StringComparer.Ordinal) ? capabilities.RelationKinds : []
            };
            AtcValidation.Require(effective.Operations.Count > 0, "forbidden");
            var data = await ExecuteAsync(binding.Provider, context, effective, operation, args, linked.Token);
            var result = Reply(requestId, data);
            AtcValidation.Bytes(result.StructuredContent!.Value, capabilities.Limits.MaximumResultBytes);
            return result;
        }
        catch (AtcException error) { return Reply(requestId, error: AtcValidation.Code(error.Code) ? error.Code : "provider_failure"); }
        catch (OperationCanceledException) { return Reply(requestId, error: "cancelled"); }
    }

    private async ValueTask<object> ExecuteAsync(IAtcProvider provider, AtcAccessContext context, AtcCapabilities caps,
        string operation, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (operation)
        {
            case "describe":
                _ = AtcValidation.Parse<AtcDescribeRequest>(args);
                AtcValidation.Text(provider.ImplementationId); AtcValidation.Text(provider.ImplementationVersion);
                return new AtcDescription(declaration, context.Target, provider.ImplementationId, provider.ImplementationVersion, caps);
            case "read":
                Demand(caps, "read");
                var read = AtcValidation.Parse<AtcReadRequest>(args); AtcValidation.Text(read.ItemId);
                var item = await provider.ReadAsync(context, read, ct); AtcValidation.Item(item, context.Target, read.ItemId);
                AtcValidation.RelationAccess(item, context);
                return item;
            case "list":
                Demand(caps, "list");
                var list = AtcValidation.Parse<AtcListRequest>(args);
                AtcValidation.Require(list.Limit > 0 && list.Limit <= caps.Limits.MaximumPageSize, "limit_exceeded");
                AtcValidation.Cursor(list.Cursor);
                var page = await provider.ListAsync(context, list, ct); AtcValidation.Page(page, context.Target, list.Limit);
                foreach (var value in page.Items) AtcValidation.RelationAccess(value, context);
                return page;
            case "mutate":
                var mutation = AtcValidation.Parse<AtcMutationRequest>(args); AtcValidation.Mutation(context.Target, mutation, caps);
                if (mutation.Change is AtcAddRelation relation)
                    AtcValidation.Require(relation.Relation.Item.TargetId == context.Target.TargetId
                        || context.RelatedTargetIds.Contains(relation.Relation.Item.TargetId), "forbidden");
                var receipt = await provider.MutateAsync(context, mutation, ct); AtcValidation.Receipt(receipt, context.Target, mutation);
                if (receipt.Item is not null) AtcValidation.RelationAccess(receipt.Item, context);
                return receipt;
            default: throw new AtcException("unsupported_operation");
        }
    }

    private static void Demand(AtcCapabilities capabilities, string operation) => AtcValidation.Require(capabilities.Operations.Contains(operation), "unsupported_operation");
    private static CallToolResult Reply(string requestId, object? data = null, string? error = null) => new()
    {
        IsError = error is not null,
        StructuredContent = JsonSerializer.SerializeToElement(new AtcReply<object>(AtcContract.Id, AtcContract.Version,
            requestId, data, error is null ? null : new AtcError(error)), AtcContract.Json)
    };
}
