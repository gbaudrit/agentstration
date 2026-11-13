namespace Agentstration.Runtime.Abstractions;

/// <summary>Payload persistence boundary for Flow assignments consuming direct source content.</summary>
public interface IRuntimeAssignmentPayloadPersistence
{
    RuntimeWorkerAssignment Store(RuntimeWorkerAssignment assignment);
    RuntimeWorkerAssignment Restore(RuntimeWorkerAssignment assignment);
}
