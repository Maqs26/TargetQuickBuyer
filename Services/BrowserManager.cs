using Microsoft.Playwright;

namespace TargetDrop.Services;

/// <summary>
/// Owns one real Chrome window with its own saved profile (./chrome-profile),
/// so your Target login, address and card stay remembered between runs.
/// </summary>
public class BrowserManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _profileDir = Path.Combine(Directory.GetCurrentDirectory(), "chrome-profile");
    private IPlaywright? _playwright;
    private IBrowserContext? _context;

    /// <summary>True while Drop Desk's Chrome window is open.</summary>
    public bool IsOpen => _context is not null;

    /// <summary>
    /// A tab for refreshing product pages. Images, video and fonts are skipped so each
    /// reload is as fast as possible (and light on Target).
    /// </summary>
    public async Task<IPage> NewFastPageAsync()
    {
        var ctx = await GetContextAsync();
        var p = await ctx.NewPageAsync();
        await p.RouteAsync("**/*", route =>
            route.Request.ResourceType is "image" or "media" or "font"
                ? route.AbortAsync()
                : route.ContinueAsync());
        return p;
    }

    public async Task<IBrowserContext> GetContextAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_context != null) return _context;

            _playwright ??= await Playwright.CreateAsync();
            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDir, new()
            {
                Channel = "chrome",          // uses your installed Google Chrome
                Headless = false,
                ViewportSize = ViewportSize.NoViewport
            });
            _context.Close += (_, _) => _context = null;
            return _context;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task OpenLoginAsync()
    {
        var ctx = await GetContextAsync();
        // Use the blank start tab if there is one; never hijack the tab the watcher is refreshing.
        var page = ctx.Pages.FirstOrDefault(p => p.Url == "about:blank") ?? await ctx.NewPageAsync();
        await page.GotoAsync("https://www.target.com/account");
        await page.BringToFrontAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
