namespace Agentstration.Work;

public readonly record struct WorkItemId(Guid Value)
{
    public static WorkItemId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct WorkExecutionId(Guid Value)
{
    public static WorkExecutionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString();
}

public readonly record struct WorkCorrelationId(string Value)
{
    public static WorkCorrelationId New() => new(Guid.NewGuid().ToString("N"));
    public override string ToString() => Value;
}
