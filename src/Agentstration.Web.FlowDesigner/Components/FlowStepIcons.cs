namespace Agentstration.Web.FlowDesigner.Components;

internal static class FlowStepIcons
{
    public static string Name(string type) => type.ToLowerInvariant() switch
    {
        "input" or "entry" or "initial" => "entry",
        "agent" => "agent",
        "flow" => "workflow",
        "tool" => "wrench",
        "router" => "route",
        "condition" => "git-branch",
        "transform" => "arrows-exchange",
        "output" => "log-out",
        "failure" => "alert-triangle",
        "manager" => "users-group",
        "conversation" => "message",
        _ => "workflow"
    };
}
