namespace Agentstration.Work;

public static class WorkplaceTaskIdentity
{
    public const string OriginMetadata = "origin";
    public const string TriggerOrigin = "trigger";
    public const string EntryMetadata = "workplace.entryId";
    public const string InteractionMetadata = "workplace.interactionId";
    public const string AnchorTaskMetadata = "workplace.taskId";

    public static bool IsTask(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.TaskHierarchy is not null) return true;

        if (string.Equals(item.Metadata.GetValueOrDefault(OriginMetadata), TriggerOrigin, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(item.Metadata.GetValueOrDefault(EntryMetadata))
            && Guid.TryParse(item.Metadata.GetValueOrDefault(InteractionMetadata), out _);
    }
}
