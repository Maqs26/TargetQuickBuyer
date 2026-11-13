using Microsoft.Playwright;

namespace TargetDrop.Services;

/// <summary>
/// Quietly revisits every saved product on a timer so each card shows whether it's
/// Live, Sold out or Coming soon. Runs only while Drop Desk's Chrome is open (it never
/// pops Chrome up on its own), and steps aside for products a watch is already refreshing.
/// When something flips to live it alerts you, and optionally goes for the cart.
/// </summary>
public class StockChecker : BackgroundService
{
    private readonly BrowserManager _browser;
    private readonly DropWatcher _watcher;
    private readonly SemaphoreSlim _kick = new(0, 1);
    private IPage? _page;

    public StockChecker(BrowserManager browser, DropWatcher watcher)
    {
        _browser = browser;
        _watcher = watcher;
    }

    public bool Running { get; private set; }
    public DateTime? NextSweep { get; private set; }

    /// <summary>Check everything now (opens Chrome if it isn't already).</summary>
    public void CheckNow()
    {
        _forceOpen = true;
        if (_kick.CurrentCount == 0) _kick.Release();
    }

    private volatile bool _forceOpen;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var minutes = _watcher.Settings.StockCheckMinutes;
            var shouldRun = _forceOpen || (minutes > 0 && _browser.IsOpen);

            if (shouldRun)
            {
                _forceOpen = false;
                try { await SweepAsync(stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception e) { _watcher.AddLog($"Stock check hiccup: {e.Message.Split('\n')[0]}"); }
            }

            // Sleep until the next sweep, but wake early for "Check now" or when a new product is
            // added (so a fresh link gets its badge within a few seconds).
            var wait = TimeSpan.FromMinutes(Math.Max(0.5, minutes <= 0 ? 1 : minutes));
            NextSweep = minutes > 0 && _browser.IsOpen ? DateTime.Now + wait : null;
            var until = DateTime.Now + wait;
            while (DateTime.Now < until && !stop.IsCancellationRequested)
            {
                if (await _kick.WaitAsync(TimeSpan.FromSeconds(3), stop)) break;
                if (_browser.IsOpen && Due().Any(i => i.StockCheckedAt is null)) break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken stop)
    {
        var items = Due().OrderBy(i => i.StockCheckedAt ?? DateTime.MinValue).ToList();
        if (items.Count == 0) return;

        Running = true;
        try
        {
            foreach (var item in items)
            {
                stop.ThrowIfCancellationRequested();
                if (_watcher.IsActive(item) || !_watcher.Items.Contains(item)) continue;

                if (_page is not { IsClosed: false }) _page = await _browser.NewFastPageAsync();
                if (!await CheckOneAsync(_page, item, stop)) break;

                // Space checks out; this is a background job, not a race.
                await Task.Delay(TimeSpan.FromSeconds(3), stop);
            }
        }
        finally
        {
            Running = false;
            // Close the tab between sweeps so it isn't in your way.
            if (_page is { IsClosed: false }) { try { await _page.CloseAsync(); } catch { } }
            _page = null;
        }
    }

    /// <summary>Products worth a stock check: not already in your cart and not being hammered by a watch.</summary>
    private IEnumerable<WatchItem> Due() =>
        _watcher.Items.Where(i => i.Status != ItemStatus.Added && !_watcher.IsActive(i));

    /// <summary>Returns false when the sweep should stop early (Target is rate-limiting).</summary>
    private async Task<bool> CheckOneAsync(IPage page, WatchItem item, CancellationToken stop)
    {
        try
        {
            var resp = await page.GotoAsync(item.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            if (resp is { Status: 429 or 403 })
            {
                _watcher.AddLog("Target is rate-limiting; stock checks paused until the next sweep.");
                return false;
            }

            await _watcher.ReadDetailsAsync(page, item);
            var (state, _) = await DropWatcher.ProbeAsync(page, TimeSpan.FromSeconds(6), stop);
            if (!_watcher.SetStock(item, state)) return true;

            // It just went live.
            _watcher.AnnounceLive(item);
            if (_watcher.Settings.AutoBuyWhenLive) _watcher.BuyNow(item);
        }
        catch (PlaywrightException) when (page.IsClosed)
        {
            // You closed the tab or Chrome mid-check; the next sweep reopens it.
            return false;
        }
        catch (Exception e) when (e is TimeoutException or PlaywrightException)
        {
            item.StockCheckedAt = DateTime.Now; // don't hot-loop on a page that won't load
        }
        return true;
    }
}
