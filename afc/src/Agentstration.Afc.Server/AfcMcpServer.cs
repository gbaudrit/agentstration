using System.Text.Json;
using Agentstration.Afc.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Afc.Server;

public sealed record AfcBinding(AfcTargetRef Target, IAfcProvider Provider,
    Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<AfcAccessContext>> Authorize);

public sealed class AfcMcpServer
{
    private readonly IReadOnlyDictionary<AfcTargetRef, AfcBinding> bindings;
    private readonly AfcDeclaration declaration;
    private readonly TimeProvider clock;

    public AfcMcpServer(IEnumerable<AfcBinding> bindings, string prefix = "afc", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        this.bindings = bindings.ToDictionary(b => b.Target);
        AfcValidation.Require(this.bindings.Count is > 0 and <= 32, "invalid_binding");
        foreach (var binding in this.bindings.Values)
        {
            AfcValidation.Target(binding.Target);
            ArgumentNullException.ThrowIfNull(binding.Provider);
            ArgumentNullException.ThrowIfNull(binding.Authorize);
            AfcValidation.Capabilities(binding.Provider.Capabilities);
        }
        declaration = AfcSchemas.Declaration(prefix);
        this.clock = clock ?? TimeProvider.System;
    }

    public ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ListToolsResult
        {
            Tools = AfcContract.Tools.Select(operation => new Tool
            {
                Name = declaration.ToolPrefix + "_" + operation,
                Description = $"AFC {AfcContract.Id} {operation}; select an independently authorized logical forge target.",
                InputSchema = AfcSchemas.Input(operation),
                OutputSchema = AfcSchemas.Output(operation)
            }).ToList()
        });
    }

    public async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var requestId = "unknown";
        try
        {
            var parameters = request.Params ?? throw new AfcException("invalid_request");
            var wire = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
            AfcValidation.Bytes(wire, AfcContract.MaximumRequestBytes);
            var invocation = AfcValidation.Parse<AfcInvocation<JsonElement>>(wire);
            AfcValidation.Target(invocation.Target);
            var args = invocation.Arguments;
            AfcValidation.Require(args.ValueKind == JsonValueKind.Object && args.TryGetProperty("requestId", out _), "invalid_request");
            var id = args.GetProperty("requestId");
            AfcValidation.Require(id.ValueKind == JsonValueKind.String, "invalid_request");
            requestId = id.GetString()!; AfcValidation.Text(requestId);
            var prefix = declaration.ToolPrefix + "_";
            AfcValidation.Require(parameters.Name.StartsWith(prefix, StringComparison.Ordinal), "unsupported_operation");
            var operation = parameters.Name[prefix.Length..];
            AfcValidation.Require(AfcContract.Tools.Contains(operation), "unsupported_operation");
            AfcValidation.Require(bindings.TryGetValue(invocation.Target, out var binding), "forbidden");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            // Authentication/configuration are host-owned; reauthorize even cached receipts.
            var context = await binding.Authorize(request, linked.Token);
            AfcValidation.Context(context);
            AfcValidation.Require(context.Target == binding.Target, "forbidden");
            var caps = binding.Provider.Capabilities;
            AfcValidation.Capabilities(caps);
            var allowed = caps.Operations.Intersect(context.Operations, StringComparer.Ordinal).ToArray();
            AfcValidation.Require(allowed.Length > 0, "forbidden");
            var effective = caps with
            {
                Operations = allowed,
                Reviews = allowed.Contains("observe", StringComparer.Ordinal) && caps.Reviews,
                Checks = allowed.Contains("observe", StringComparer.Ordinal) && caps.Checks
            };
            var data = await ExecuteAsync(binding.Provider, context, effective, operation, args, linked.Token);
            var result = Reply(requestId, data);
            AfcValidation.Bytes(result.StructuredContent!.Value, caps.Limits.MaximumResultBytes);
            return result;
        }
        catch (AfcException error) { return Reply(requestId, error: AfcValidation.Code(error.Code) ? error.Code : "provider_failure"); }
        catch (OperationCanceledException) { return Reply(requestId, error: "cancelled"); }
    }

    private async ValueTask<object> ExecuteAsync(IAfcProvider provider, AfcAccessContext context, AfcCapabilities caps,
        string operation, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (operation != "describe") AfcValidation.Require(caps.Operations.Contains(operation), "unsupported_operation");
        switch (operation)
        {
            case "describe":
                _ = AfcValidation.Parse<AfcDescribeRequest>(args);
                AfcValidation.Text(provider.ImplementationId); AfcValidation.Text(provider.ImplementationVersion);
                return new AfcDescription(declaration, context.Target, provider.ImplementationId, provider.ImplementationVersion, caps);
            case "resolve":
                var resolve = AfcValidation.Parse<AfcResolveRequest>(args); AfcValidation.ResolveRequest(resolve, caps);
                var repository = await provider.ResolveAsync(context, resolve, token);
                AfcValidation.Repository(repository, context.Target, resolve, caps); return repository;
            case "read":
                var read = AfcValidation.Parse<AfcReadRequest>(args); AfcValidation.ReadRequest(read);
                var item = await provider.ReadAsync(context, read, token);
                AfcValidation.ChangeRequest(item, context.Target, read.RepositoryId, read.NativeId); return item;
            case "create":
                var create = AfcValidation.Parse<AfcCreateRequest>(args); AfcValidation.CreateRequest(context.Target, create, caps);
                var receipt = await provider.CreateAsync(context, create, token);
                AfcValidation.Receipt(receipt, context.Target, create); return receipt;
            case "observe":
                var observe = AfcValidation.Parse<AfcObserveRequest>(args); AfcValidation.ObserveRequest(observe, caps);
                var observations = await provider.ObserveAsync(context, observe, token);
                AfcValidation.Observations(observations, context.Target, observe, caps); return observations;
            default: throw new AfcException("unsupported_operation");
        }
    }

    private static CallToolResult Reply(string requestId, object? data = null, string? error = null) => new()
    {
        IsError = error is not null,
        StructuredContent = JsonSerializer.SerializeToElement(new AfcReply<object>(AfcContract.Id, AfcContract.Version,
            requestId, data, error is null ? null : new AfcError(error)), AfcContract.Json)
    };
}
