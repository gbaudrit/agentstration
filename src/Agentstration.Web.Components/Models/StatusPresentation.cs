namespace Agentstration.Web.Components.Models;

public static class StatusPresentation
{
    public static string Label(string? value) => value switch
    {
        "Accepted" => "Valid",
        "Succeeded" => "Published",
        "TimedOut" => "Timed out",
        null or "" => "Unknown",
        _ => value
    };

    public static UiStatus Tone(string? value) => value switch
    {
        "Succeeded" or "Ready" or "Published" => UiStatus.Success,
        "Failed" or "Unavailable" or "TimedOut" => UiStatus.Danger,
        "Degraded" => UiStatus.Warning,
        "Validating" or "Creating" or "Updating" or "Starting" or "Deploying" or "Running" => UiStatus.Info,
        _ => UiStatus.Neutral
    };
}
