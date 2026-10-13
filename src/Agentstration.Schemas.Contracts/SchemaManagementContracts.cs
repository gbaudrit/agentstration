using Agentstration.Resources;
using Agentstration.Schemas;

namespace Agentstration.Schemas.Contracts;

public sealed record CreateSchemaRequest(
    string Name,
    SchemaProperties Properties,
    ResourceNamespace? Namespace = null,
    ResourceScopeRef? ScopeRef = null);

public sealed record PutSchemaRequest(SchemaProperties Properties);
public sealed record PublishSchemaVersionRequest(string Version);
