using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;


var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<LaunchApp.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddMasaBlazor(builder =>
{ 
    builder.ConfigureTheme(theme =>
    {
        theme.Themes.Light.Primary = "#006fee";
        theme.Themes.Light.Accent = "#006fee";
        theme.Themes.Dark.Primary = "#08628b";
        theme.Themes.Dark.Accent = "#006fee";
    });
});

builder.Services.AddScoped<LaunchApp.Services.GameNavigation>();

await builder.Build().RunAsync();
