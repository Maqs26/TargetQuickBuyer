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
        var page = ctx.Pages.FirstOrDefault() ?? await ctx.NewPageAsync();
        await page.GotoAsync("https://www.target.com/account");
        await page.BringToFrontAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null) await _context.CloseAsync();
        _playwright?.Dispose();
    }
}
