using Agentstration.Web.Components.Models;

namespace Agentstration.Web.Components.State;

public sealed class NotificationState
{
    private readonly List<NotificationItem> _items = [];
    public IReadOnlyList<NotificationItem> Items => _items;
    public int UnreadCount => _items.Count(item => !item.IsRead);
    public bool IsPanelOpen { get; private set; }
    public event Action? Changed;

    public void Add(NotificationItem item)
    {
        _items.Insert(0, item);
        Changed?.Invoke();
    }

    public void TogglePanel()
    {
        IsPanelOpen = !IsPanelOpen;
        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        for (var index = 0; index < _items.Count; index++) _items[index] = _items[index] with { IsRead = true };
        Changed?.Invoke();
    }
}

