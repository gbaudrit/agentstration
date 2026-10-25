namespace Agentstration.Web.FlowDesigner.Components;

public sealed record FlowNodeInteractionContext(Func<string, Task> OpenSettingsAsync);
