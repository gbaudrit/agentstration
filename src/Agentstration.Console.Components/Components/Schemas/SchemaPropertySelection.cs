using System.Text.Json.Nodes;

namespace Agentstration.Web.Components.Schemas;

public sealed record SchemaPropertySelection(
    JsonObject ParentSchema,
    string Name,
    JsonObject Schema,
    int Depth);
