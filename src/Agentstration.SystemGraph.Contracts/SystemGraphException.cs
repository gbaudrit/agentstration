namespace Agentstration.SystemGraph;

/// <summary>Public failure code shared by callers of the neutral graph authorization and query ports.</summary>
public sealed class SystemGraphException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
