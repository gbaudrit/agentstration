using System.Text.Json;
using Agentstration.Asp.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Asp.Client;

/// <summary>Owns no transport and never automatically retries mutations.</summary>
public sealed class AspDocumentClient
{
    private readonly McpClient transport;
    private readonly string prefix;
    public AspDocumentDescription Description { get; }
    private AspDocumentClient(McpClient transport, string prefix, AspDocumentDescription description)
    { this.transport = transport; this.prefix = prefix; Description = description; }

    public static async Task<AspDocumentClient> ConnectAsync(McpClient transport, string prefix,
        AspStoreRef store, AspDocumentSelection selection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var tools = await transport.ListToolsAsync(cancellationToken: ct);
        AspValidation.Require(tools.Count <= 1024 && tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() == tools.Count,
            "incompatible_schema", "The document tool catalog contains duplicate mappings or exceeds its bound.");
        foreach (var operation in AspDocumentSchemas.Operations)
        {
            var tool = tools.SingleOrDefault(t => t.Name == prefix + "_" + operation)
                ?? throw new AspException("incompatible_schema", "A document mapping is missing.");
            AspValidation.Require(JsonElement.DeepEquals(tool.ProtocolTool.InputSchema, AspDocumentSchemas.Input(operation))
                && tool.ProtocolTool.OutputSchema is { } schema && JsonElement.DeepEquals(schema, AspDocumentSchemas.Output(operation)),
                "incompatible_schema", "The document mapping differs from its canonical schema.");
        }
        var description = await InvokeAsync<AspDocumentDescription>(transport, prefix, "describe", store, selection,
            new AspDescribeRequest(Guid.NewGuid().ToString("N")), ct);
        AspValidation.Require(description.AspVersion == AspProtocol.Version && description.Profile == AspDocuments.Profile,
            "incompatible_version", "The document profile is incompatible.");
        AspValidation.Require(description.Store == store && description.Selection == selection,
            "forbidden", "The provider changed the selected target.");
        AspDocumentValidation.Policy(description.Policy);
        AspValidation.Require(description.Policy.Collection == selection.Collection, "invalid_result", "The provider changed the collection policy.");
        AspValidation.Require(description.SchemaDigest == AspDocumentValidation.Digest(description.DocumentSchema)
            && description.QueryProfiles.Count <= 8
            && description.QueryProfiles.All(p => p.Reference.ModelDigest == description.SchemaDigest),
            "invalid_result", "Invalid model/schema evidence.");
        return new(transport, prefix, description);
    }
    public Task<AspDocument> GetAsync(AspDocumentGet request, CancellationToken ct = default) => CallAsync<AspDocument>("get", request, ct);
    public Task<AspDocument> PutAsync(AspDocumentPut request, CancellationToken ct = default) => CallAsync<AspDocument>("put", request, ct);
    public Task<AspDocumentDeleted> DeleteAsync(AspDocumentDelete request, CancellationToken ct = default) => CallAsync<AspDocumentDeleted>("delete", request, ct);
    public Task<AspDocumentPage> QueryAsync(AspDocumentQuery request, CancellationToken ct = default) => CallAsync<AspDocumentPage>("query", request, ct);
    public Task<AspDocumentPublication> PrepareAsync(AspDocumentPrepare request, CancellationToken ct = default) => CallAsync<AspDocumentPublication>("prepare", request, ct);
    public Task<AspDocumentPublication> SealAsync(AspDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AspDocumentPublication>("seal", request, ct);
    public Task<AspDocumentPublication> InspectAsync(AspDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AspDocumentPublication>("inspect", request, ct);
    public Task<AspDocumentPublication> RetireAsync(AspDocumentPublicationRequest request, CancellationToken ct = default) => CallAsync<AspDocumentPublication>("retire", request, ct);
    private async Task<T> CallAsync<T>(string operation, object request, CancellationToken ct)
    {
        var result = await InvokeAsync<T>(transport, prefix, operation, Description.Store, Description.Selection, request, ct);
        if (result is AspDocument document)
        {
            var expectedId = request switch { AspDocumentGet get => get.Id, AspDocumentPut put => put.Id, _ => null };
            Check(document);
            AspValidation.Require(document.Id == expectedId && (request is not AspDocumentGet { Revision: { } revision } || document.Revision == revision),
                "invalid_result", "The provider changed the exact document identity/revision.");
        }
        if (result is AspDocumentPage page && request is AspDocumentQuery query)
        {
            AspValidation.Require(page.Documents.Count <= query.PageSize && page.Groups.Count <= query.PageSize
                && (page.Documents.Count == 0 || page.Groups.Count == 0)
                && page.Documents.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == page.Documents.Count
                && (page.Cursor is null || page.Cursor.Length is > 0 and <= 4096), "invalid_result", "The provider exceeded the page contract.");
            foreach (var item in page.Documents) Check(item);
        }
        if (result is AspDocumentDeleted deleted && request is AspDocumentDelete removal)
            AspValidation.Require(deleted.Id == removal.Id && deleted.Revision == removal.ExpectedRevision, "invalid_result", "The provider changed deletion evidence.");
        if (result is AspDocumentPublication publication)
        {
            var expected = request switch { AspDocumentPrepare prepare => prepare.PublicationId, AspDocumentPublicationRequest operationRequest => operationRequest.PublicationId, _ => null };
            AspValidation.Require(publication.Id == expected && publication.Count is >= 0 and <= 100
                && publication.Digest is { Length: 64 } && publication.SchemaDigest == Description.SchemaDigest && publication.Contract == AspDocuments.PublicationContract,
                "invalid_result", "The provider changed immutable publication evidence.");
        }
        return result;
    }
    private void Check(AspDocument document)
    {
        try { AspDocumentValidation.Identity(document.Id); AspDocumentValidation.Identity(document.Revision); AspDocumentValidation.Document(document.Value, Description.Policy.MaximumDocumentBytes); }
        catch (AspException) { throw new AspException("invalid_result", "The provider returned an invalid document."); }
    }
    private static async Task<T> InvokeAsync<T>(McpClient transport, string prefix, string operation,
        AspStoreRef store, AspDocumentSelection selection, object request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var args = JsonSerializer.SerializeToElement(request, AspProtocol.JsonOptions);
        var wire = JsonSerializer.SerializeToElement(new AspDocumentInvocation<JsonElement>(store, selection, args), AspProtocol.JsonOptions);
        var result = await transport.CallToolAsync(prefix + "_" + operation,
            wire.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: timeout.Token);
        AspValidation.Require(result.StructuredContent is { ValueKind: JsonValueKind.Object }, "invalid_result", "Structured document results are required.");
        var raw = result.StructuredContent.Value;
        AspValidation.Require(raw.GetRawText().Length <= AspDocuments.MaximumResultBytes, "invalid_result", "Oversized document result.");
        var reply = AspValidation.Parse<AspReply<T>>(raw);
        AspValidation.Require(reply.AspVersion == AspProtocol.Version && reply.RequestId == args.GetProperty("requestId").GetString()
            && (reply.Error is not null) == (result.IsError == true)
            && (reply.Error is null ? reply.Data is not null : reply.Data is null), "invalid_result", "Invalid document result envelope.");
        if (reply.Error is { } error)
        {
            AspValidation.Require(error.Code is { Length: > 0 and <= 128 } && error.Message is { Length: > 0 and <= 1024 }, "invalid_result", "Invalid error.");
            throw new AspException(error.Code, error.Message);
        }
        return reply.Data!;
    }
}
