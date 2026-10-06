namespace LaunchApp.Services;

public sealed record GameSection(string Id, string Title);
public sealed class GameNavigation
{
    public string? Route { get; private set; }
    public IReadOnlyList<GameSection> Sections { get; private set; } = [];
    public event Action? Changed;
    public void Set(string? route, params GameSection[] sections)
    {
        Route = route; Sections = sections; Changed?.Invoke();
    }
}
