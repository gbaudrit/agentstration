namespace Agentstration.Domain;

public readonly record struct WorkspaceId(Guid Value)
{
    public static WorkspaceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct InboxId(Guid Value)
{
    public static InboxId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct ItemId(Guid Value)
{
    public static ItemId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct MissionId(Guid Value)
{
    public static MissionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct MissionRunId(Guid Value)
{
    public static MissionRunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}
