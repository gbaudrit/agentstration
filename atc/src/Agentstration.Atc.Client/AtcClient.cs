using System.Text.Json;
using Agentstration.Atc.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Atc.Client;

// The consumer owns the authenticated MCP transport and disposes it.
// Mutations are never automatically retried or switched to a different connection.
public sealed class AtcClient
{
    private readonly McpClient mcp;
    public AtcDescription Description { get; }

    private AtcClient(McpClient mcp, AtcDescription description) { this.mcp = mcp; Description = description; }

    public static async Task<AtcClient> ConnectAsync(McpClient mcp, AtcTargetRef target,
        AtcDeclaration? expectedDeclaration = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mcp); AtcValidation.Target(target);
        var declaration = expectedDeclaration ?? AtcSchemas.Declaration(); AtcSchemas.Validate(declaration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var tools = await mcp.ListToolsAsync(cancellationToken: deadline.Token);
        foreach (var operation in AtcContract.Tools)
        {
            var candidates = tools.Where(t => t.Name == declaration.ToolPrefix + "_" + operation).ToArray();
            AtcValidation.Require(candidates.Length == 1, "incompatible_mapping");
            AtcValidation.Require(JsonElement.DeepEquals(candidates[0].ProtocolTool.InputSchema, AtcSchemas.Input(operation))
                && candidates[0].ProtocolTool.OutputSchema is { } output && JsonElement.DeepEquals(output, AtcSchemas.Output(operation)), "incompatible_schema");
        }
        var description = await InvokeAsync<AtcDescription>(mcp, declaration, target, "describe", new AtcDescribeRequest(Guid.NewGuid().ToString("N")),
            AtcContract.MaximumResultBytes, deadline.Token);
        AtcSchemas.Validate(description.Declaration);
        AtcValidation.Require(description.Target == target && description.Declaration.ToolPrefix == declaration.ToolPrefix
            && description.Declaration.Version == declaration.Version, "incompatible_target");
        AtcValidation.Capabilities(description.Capabilities);
        AtcValidation.Text(description.ImplementationId); AtcValidation.Text(description.ImplementationVersion);
        return new(mcp, description);
    }

    public async Task<AtcItem> ReadAsync(AtcReadRequest request, CancellationToken cancellationToken = default)
    {
        Demand("read"); AtcValidation.Text(request.ItemId);
        var item = await CallAsync<AtcItem>("read", request, cancellationToken);
        AtcValidation.Item(item, Description.Target, request.ItemId); return item;
    }

    public async Task<AtcPage> ListAsync(AtcListRequest request, CancellationToken cancellationToken = default)
    {
        Demand("list"); AtcValidation.Require(request.Limit > 0 && request.Limit <= Description.Capabilities.Limits.MaximumPageSize, "limit_exceeded");
        AtcValidation.Cursor(request.Cursor);
        var page = await CallAsync<AtcPage>("list", request, cancellationToken);
        AtcValidation.Page(page, Description.Target, request.Limit); return page;
    }

    public async Task<AtcReceipt> MutateAsync(AtcMutationRequest request, CancellationToken cancellationToken = default)
    {
        AtcValidation.Mutation(Description.Target, request, Description.Capabilities);
        var receipt = await CallAsync<AtcReceipt>("mutate", request, cancellationToken);
        AtcValidation.Receipt(receipt, Description.Target, request); return receipt;
    }

    private void Demand(string operation) => AtcValidation.Require(Description.Capabilities.Operations.Contains(operation), "unsupported_operation");
    private async Task<T> CallAsync<T>(string operation, object args, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await InvokeAsync<T>(mcp, Description.Declaration, Description.Target, operation, args,
            Description.Capabilities.Limits.MaximumResultBytes, deadline.Token);
    }

    private static async Task<T> InvokeAsync<T>(McpClient mcp, AtcDeclaration declaration, AtcTargetRef target,
        string operation, object args, int maximumBytes, CancellationToken ct)
    {
        var input = JsonSerializer.SerializeToElement(new AtcInvocation<object>(target, args), AtcContract.Json);
        AtcValidation.Bytes(input, AtcContract.MaximumRequestBytes);
        var id = input.GetProperty("arguments").GetProperty("requestId").GetString()!; AtcValidation.Text(id);
        var result = await mcp.CallToolAsync(declaration.ToolPrefix + "_" + operation,
            input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: ct);
        AtcValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result");
        AtcValidation.Bytes(result.StructuredContent.Value, maximumBytes);
        AtcReply<T> reply;
        try { reply = AtcValidation.Parse<AtcReply<T>>(result.StructuredContent.Value); }
        catch (AtcException) { throw new AtcException("invalid_result"); }
        AtcValidation.Require(reply.ContractId == AtcContract.Id && reply.Version == declaration.Version && reply.RequestId == id, "invalid_result");
        AtcValidation.Require((reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null && AtcValidation.Code(reply.Error.Code)), "invalid_result");
        if (reply.Error is not null) throw new AtcException(reply.Error.Code);
        return reply.Data!;
    }
}
