using System.Text.Json;
using Agentstration.Asc.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Asc.Client;

/// <summary>Owns no transport and never automatically retries mutations.</summary>
public sealed class AscDocumentClient
{
    private readonly McpClient transport;
    private readonly string prefix;
    public AscDocumentDescription Description { get; }
    private AscDocumentClient(McpClient transport, string prefix, AscDocumentDescription description)
    { this.transport = transport; this.prefix = prefix; Description = description; }

    public static async Task<AscDocumentClient> ConnectAsync(McpClient transport, string prefix,
        AscStoreRef store, AscDocumentSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        AscValidation.Require(transport.NegotiatedProtocolVersion == AscContract.McpProtocolVersion,
            "incompatible_version", "Unsupported ASC MCP protocol version.");
        var tools = await transport.ListToolsAsync(cancellationToken: ct);
        AscValidation.Require(tools.Count <= 1024 && tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == tools.Count,
            "incompatible_schema", "The document tool catalog contains duplicate mappings or exceeds its bound.");
        foreach (var operation in AscDocumentSchemas.Operations)
        {
            var tool = tools.SingleOrDefault(t => t.Name == prefix + "_" + operation)
                ?? throw new AscException("incompatible_schema", "A document mapping is missing.");
            AscValidation.Require(JsonElement.DeepEquals(tool.ProtocolTool.InputSchema, AscDocumentSchemas.Input(operation))
                && tool.ProtocolTool.OutputSchema is { } schema && JsonElement.DeepEquals(schema, AscDocumentSchemas.Output(operation)),
                "incompatible_schema", "The document mapping differs from its canonical schema.");
        }
        var description = await InvokeAsync<AscDocumentDescription>(transport, prefix, "describe", store, selection,
            new AscDescribeRequest(Guid.NewGuid().ToString("N")), ct);
        AscValidation.Require(description.AscVersion == AscContract.Version && description.Profile == AscDocuments.Profile,
            "incompatible_version", "The document profile is incompatible.");
        AscValidation.Require(description.Store == store && description.Selection == selection,
            "forbidden", "The provider changed the selected target.");
        AscDocumentValidation.Policy(description.Policy);
        AscValidation.Require(description.Policy.Collection == selection.Collection, "invalid_result", "The provider changed the collection policy.");
        AscValidation.Require(description.SchemaDigest == AscDocumentValidation.Digest(description.DocumentSchema)
            && description.QueryProfiles.Count <= 8
            && description.QueryProfiles.All(p => p.Reference.ModelDigest == description.SchemaDigest),
            "invalid_result", "Invalid model/schema evidence.");
        return new(transport, prefix, description);
    }
    public Task<AscDocument> GetAsync(AscDocumentGet request, CancellationToken ct = default) => CallAsync<AscDocument>("get", request, ct);
    public Task<AscDocument> PutAsync(AscDocumentPut request, CancellationToken ct = default) => CallAsync<AscDocument>("put", request, ct);
    public Task<AscDocumentDeleted> DeleteAsync(AscDocumentDelete request, CancellationToken ct = default) => CallAsync<AscDocumentDeleted>("delete", request, ct);
    public Task<AscDocumentPage> QueryAsync(AscDocumentQuery request, CancellationToken ct = default) => CallAsync<AscDocumentPage>("query", request, ct);
    public Task<AscDocumentPublication> PrepareAsync(AscDocumentPrepare request, CancellationToken ct = default) => CallAsync<AscDocumentPublication>("prepare", request, ct);
    public Task<AscDocumentPublication> SealAsync(AscDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AscDocumentPublication>("seal", request, ct);
    public Task<AscDocumentPublication> InspectAsync(AscDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AscDocumentPublication>("inspect", request, ct);
    public Task<AscDocumentPublication> RetireAsync(AscDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AscDocumentPublication>("retire", request, ct);
    private async Task<T> CallAsync<T>(string operation, object request, CancellationToken ct)
    {
        var result = await InvokeAsync<T>(transport, prefix, operation, Description.Store, Description.Selection, request, ct);
        if (result is AscDocument document)
        {
            var expectedId = request switch { AscDocumentGet get => get.Id, AscDocumentPut put => put.Id, _ => null };
            Check(document);
            AscValidation.Require(document.Id == expectedId && (request is not AscDocumentGet { Revision: { } revision } || document.Revision == revision),
                "invalid_result", "The provider changed the exact document identity/revision.");
        }
        if (result is AscDocumentPage page && request is AscDocumentQuery query)
        {
            AscValidation.Require(page.Documents.Count <= query.PageSize && page.Groups.Count <= query.PageSize
                && (page.Documents.Count == 0 || page.Groups.Count == 0)
                && page.Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == page.Documents.Count
                && (page.Cursor is null || page.Cursor.Length is > 0 and <= 4096), "invalid_result", "The provider exceeded the page contract.");
            foreach (var item in page.Documents) Check(item);
        }
        if (result is AscDocumentDeleted deleted && request is AscDocumentDelete removal)
            AscValidation.Require(deleted.Id == removal.Id && deleted.Revision == removal.ExpectedRevision, "invalid_result", "The provider changed deletion evidence.");
        if (result is AscDocumentPublication publication)
        {
            var expected = request switch { AscDocumentPrepare prepare => prepare.PublicationId, AscDocumentPublicationRequest operationRequest => operationRequest.PublicationId, _ => null };
            AscValidation.Require(publication.Id == expected && publication.Count is >= 0 and <= 100
                && publication.Digest is { Length: 64 } && publication.SchemaDigest == Description.SchemaDigest && publication.Contract == AscDocuments.PublicationContract,
                "invalid_result", "The provider changed immutable publication evidence.");
        }
        return result;
    }
    private void Check(AscDocument document)
    {
        try { AscDocumentValidation.Identity(document.Id); AscDocumentValidation.Identity(document.Revision); AscDocumentValidation.Document(document.Value, Description.Policy.MaximumDocumentBytes); }
        catch (AscException) { throw new AscException("invalid_result", "The provider returned an invalid document."); }
    }
    private static async Task<T> InvokeAsync<T>(McpClient transport, string prefix, string operation,
        AscStoreRef store, AscDocumentSelection selection, object request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var args = JsonSerializer.SerializeToElement(request, AscContract.JsonOptions);
        var wire = JsonSerializer.SerializeToElement(new AscDocumentInvocation<JsonElement>(store, selection, args), AscContract.JsonOptions);
        var result = await transport.CallToolAsync(prefix + "_" + operation,
            wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: timeout.Token);
        AscValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result", "Structured document results are required.");
        var raw = result.StructuredContent.Value;
        AscValidation.Require(raw.GetRawText().Length <= AscDocuments.MaximumResultBytes, "invalid_result", "Oversized document result.");
        var reply = AscValidation.Parse<AscReply<T>>(raw);
        AscValidation.Require(reply.AscVersion == AscContract.Version && reply.RequestId == args.GetProperty("requestId").GetString()
            && (reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null), "invalid_result", "Invalid document result envelope.");
        if (reply.Error is { } error)
        {
            AscValidation.Require(error.Code is { Length: > 0 and <= 128 } && error.Message is { Length: > 0 and <= 1024 }, "invalid_result", "Invalid error.");
            throw new AscException(error.Code, error.Message);
        }
        return reply.Data!;
    }
}
