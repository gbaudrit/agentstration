using System.Text.Json;

namespace Agentstration.DataSources.Contracts;

public static class DataSourceDefinitionSemantics
{
    public static bool ExecutionEquals(DataSourceProperties left, DataSourceProperties right) => JsonElement.DeepEquals(
        JsonSerializer.SerializeToElement(left with { DisplayName = string.Empty, Description = null }),
        JsonSerializer.SerializeToElement(right with { DisplayName = string.Empty, Description = null }));
}
