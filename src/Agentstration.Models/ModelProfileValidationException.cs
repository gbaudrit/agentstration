namespace Agentstration.Models;

public sealed class ModelProfileValidationException(string code, string message, IReadOnlyDictionary<string, string[]>? errors = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();
}
