using LaunchApp.Services;
using Microsoft.AspNetCore.Components;

namespace LaunchApp.Shared;

public abstract class GamePageBase : ComponentBase
{
    [Inject] protected GameNavigation GameNavigation { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Parameter] public string? Section { get; set; }
    [SupplyParameterFromQuery(Name = "section")] public string? QuerySection { get; set; }
    protected abstract string GameRoute { get; }
    protected abstract GameSection[] PageSections { get; }
    protected override void OnParametersSet()
    {
        GameNavigation.Set(GameRoute, PageSections);
        var fragment = new Uri(GameNavigation.Location).Fragment.TrimStart('#');
        var explicitRequest = Section ?? QuerySection;
        var requested = explicitRequest ?? (fragment.Length > 0 ? Uri.UnescapeDataString(fragment) : null);
        if (requested is null) return;
        var selected = GameNavigation.Sections.FirstOrDefault(s => s.Id.Equals(requested, StringComparison.OrdinalIgnoreCase))?.Id;
        if (selected is null && explicitRequest is null) return;
        selected ??= "overview";
        var target = GameRoute + "#" + selected;
        if (Navigation.ToBaseRelativePath(GameNavigation.Location) != target)
            Navigation.NavigateTo(target, replace: true);
    }
}
