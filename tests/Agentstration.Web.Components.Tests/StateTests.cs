using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class StateTests
{
    [TestMethod]
    public void NavigationStateTogglesSidebarAndNotifies()
    {
        var state = new NavigationState();
        var notifications = 0;
        state.Changed += () => notifications++;

        state.ToggleSidebar();

        Assert.IsTrue(state.IsSidebarCollapsed);
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public void NotificationStateTracksUnreadItems()
    {
        var state = new NotificationState();
        state.Add(new NotificationItem(Guid.NewGuid(), "Runtime", "Degraded", DateTimeOffset.UtcNow, UiStatus.Warning));

        Assert.AreEqual(1, state.UnreadCount);
        state.MarkAllRead();
        Assert.AreEqual(0, state.UnreadCount);
    }

    [TestMethod]
    public void UserPreferencesStateTogglesTheme()
    {
        var state = new UserPreferencesState();
        state.ToggleTheme();
        Assert.IsTrue(state.IsDarkTheme);
    }
}
