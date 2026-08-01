using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Components.State;

public sealed class PlatformStatusState
{
    public UiStatus Status { get; private set; } = UiStatus.Neutral;
    public string Label { get; private set; } = "Connecting";
    public event Action? Changed;

    public void Set(UiStatus status, string label)
    {
        Status = status;
        Label = label;
        Changed?.Invoke();
    }
}

