using System.Text.Json;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeDefinitionSemantics
{
    public static KnowledgeSourceProperties ExecutionDefinition(KnowledgeSourceProperties value) => value with
    {
        DisplayName = string.Empty,
        Description = null,
        DataSources = value.DataSources.Select(binding => binding with { Description = null }).ToArray()
    };

    public static bool ExecutionEquals(KnowledgeSourceProperties left, KnowledgeSourceProperties right) =>
        JsonElement.DeepEquals(JsonSerializer.SerializeToElement(ExecutionDefinition(left)),
            JsonSerializer.SerializeToElement(ExecutionDefinition(right)));
}
