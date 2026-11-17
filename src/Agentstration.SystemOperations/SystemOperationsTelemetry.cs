using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Agentstration.SystemOperations;

public sealed class SystemOperationsTelemetry : IDisposable
{
    public const string Name = "Agentstration.SystemOperations";
    private readonly Meter meter = new(Name);
    private readonly ActivitySource activities = new(Name);
    private readonly Counter<long> snapshots;
    public SystemOperationsTelemetry() => snapshots = meter.CreateCounter<long>("agentstration.system_operations.snapshots");
    public Activity? Start(string root) { var activity = activities.StartActivity("system-operations.snapshot"); activity?.SetTag("resource.graph_id", root); return activity; }
    public void Record(string kind, string coverage) => snapshots.Add(1,
        new KeyValuePair<string, object?>("resource.family", kind is "Flow" or "Agent" or "AgentRevision" ? kind : "other"),
        new KeyValuePair<string, object?>("evidence.coverage", coverage is "attested-window" or "bounded-sample" or "family-unsupported" or "run-read-required" or "unavailable" ? coverage : "unknown"));
    public void Dispose() { meter.Dispose(); activities.Dispose(); GC.SuppressFinalize(this); }
}
