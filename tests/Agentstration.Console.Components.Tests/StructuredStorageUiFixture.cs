using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Tests;

internal sealed class StructuredStorageUiFixture(bool collection) : HttpMessageHandler, IConsoleNavigationContext
{
    internal Guid Collection { get; } = Guid.NewGuid();
    internal StorageTypeResource Type { get; } = CreateType();
    internal StorageResource Storage => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = StorageResourceKinds.Storage,
        Metadata = new() { Name = "documents" },
        ScopeRef = ResourceScopeRef.Instance,
        Uid = Type.Uid,
        Generation = 1,
        Definition = new()
        {
            DisplayName = "Documents",
            Configuration = JsonSerializer.SerializeToElement(new { }),
            Envelope = JsonSerializer.SerializeToElement(new { }),
            Type = new() { Type = new(Type.Name, ResourceScopeRef.Instance), TypeUid = Type.Uid, Version = "1", SchemaDigest = "schema", ContractDigest = "contract" },
            Collections = collection ? [new() { Name = "alerts", DisplayName = "Alerts", Uid = Collection, Policy = new(Publish: true, QueryProfiles: ["odata/4.01"]) }] : []
        },
        ObservedState = new(StorageAvailability.Ready, DateTimeOffset.UtcNow, "binding", [])
    };
    public string ScopeRef => "/instance";
    public string AddToUri(string uri) => uri;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath == "/api/storage-types" ? JsonContent.Create(new { value = new[] { Type } }) : JsonContent.Create(new { value = new[] { Storage } }) });
    private static StorageTypeResource CreateType() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = StorageResourceKinds.StorageType,
        Metadata = new() { Name = "documents" },
        ScopeRef = ResourceScopeRef.Instance,
        Uid = Guid.NewGuid(),
        Definition = new()
        {
            DisplayName = "Documents",
            PreferredVersion = "1",
            Versions = [new() { Version = "1", SchemaDigest = "schema", EnvelopeSchemaDigest = "envelope",
                ConfigurationSchema = JsonSerializer.SerializeToElement(new { type = "object" }), EnvelopeSchema = JsonSerializer.SerializeToElement(new { type = "object" }),
                Documents = new(true, true, true, true, true, [new("odata/4.01", "1.0", "odata-options+json")]) }]
        }
    };
}
