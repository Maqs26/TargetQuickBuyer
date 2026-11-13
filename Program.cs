using TargetDrop.Components;
using TargetDrop.Services;

var builder = WebApplication.CreateBuilder(args);

// Local-only: this app controls your logged-in Chrome, so never expose it to the network.
// (Override with --urls http://localhost:NNNN if 5080 is taken; keep it on localhost.)
var url = builder.Configuration["urls"] ?? "http://localhost:5080";
builder.WebHost.UseUrls(url);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<BrowserManager>();
builder.Services.AddSingleton<DropWatcher>();
builder.Services.AddSingleton<StockChecker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StockChecker>());

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Console.WriteLine($"Drop Desk running at {url}");
app.Run();
