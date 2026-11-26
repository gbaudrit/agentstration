using System.Text.Json;
using Agentstration.Afc.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Afc.Client;

// The consumer owns the authenticated MCP transport and disposes it.
// Mutations are never automatically retried or switched to a different connection.
public sealed class AfcClient
{
    private readonly McpClient mcp;
    public AfcDescription Description { get; }

    private AfcClient(McpClient mcp, AfcDescription description) { this.mcp = mcp; Description = description; }

    public static async Task<AfcClient> ConnectAsync(McpClient mcp, AfcTargetRef target,
        AfcDeclaration? expectedDeclaration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mcp); AfcValidation.Target(target);
        var declaration = expectedDeclaration ?? AfcSchemas.Declaration(); AfcSchemas.Validate(declaration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var tools = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        foreach (var operation in AfcContract.Tools)
        {
            var candidates = tools.Where(t => t.Name == declaration.ToolPrefix + "_" + operation).ToArray();
            AfcValidation.Require(candidates.Length == 1, "incompatible_mapping");
            AfcValidation.Require(JsonElement.DeepEquals(candidates[0].ProtocolTool.InputSchema, AfcSchemas.Input(operation))
                && candidates[0].ProtocolTool.OutputSchema is { } output && JsonElement.DeepEquals(output, AfcSchemas.Output(operation)), "incompatible_schema");
        }
        var description = await InvokeAsync<AfcDescription>(mcp, declaration, target, "describe", new AfcDescribeRequest(Guid.NewGuid().ToString("N")),
            AfcContract.MaximumResultBytes, deadline.Token);
        AfcSchemas.Validate(description.Declaration);
        AfcValidation.Require(description.Target == target && description.Declaration.ToolPrefix == declaration.ToolPrefix
            && description.Declaration.Version == declaration.Version, "incompatible_target");
        AfcValidation.Capabilities(description.Capabilities);
        AfcValidation.Text(description.ImplementationId); AfcValidation.Text(description.ImplementationVersion);
        return new(mcp, description);
    }

    public async Task<AfcRepository> ResolveAsync(AfcResolveRequest request, CancellationToken cancellationToken = default)
    {
        Demand("resolve"); AfcValidation.ResolveRequest(request, Description.Capabilities);
        var repository = await CallAsync<AfcRepository>("resolve", request, cancellationToken);
        AfcValidation.Repository(repository, Description.Target, request, Description.Capabilities); return repository;
    }

    public async Task<AfcChangeRequest> ReadAsync(AfcReadRequest request, CancellationToken cancellationToken = default)
    {
        Demand("read"); AfcValidation.ReadRequest(request);
        var item = await CallAsync<AfcChangeRequest>("read", request, cancellationToken);
        AfcValidation.ChangeRequest(item, Description.Target, request.RepositoryId, request.NativeId); return item;
    }

    public async Task<AfcReceipt> CreateAsync(AfcCreateRequest request, CancellationToken cancellationToken = default)
    {
        AfcValidation.CreateRequest(Description.Target, request, Description.Capabilities);
        var receipt = await CallAsync<AfcReceipt>("create", request, cancellationToken);
        AfcValidation.Receipt(receipt, Description.Target, request); return receipt;
    }

    public async Task<AfcObservations> ObserveAsync(AfcObserveRequest request, CancellationToken cancellationToken = default)
    {
        AfcValidation.ObserveRequest(request, Description.Capabilities);
        var observations = await CallAsync<AfcObservations>("observe", request, cancellationToken);
        AfcValidation.Observations(observations, Description.Target, request, Description.Capabilities); return observations;
    }

    private void Demand(string operation) => AfcValidation.Require(Description.Capabilities.Operations.Contains(operation), "unsupported_operation");
    private async Task<T> CallAsync<T>(string operation, object args, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await InvokeAsync<T>(mcp, Description.Declaration, Description.Target, operation, args,
            Description.Capabilities.Limits.MaximumResultBytes, deadline.Token);
    }

    private static async Task<T> InvokeAsync<T>(McpClient mcp, AfcDeclaration declaration, AfcTargetRef target,
        string operation, object args, int maximumBytes, CancellationToken ct)
    {
        var input = JsonSerializer.SerializeToElement(new AfcInvocation<object>(target, args), AfcContract.Json);
        AfcValidation.Bytes(input, AfcContract.MaximumRequestBytes);
        var id = input.GetProperty("arguments").GetProperty("requestId").GetString()!; AfcValidation.Text(id);
        var result = await mcp.CallToolAsync(declaration.ToolPrefix + "_" + operation,
            input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: ct);
        AfcValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result");
        AfcValidation.Bytes(result.StructuredContent.Value, maximumBytes);
        AfcReply<T> reply;
        try { reply = AfcValidation.Parse<AfcReply<T>>(result.StructuredContent.Value); }
        catch (AfcException) { throw new AfcException("invalid_result"); }
        AfcValidation.Require(reply.ContractId == AfcContract.Id && reply.Version == declaration.Version && reply.RequestId == id, "invalid_result");
        AfcValidation.Require((reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null && AfcValidation.Code(reply.Error.Code)), "invalid_result");
        if (reply.Error is not null) throw new AfcException(reply.Error.Code);
        return reply.Data!;
    }
}
