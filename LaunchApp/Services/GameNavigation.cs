using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace LaunchApp.Services;

public sealed record GameSection(string Id, string Title);

/// <summary>One source for the page's section links and selection, including direct links and history.</summary>
public sealed class GameNavigation : IDisposable
{
    private readonly NavigationManager navigation;
    public string Location { get; private set; }
    public string? Route { get; private set; }
    public string ActiveSection { get; private set; } = "overview";
    public IReadOnlyList<GameSection> Sections { get; private set; } = [];
    public event Action? Changed;
    public GameNavigation(NavigationManager navigation)
    {
        this.navigation = navigation;
        Location = navigation.Uri;
        navigation.LocationChanged += LocationChanged;
    }
    public void Set(string? route, params GameSection[] sections)
    {
        if (route != Route || new Uri(Location).PathAndQuery != new Uri(navigation.Uri).PathAndQuery)
            Location = navigation.Uri;
        var next = route is null ? [] : sections.Append(new GameSection("launch", "Launch settings")).ToArray();
        var changed = Route != route || !Sections.SequenceEqual(next);
        Route = route; Sections = next;
        changed |= UpdateSelection();
        if (changed) Changed?.Invoke();
    }
    private bool UpdateSelection()
    {
        var fragment = Uri.UnescapeDataString(new Uri(Location).Fragment.TrimStart('#'));
        var selected = Sections.FirstOrDefault(s => s.Id.Equals(fragment, StringComparison.OrdinalIgnoreCase))?.Id
            ?? Sections.FirstOrDefault()?.Id ?? "overview";
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
