using Agentstration.Functions.Abstractions;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Functions;

public sealed class NativeFunctionExecutionContext : INativeFunctionExecutionContext
{
    private readonly AsyncLocal<string?> current = new();
    private readonly AsyncLocal<IReadOnlyDictionary<string, SchemaBindingSnapshot>?> schemas = new();
    public string? InvocationId => current.Value;
    public IReadOnlyDictionary<string, SchemaBindingSnapshot>? SchemaBindings => schemas.Value;
    public IDisposable Enter(string invocationId, IReadOnlyDictionary<string, SchemaBindingSnapshot>? bindings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        var previous = current.Value;
        var previousSchemas = schemas.Value;
        current.Value = invocationId;
        schemas.Value = bindings;
        return new Restoration(this, previous, previousSchemas);
    }
    private sealed class Restoration(NativeFunctionExecutionContext owner, string? previous,
        IReadOnlyDictionary<string, SchemaBindingSnapshot>? previousSchemas) : IDisposable
    {
        public void Dispose() { owner.current.Value = previous; owner.schemas.Value = previousSchemas; }
    }
}
