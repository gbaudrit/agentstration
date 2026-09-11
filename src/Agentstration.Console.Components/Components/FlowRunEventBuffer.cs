using Agentstration.Flow;

namespace Agentstration.Web.Components;

internal sealed class FlowRunEventBuffer
{
    private readonly Lock syncRoot = new();
    private readonly List<FlowRunEvent> events = [];
    private readonly HashSet<long> sequences = [];
    private long lastSequence;

    public long LastSequence
    {
        get
        {
            lock (syncRoot) return lastSequence;
        }
    }

    public IReadOnlyList<FlowRunEvent> Snapshot()
    {
        lock (syncRoot) return events.ToArray();
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            events.Clear();
            sequences.Clear();
            lastSequence = 0;
        }
    }

    public bool TryAdd(FlowRunEvent runEvent)
    {
        lock (syncRoot)
        {
            if (!sequences.Add(runEvent.Sequence)) return false;
            lastSequence = Math.Max(lastSequence, runEvent.Sequence);
            events.Add(runEvent);
            return true;
        }
    }
}
