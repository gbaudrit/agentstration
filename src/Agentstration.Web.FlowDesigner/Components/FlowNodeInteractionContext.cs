namespace Agentstration.Web.FlowDesigner.Components;

public sealed record FlowNodeInteractionContext(Func<string, Task> OpenSettingsAsync,
    Func<string, string, Task>? InspectPortAsync = null,
    Func<string, string, string>? DescribePort = null,
    Func<string, string?>? DescribeResource = null);
