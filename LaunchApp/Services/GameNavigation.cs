using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace LaunchApp.Services;

public sealed record GameSection(string Id, string Title);

/// <summary>One source for the page's section links and selection, including direct links and history.</summary>
public sealed class GameNavigation : IDisposable
{
    private readonly NavigationManager navigation;
    private Func<Task>? pageAction;
    private string? pageActionLabel;
    private long pageActionId;
    public string Location { get; private set; }
    public string? Route { get; private set; }
    public string ActiveSection { get; private set; } = "overview";
    public IReadOnlyList<GameSection> Sections { get; private set; } = [];
    /// <summary>The primary action supplied by the currently rendered game page.</summary>
    public string? PageActionLabel => pageActionLabel;
    public bool HasPageAction => pageAction is not null;
    public event Action? Changed;
    public GameNavigation(NavigationManager navigation)
    {
        this.navigation = navigation;
        Location = navigation.Uri;
        navigation.LocationChanged += LocationChanged;
    }
    public void Set(string? route, params GameSection[] sections)
    {
        var routeChanged = route != Route;
        if (routeChanged) ClearPageAction(notify: false);
        if (route != Route || new Uri(Location).PathAndQuery != new Uri(navigation.Uri).PathAndQuery)
            Location = navigation.Uri;
        var next = route is null ? [] : sections.Append(new GameSection("launch", "Launch settings")).ToArray();
        var changed = routeChanged || !Sections.SequenceEqual(next);
        Route = route; Sections = next;
        changed |= UpdateSelection();
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Lets a routed page own the navbar's primary action without the layout knowing how that
    /// page launches a game. The returned handle prevents an old page from clearing a newer one.
    /// </summary>
    public IDisposable RegisterPageAction(string label, Func<Task> action)
    {
        var id = ++pageActionId;
        pageActionLabel = label;
        pageAction = action;
        Changed?.Invoke();
        return new PageActionRegistration(this, id);
    }

    public Task InvokePageActionAsync() => pageAction?.Invoke() ?? Task.CompletedTask;

    private void ClearPageAction(bool notify)
    {
        if (pageAction is null) return;
        pageAction = null;
        pageActionLabel = null;
        pageActionId++;
        if (notify) Changed?.Invoke();
    }

    private void ReleasePageAction(long id)
    {
        if (id != pageActionId) return;
        ClearPageAction(notify: true);
    }

    private sealed class PageActionRegistration(GameNavigation owner, long id) : IDisposable
    {
        private GameNavigation? owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            current?.ReleasePageAction(id);
        }
    }
    private bool UpdateSelection()
    {
        var fragment = Uri.UnescapeDataString(new Uri(Location).Fragment.TrimStart('#'));
        var selected = Sections.FirstOrDefault(s => s.Id.Equals(fragment, StringComparison.OrdinalIgnoreCase))?.Id;
        if (selected is null)
        {
            // #main and other non-section fragments must not make an invalid section active.
            // Preserve the current selection only when it belongs to this route.
            selected = fragment.Length != 0 && Sections.Any(s => s.Id.Equals(ActiveSection, StringComparison.OrdinalIgnoreCase))
                ? ActiveSection
                : Sections.FirstOrDefault()?.Id ?? "overview";
        }
        if (ActiveSection == selected) return false;
        ActiveSection = selected; return true;
    }
    private void LocationChanged(object? sender, LocationChangedEventArgs e)
    {
        RefreshLocation(e.Location);
    }
    public void RefreshLocation(string location)
    {
        if (!location.StartsWith(navigation.BaseUri, StringComparison.Ordinal)) return;
        Location = location;
        var page = navigation.ToBaseRelativePath(location).Split('?', '#')[0].Split('/')[0];
        if (Route is not null && !Route.Equals(page, StringComparison.OrdinalIgnoreCase))
        { Set(null); return; }
        UpdateSelection();
        Changed?.Invoke();
    }
    public void Dispose() => navigation.LocationChanged -= LocationChanged;
}
