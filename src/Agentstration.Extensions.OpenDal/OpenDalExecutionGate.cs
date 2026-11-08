namespace Agentstration.Extensions.OpenDal;

/// <summary>Bounds native work and active transfer handles across every logical store in this host.</summary>
public sealed class OpenDalExecutionGate : IDisposable
{
    internal SemaphoreSlim Slots { get; } = new(32, 32);
    public void Dispose() => Slots.Dispose();
}
