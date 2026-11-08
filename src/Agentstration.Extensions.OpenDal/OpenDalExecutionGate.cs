namespace Agentstration.Extensions.OpenDal;

/// <summary>Bounds native work and state buffers across every logical store in this host.</summary>
public sealed class OpenDalExecutionGate : IDisposable
{
    internal SemaphoreSlim Slots { get; } = new(4, 4);
    public void Dispose() => Slots.Dispose();
}
