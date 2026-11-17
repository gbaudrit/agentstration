namespace Agentstration.SystemOperations.Api;

/// <summary>No unbounded subscription registry or pending queue.</summary>
public sealed class SystemOperationsSubscriptions
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> actors = [];
    private int total;
    public IDisposable? TryAcquire(Guid actor)
    {
        lock (gate)
        {
            var count = actors.GetValueOrDefault(actor);
            if (total >= 64 || count >= 8) return null;
            actors[actor] = count + 1; ++total;
            return new Lease(() => Release(actor));
        }
    }
    private void Release(Guid actor)
    {
        lock (gate)
        {
            var count = actors[actor] - 1;
            if (count == 0) actors.Remove(actor); else actors[actor] = count;
            --total;
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? remaining = release;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Invoke();
    }
}
