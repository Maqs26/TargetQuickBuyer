using TargetDrop.Components;
using TargetDrop.Services;

var builder = WebApplication.CreateBuilder(args);

// Local-only: this app controls your logged-in Chrome, so never expose it to the network.
builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<BrowserManager>();
builder.Services.AddSingleton<DropWatcher>();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Console.WriteLine("Drop Desk running at http://localhost:5080");
app.Run();
