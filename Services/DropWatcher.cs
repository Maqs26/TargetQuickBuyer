using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TargetDrop.Services;

public enum WatcherMode { Idle, Armed, Watching }

/// <summary>
/// Refreshes product pages in your own Chrome, clicks "Add to cart" the moment it is live,
/// confirms Target actually accepted it, then opens checkout and raises every alarm it can.
/// It never places the order: you finish checkout yourself.
/// </summary>
public class DropWatcher
{
    // Target's add-to-cart button. data-test is the stable hook; the id prefix is a fallback.
    private const string AddToCartSelector =
        "button[data-test=\"shippingButton\"], button[id^=\"addToCartButtonOrTextIdFor\"]";

    private static readonly Regex AddToCartText = new(@"^\s*(add to cart|preorder|pre-order)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex ComingSoonText = new(@"coming soon|available (on|starting)|releases on", RegexOptions.IgnoreCase);
    private static readonly Regex SoldOutText = new(@"out of stock|sold out|notify me when|isn.t available", RegexOptions.IgnoreCase);
    private static readonly Regex AddedConfirmText = new("added to cart", RegexOptions.IgnoreCase);

    private readonly BrowserManager _browser;
    private readonly StateStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _logSync = new();
    private readonly List<string> _log = new();
    private readonly object _runSync = new();
    private readonly HashSet<string> _active = new();
    private CancellationTokenSource? _cts;

    public DropWatcher(BrowserManager browser, StateStore store)
    {
        _browser = browser;
        _store = store;
    }

    public event Action? Changed;
    public event Action<WatchItem>? ItemAdded;
    public event Action<WatchItem>? ItemLive;

    public WatcherMode Mode { get; private set; } = WatcherMode.Idle;
    public DateTime StartsAt { get; private set; }
    public DateTime DropAt { get; private set; }
    public DateTime GiveUpAt { get; private set; }
    public WatchItem? LastSuccess { get; private set; }

    public DropSettings Settings => _store.State.Settings;

    public List<WatchItem> Items
    {
        get { lock (_store.Sync) return _store.State.Items.ToList(); }
    }

    public List<string> Log
    {
        get { lock (_logSync) return _log.ToList(); }
    }

    public bool IsActive(WatchItem item)
    {
        lock (_runSync) return Mode != WatcherMode.Idle && _active.Contains(item.Id);
    }

    // ---------- list management ----------

    /// <summary>Adds a product link. Returns (item, null) on success or when it's already listed, (null, error) otherwise.</summary>
    public (WatchItem? item, string? error) AddItem(string raw)
    {
        if (!TryNormalize(raw, out var url, out var uri))
            return (null, "That isn't a target.com product link. Copy it from the product page's address bar.");

        WatchItem item;
        lock (_store.Sync)
        {
            var existing = _store.State.Items.FirstOrDefault(i => SameProduct(i.Url, url));
            if (existing is not null) return (existing, null);
            item = new WatchItem { Url = url, Label = LabelFrom(uri) };
            _store.State.Items.Add(item);
        }
        _store.Save();
        AddLog($"Added {item.Label}");
        return (item, null);
    }

    public void RemoveItem(string id)
    {
        lock (_store.Sync) _store.State.Items.RemoveAll(i => i.Id == id);
        lock (_runSync) _active.Remove(id);
        _store.Save();
        Raise();
    }

    public void SaveSettings()
    {
        var s = Settings;
        s.RefreshSeconds = Math.Clamp(s.RefreshSeconds, 2, 120);
        s.StartEarlyMinutes = Math.Clamp(s.StartEarlyMinutes, 0, 120);
        s.GiveUpAfterMinutes = Math.Clamp(s.GiveUpAfterMinutes, 1, 720);
        s.StockCheckMinutes = Math.Clamp(s.StockCheckMinutes, 0, 120);
        _store.Save();
        Raise();
    }

    // ---------- start / stop ----------

    /// <summary>
    /// The one-click path: paste a link, and it goes for the cart right now. If the item
    /// isn't live yet it keeps refreshing until it is (or the give-up window ends).
    /// If a watch is already running, the item simply joins it.
    /// </summary>
    public string? BuyNow(string raw)
    {
        var (item, error) = AddItem(raw);
        if (item is null) return error;
        BuyNow(item);
        return null;
    }

    public void BuyNow(WatchItem item)
    {
        if (item.Status == ItemStatus.Added) item.Status = ItemStatus.Waiting;
        lock (_runSync)
        {
            if (Mode != WatcherMode.Idle)
            {
                if (_active.Add(item.Id))
                {
                    item.Status = ItemStatus.Waiting;
                    item.Checks = 0;
                    item.Note = null;
                    AddLog($"Joined the current watch: {item.Label}");
                }
                // Already armed for later? Go now.
                if (Mode == WatcherMode.Armed) StartsAt = DateTime.Now;
                return;
            }
        }
        Start(watchNow: true, only: new[] { item });
    }

    public void Start(bool watchNow, IEnumerable<WatchItem>? only = null)
    {
        var targets = (only ?? Items).ToList();
        if (targets.Count == 0) return;

        lock (_runSync)
        {
            if (Mode != WatcherMode.Idle) return;

            _active.Clear();
            foreach (var i in targets)
            {
                _active.Add(i.Id);
                i.Status = ItemStatus.Waiting;
                i.Checks = 0;
                i.Note = null;
            }
            LastSuccess = null;

            var now = DateTime.Now;
            if (watchNow)
            {
                StartsAt = DropAt = now;
            }
            else
            {
                var time = Settings.DropTimeValue;
                var drop = now.Date + time.ToTimeSpan();
                if (drop.AddMinutes(Settings.GiveUpAfterMinutes) < now) drop = drop.AddDays(1);
                DropAt = drop;
                StartsAt = drop.AddMinutes(-Settings.StartEarlyMinutes);
            }
            GiveUpAt = DropAt.AddMinutes(Math.Max(1, Settings.GiveUpAfterMinutes));

            _cts = new CancellationTokenSource();
            Mode = WatcherMode.Armed;
        }

        AddLog(watchNow
            ? (targets.Count == 1 ? $"Going for {targets[0].Label}" : $"Watching {targets.Count} products now")
            : $"Armed for {DropAt:ddd h:mm tt}");
        var token = _cts.Token;
        _ = Task.Run(() => RunAsync(token));
    }

    public void Stop() => _cts?.Cancel();

    // ---------- the loop ----------

    private List<WatchItem> Pending()
    {
        lock (_runSync)
            return Items.Where(i => _active.Contains(i.Id) && i.Status != ItemStatus.Added).ToList();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        IPage? page = null;
        var anyAdded = false;
        try
        {
            // Sleep in short chunks so a laptop that dozed off still wakes on time,
            // and so "Buy now" can pull the start time forward.
            while (DateTime.Now < StartsAt)
            {
                var left = (StartsAt - DateTime.Now).TotalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(left, 0.1, 5)), ct);
            }

            Mode = WatcherMode.Watching;
            Raise();
            if (DropAt > DateTime.Now.AddSeconds(5))
            {
                AddLog("Refreshing started");
                _ = PushAsync("Drop Desk is watching now.");
            }

            while (DateTime.Now < GiveUpAt)
            {
                var pending = Pending();
                if (pending.Count == 0) break;

                foreach (var item in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Status == ItemStatus.Added || !Pending().Contains(item)) continue; // removed mid-run

                    var started = Stopwatch.StartNew();
                    page = await EnsureWatchPageAsync(page);
                    var result = await TryAddAsync(page, item, ct);

                    if (result == AttemptResult.Added)
                    {
                        anyAdded = true;
                        await OnAddedAsync(item);
                        continue; // no delay: move straight on to the next product
                    }
                    if (result == AttemptResult.RateLimited)
                    {
                        AddLog("Target is rate-limiting this browser; pausing 60s. Consider a longer refresh delay.");
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                        continue;
                    }

                    // Refresh delay is measured from the start of this check, so slow page loads
                    // don't stretch the cycle. Spread evenly across the list.
                    var perItem = Math.Max(2.0, Settings.RefreshSeconds) / Math.Max(1, pending.Count);
                    var wait = TimeSpan.FromSeconds(Math.Max(1.0, perItem)) - started.Elapsed;
                    if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                }
            }

            if (Pending().Count > 0)
            {
                AddLog("Watch window ended before everything went live");
                await PushAsync("Drop window closed. Not everything went live.");
            }
            else if (anyAdded)
            {
                AddLog("Everything you asked for is in your cart");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Stopped");
        }
        catch (Exception e)
        {
            AddLog($"Watcher error: {FirstLine(e)}");
            await PushAsync($"Drop Desk hit an error: {FirstLine(e)}");
        }
        finally
        {
            if (page is { IsClosed: false }) { try { await page.CloseAsync(); } catch { } }
            lock (_store.Sync)
                foreach (var i in _store.State.Items.Where(i => i.Status == ItemStatus.Checking))
                    i.Status = ItemStatus.NotLive;
            lock (_runSync)
            {
                Mode = WatcherMode.Idle;
                _active.Clear();
            }
            _store.Save();
            Raise();
        }
    }

    /// <summary>One reusable tab for refreshing; reopened if you closed it or the whole Chrome window.</summary>
    private async Task<IPage> EnsureWatchPageAsync(IPage? page) =>
        page is { IsClosed: false } ? page : await _browser.NewFastPageAsync();

    private enum AttemptResult { NotLive, Added, RateLimited, Failed }

    private async Task<AttemptResult> TryAddAsync(IPage page, WatchItem item, CancellationToken ct)
    {
        item.Status = ItemStatus.Checking;
        item.LastChecked = DateTime.Now;
        item.Checks++;
        Raise();

        try
        {
            var resp = await page.GotoAsync(item.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            if (resp is { Status: 429 or 403 } || await LooksBlockedAsync(page))
            {
                item.Status = ItemStatus.NotLive;
                Raise();
                return AttemptResult.RateLimited;
            }

            // Pick up the real product name, price and picture the first time we see the page.
            if (item.ImageUrl is null || item.Label.StartsWith("Item ", StringComparison.Ordinal))
                await ReadDetailsAsync(page, item);

            // Wait for the buy box to render and the button to enable, polling quickly so a live
            // button is clicked within ~150ms of appearing. Give up after a few seconds: not live.
            var (stock, btn) = await ProbeAsync(page, TimeSpan.FromSeconds(5), ct);
            SetStock(item, stock);
            if (btn is null)
            {
                item.Status = ItemStatus.NotLive;
                item.Note = null;
                Raise();
                return AttemptResult.NotLive;
            }

            var (ok, note) = await ClickAndConfirmAsync(page, btn);
            item.Note = note;
            if (!ok)
            {
                item.Status = ItemStatus.NotLive;
                if (note is not null) AddLog($"{item.Label}: {note}");
                Raise();
                return AttemptResult.NotLive;
            }
            return AttemptResult.Added;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (page.IsClosed)
        {
            item.Status = ItemStatus.NotLive;
            AddLog($"Watch tab was closed ({FirstLine(e)}); reopening");
            return AttemptResult.Failed;
        }
        catch (Exception e)
        {
            item.Status = ItemStatus.Failed;
            item.Note = FirstLine(e);
            AddLog($"{item.Label}: {FirstLine(e)}");
            Raise();
            return AttemptResult.Failed;
        }
    }

    private static async Task<ILocator?> WaitForLiveButtonAsync(IPage page, TimeSpan max, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + max;
        var bySelector = page.Locator(AddToCartSelector);
        var byText = page.GetByRole(AriaRole.Button, new() { NameRegex = AddToCartText });

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var loc in new[] { bySelector, byText })
            {
                var n = await loc.CountAsync();
                for (var i = 0; i < n; i++)
                {
                    var b = loc.Nth(i);
                    if (await b.IsVisibleAsync() && await b.IsEnabledAsync()) return b;
                }
            }
            await Task.Delay(150, ct);
        }
        return null;
    }

    /// <summary>
    /// Clicks Add to cart and waits for Target's cart API to answer, so we only celebrate when
    /// the item is really in the cart. Falls back to the "Added to cart" dialog if the API
    /// call can't be seen (e.g. Target renamed the endpoint).
    /// </summary>
    private static async Task<(bool ok, string? note)> ClickAndConfirmAsync(IPage page, ILocator btn)
    {
        var cartCall = page.WaitForResponseAsync(
            r => r.Request.Method == "POST" &&
                 (r.Url.Contains("cart_items", StringComparison.OrdinalIgnoreCase) ||
                  r.Url.Contains("://carts.target.com", StringComparison.OrdinalIgnoreCase)),
            new() { Timeout = 10000 });

        await btn.ClickAsync(new() { Timeout = 5000 });

        try
        {
            var r = await cartCall;
            if (r.Ok) return (true, null);

            var body = "";
            try { body = await r.TextAsync(); } catch { }
            var reason = ExplainCartError(r.Status, body);
            return (false, reason);
        }
        catch (TimeoutException)
        {
            var dialog = page.GetByText(AddedConfirmText);
            if (await dialog.CountAsync() > 0) return (true, null);
            // Can't confirm either way; go to checkout anyway so you can see for yourself.
            return (true, "Couldn't confirm with Target; check your cart");
        }
    }

    private static string ExplainCartError(int status, string body)
    {
        var b = body.ToLowerInvariant();
        if (b.Contains("limit") || b.Contains("max_quantity") || b.Contains("quantity")) return "Target's purchase limit reached for this item";
        if (b.Contains("out_of_stock") || b.Contains("out of stock") || b.Contains("not_available") || b.Contains("unavailable"))
            return "Sold out as it was added; still trying";
        if (status is 401 or 403) return "Target wants you to log in again (use \"Log in to Target\")";
        return $"Target refused the add ({status}); still trying";
    }

    private static async Task<bool> LooksBlockedAsync(IPage page)
    {
        var title = (await page.TitleAsync()).ToLowerInvariant();
        return title.Contains("access denied") || title.Contains("blocked") || title.Contains("too many requests");
    }

    /// <summary>
    /// Looks at a loaded product page and decides whether it can be bought right now.
    /// Polls quickly so a live button is found within ~150ms of rendering.
    /// </summary>
    internal static async Task<(StockState state, ILocator? button)> ProbeAsync(IPage page, TimeSpan max, CancellationToken ct)
    {
        var btn = await WaitForLiveButtonAsync(page, max, ct);
        if (btn is not null) return (StockState.Live, btn);

        try
        {
            var text = await page.Locator("main").First.InnerTextAsync(new() { Timeout = 1000 });
            if (ComingSoonText.IsMatch(text)) return (StockState.ComingSoon, null);
            if (SoldOutText.IsMatch(text)) return (StockState.SoldOut, null);
        }
        catch { /* no main element; fall through */ }
        return (StockState.Unavailable, null);
    }

    /// <summary>Fills in the real product name, price and picture from the page (best effort).</summary>
    internal async Task ReadDetailsAsync(IPage page, WatchItem item)
    {
        try
        {
            var h1 = page.Locator("h1[data-test=\"product-title\"], h1").First;
            await h1.WaitForAsync(new() { Timeout = 2000 });
            var text = (await h1.InnerTextAsync()).Trim();
            if (text.Length > 0) item.Label = text.Length > 90 ? text[..90] + "…" : text;
        }
        catch { /* keep the fallback label */ }

        try
        {
            var price = page.Locator("[data-test=\"product-price\"]").First;
            if (await price.CountAsync() > 0) item.Price = (await price.InnerTextAsync(new() { Timeout = 1000 })).Trim();
        }
        catch { }

        try
        {
            var img = await page.Locator("meta[property=\"og:image\"]").First.GetAttributeAsync("content", new() { Timeout = 1000 });
            if (!string.IsNullOrWhiteSpace(img) && img.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) item.ImageUrl = img;
        }
        catch { }

        _store.Save();
    }

    /// <summary>A saved product just went live: tell you everywhere we can.</summary>
    internal void AnnounceLive(WatchItem item)
    {
        AddLog($"LIVE NOW: {item.Label}" + (Settings.AutoBuyWhenLive ? " (going for the cart)" : ""));
        ItemLive?.Invoke(item);
        _ = PushAsync($"LIVE NOW on Target: {item.Label}" + (Settings.AutoBuyWhenLive ? ". Adding to cart." : ". Go!"));
    }

    /// <summary>Records a stock reading; returns true when the item just flipped to live.</summary>
    internal bool SetStock(WatchItem item, StockState state)
    {
        var wasLive = item.Stock == StockState.Live;
        item.Stock = state;
        item.StockCheckedAt = DateTime.Now;
        if (state == StockState.Live && !wasLive) item.LiveSince = DateTime.Now;
        if (state != StockState.Live) item.LiveSince = null;
        Raise();
        return state == StockState.Live && !wasLive;
    }

    private async Task OnAddedAsync(WatchItem item)
    {
        item.Status = ItemStatus.Added;
        item.Stock = StockState.Live;
        item.StockCheckedAt = DateTime.Now;
        LastSuccess = item;
        _store.Save();
        AddLog(item.Note is null ? $"In your cart: {item.Label}" : $"Added (unconfirmed): {item.Label}");
        ItemAdded?.Invoke(item);
        _ = PushAsync($"IN YOUR CART: {item.Label}. Check out now!");

        _ = Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            for (var i = 0; i < 6; i++) { Console.Beep(1200, 250); Thread.Sleep(150); }
        });

        // Reuse one checkout tab: if several items land, it just reloads with the fuller cart.
        try
        {
            var url = Settings.OpenCheckout ? "https://www.target.com/checkout" : "https://www.target.com/cart";
            if (_checkoutPage is not { IsClosed: false })
            {
                var ctx = await _browser.GetContextAsync();
                _checkoutPage = await ctx.NewPageAsync();
            }
            await _checkoutPage.BringToFrontAsync();
            await _checkoutPage.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
        }
        catch (Exception e)
        {
            AddLog($"Couldn't open checkout ({FirstLine(e)}). Open your cart in Chrome.");
        }
    }

    private IPage? _checkoutPage;

    // ---------- helpers ----------

    private async Task PushAsync(string message)
    {
        var topic = Settings.NtfyTopic?.Trim();
        if (string.IsNullOrEmpty(topic)) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://ntfy.sh/{Uri.EscapeDataString(topic)}")
            {
                Content = new StringContent(message)
            };
            req.Headers.Add("Title", "Drop Desk");
            req.Headers.Add("Priority", "urgent");
            req.Headers.Add("Tags", "rotating_light");
            await _http.SendAsync(req);
        }
        catch (Exception e)
        {
            AddLog($"Phone alert failed: {FirstLine(e)}");
        }
    }

    public void AddLog(string message)
    {
        lock (_logSync)
        {
            _log.Insert(0, $"{DateTime.Now:h:mm:ss tt}  {message}");
            if (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        }
        Raise();
    }

    private void Raise() => Changed?.Invoke();

    private static string FirstLine(Exception e) => e.Message.Split('\n')[0].Trim();

    /// <summary>Accepts full links, links without https://, and strips tracking junk after the product id.</summary>
    internal static bool TryNormalize(string raw, out string url, out Uri uri)
    {
        url = raw.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!) ||
            !(uri.Host.Equals("target.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".target.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        // Product pages look like /p/<slug>/-/A-12345678. Keep the preselect param (variant) only.
        if (!Regex.IsMatch(uri.AbsolutePath, @"/A-\d+", RegexOptions.IgnoreCase)) return false;
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var preselect = q["preselect"];
        url = $"https://www.target.com{uri.AbsolutePath}" + (string.IsNullOrEmpty(preselect) ? "" : $"?preselect={preselect}");
        uri = new Uri(url);
        return true;
    }

    private static string? Tcin(string url)
    {
        var m = Regex.Match(url, @"/A-(\d+)", RegexOptions.IgnoreCase);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["preselect"];
        return m.Success ? (q ?? m.Groups[1].Value) : null;
    }

    private static bool SameProduct(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || (Tcin(a) is { } t && t == Tcin(b));

    private static string LabelFrom(Uri uri)
    {
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var p = Array.IndexOf(segs, "p");
        if (p >= 0 && p + 1 < segs.Length && segs[p + 1] != "-")
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segs[p + 1].Replace('-', ' ').Trim());
        var tcin = segs.FirstOrDefault(s => s.StartsWith("A-", StringComparison.OrdinalIgnoreCase));
        return tcin is null ? uri.Host : $"Item {tcin[2..]}";
    }
}
