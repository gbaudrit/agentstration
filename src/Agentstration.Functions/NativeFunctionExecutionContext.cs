using Agentstration.Functions.Abstractions;

namespace Agentstration.Functions;

public sealed class NativeFunctionExecutionContext : INativeFunctionExecutionContext
{
    private readonly AsyncLocal<string?> current = new();
    public string? InvocationId => current.Value;
    public IDisposable Enter(string invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        var previous = current.Value;
        current.Value = invocationId;
        return new Restoration(this, previous);
    }
    private sealed class Restoration(NativeFunctionExecutionContext owner, string? previous) : IDisposable
    {
        public void Dispose() => owner.current.Value = previous;
    }
}
