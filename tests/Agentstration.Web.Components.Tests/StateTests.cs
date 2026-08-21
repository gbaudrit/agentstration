using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class StateTests
{
    [TestMethod]
    public void StatusPresentationUsesOperationalVocabulary()
    {
        Assert.AreEqual("Valid", StatusPresentation.Label("Accepted"));
        Assert.AreEqual("Published", StatusPresentation.Label("Succeeded"));
        Assert.AreEqual("Timed out", StatusPresentation.Label("TimedOut"));
        Assert.AreEqual(UiStatus.Success, StatusPresentation.Tone("Ready"));
        Assert.AreEqual(UiStatus.Danger, StatusPresentation.Tone("Failed"));
        Assert.AreEqual(UiStatus.Warning, StatusPresentation.Tone("Degraded"));
    }

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
    public async Task UserPreferencesStateLoadsAndPersistsSelectedTheme()
    {
        var client = new StubUserPreferencesClient(UserTheme.Light);
        var state = new UserPreferencesState(client);

        await state.LoadAsync(default);
        await state.SetThemeAsync(UserTheme.Dark, default);

        Assert.IsTrue(state.IsDarkTheme);
        Assert.IsTrue(state.IsLoaded);
        Assert.AreEqual(UserTheme.Dark, client.SavedTheme);
    }

    private sealed class StubUserPreferencesClient(UserTheme initialTheme) : IUserPreferencesClient
    {
        public UserTheme? SavedTheme { get; private set; }

        public Task<UserPreferences> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new UserPreferences(initialTheme, DateTimeOffset.UtcNow));

        public Task<UserPreferences> UpdateAsync(UserTheme theme, CancellationToken cancellationToken)
        {
            SavedTheme = theme;
            return Task.FromResult(new UserPreferences(theme, DateTimeOffset.UtcNow));
        }
    }
}
