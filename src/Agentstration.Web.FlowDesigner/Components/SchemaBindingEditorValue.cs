using System.Text.Json;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Web.FlowDesigner.Components;

public sealed record SchemaBindingEditorValue(SchemaBinding? Binding, JsonElement? EffectiveContent);
