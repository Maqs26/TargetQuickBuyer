using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace TargetDrop.Services;

public enum WatcherMode { Idle, Armed, Watching }

/// <summary>
/// Waits for the drop window, refreshes each product page, clicks "Add to cart"
/// the moment it is live, then opens checkout in Chrome and raises every alarm it can.
/// It never places the order: you finish checkout yourself.
/// </summary>
public class DropWatcher
{
    private readonly BrowserManager _browser;
    private readonly StateStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly object _logSync = new();
    private readonly List<string> _log = new();
    private CancellationTokenSource? _cts;

    public DropWatcher(BrowserManager browser, StateStore store)
    {
        _browser = browser;
        _store = store;
    }

    public event Action? Changed;
    public event Action<WatchItem>? ItemAdded;

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

    // ---------- list management ----------

    public string? AddItem(string raw)
    {
        var url = raw.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("target.com", StringComparison.OrdinalIgnoreCase))
            return "That isn't a target.com link. Copy it from the product page's address bar.";

        lock (_store.Sync)
        {
            if (_store.State.Items.Any(i => i.Url == url))
                return "That product is already on the list.";
            _store.State.Items.Add(new WatchItem { Url = url, Label = LabelFrom(uri) });
        }
        _store.Save();
        AddLog($"Added {LabelFrom(uri)}");
        return null;
    }

    public void RemoveItem(string id)
    {
        lock (_store.Sync) _store.State.Items.RemoveAll(i => i.Id == id);
        _store.Save();
        Raise();
    }

    public void SaveSettings()
    {
        _store.Save();
        Raise();
    }

    // ---------- start / stop ----------

    public void Start(bool watchNow)
    {
        if (Mode != WatcherMode.Idle || Items.Count == 0) return;

        lock (_store.Sync)
            foreach (var i in _store.State.Items) { i.Status = ItemStatus.Waiting; i.Checks = 0; }
        LastSuccess = null;

        var now = DateTime.Now;
        if (watchNow)
        {
            StartsAt = DropAt = now;
        }
        else
        {
            var time = TimeOnly.TryParse(Settings.DropTime, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(3, 0);
            var drop = now.Date + time.ToTimeSpan();
            if (drop.AddMinutes(Settings.GiveUpAfterMinutes) < now) drop = drop.AddDays(1);
            DropAt = drop;
            StartsAt = drop.AddMinutes(-Settings.StartEarlyMinutes);
        }
        GiveUpAt = DropAt.AddMinutes(Settings.GiveUpAfterMinutes);

        _cts = new CancellationTokenSource();
        Mode = WatcherMode.Armed;
        AddLog(watchNow ? "Watching now" : $"Armed for {DropAt:ddd h:mm tt}");
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    // ---------- the loop ----------

    private async Task RunAsync(CancellationToken ct)
    {
        IPage? page = null;
        try
        {
            // Sleep in short chunks so a laptop that dozed off still wakes on time.
            while (DateTime.Now < StartsAt)
            {
                var left = (StartsAt - DateTime.Now).TotalSeconds;
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(left, 0.2, 15)), ct);
            }

            Mode = WatcherMode.Watching;
            AddLog("Refreshing started");
            await PushAsync("Drop Desk is watching now.");

            var ctx = await _browser.GetContextAsync();
            page = await ctx.NewPageAsync();

            while (DateTime.Now < GiveUpAt)
            {
                var pending = Items.Where(i => i.Status != ItemStatus.Added).ToList();
                if (pending.Count == 0) break;

                foreach (var item in pending)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Items.Contains(item)) continue; // removed mid-run

                    if (await TryAddAsync(page, item, ct))
                        await OnAddedAsync(ctx, item);

                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, Settings.RefreshSeconds)), ct);
                }
            }

            if (Items.Any(i => i.Status != ItemStatus.Added))
            {
                AddLog("Window closed before everything went live");
                await PushAsync("Drop window closed. Not everything went live.");
            }
            else
            {
                AddLog("Everything on the list is in your cart");
            }
        }
        catch (OperationCanceledException)
        {
            AddLog("Stopped");
        }
        catch (Exception e)
        {
            AddLog($"Watcher error: {e.Message}");
            await PushAsync($"Drop Desk hit an error: {e.Message}");
        }
        finally
        {
            if (page != null) { try { await page.CloseAsync(); } catch { } }
            lock (_store.Sync)
                foreach (var i in _store.State.Items.Where(i => i.Status == ItemStatus.Checking))
                    i.Status = ItemStatus.NotLive;
            Mode = WatcherMode.Idle;
            Raise();
        }
    }

    private async Task<bool> TryAddAsync(IPage page, WatchItem item, CancellationToken ct)
    {
        item.Status = ItemStatus.Checking;
        item.LastChecked = DateTime.Now;
        item.Checks++;
        Raise();

        try
        {
            await page.GotoAsync(item.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            await page.WaitForTimeoutAsync(1500); // let the buy box render

            var title = (await page.TitleAsync()).ToLowerInvariant();
            if (title.Contains("access denied") || title.Contains("blocked"))
            {
                AddLog("Target is rate-limiting this browser; pausing 60s. Consider a longer refresh delay.");
                item.Status = ItemStatus.NotLive;
                Raise();
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
                return false;
            }

            // Target's "Add to cart" (shipping) button; fall back to any button with that text.
            var btn = page.Locator("button[data-test=\"shippingButton\"]");
            if (await btn.CountAsync() == 0)
                btn = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("add to cart", RegexOptions.IgnoreCase) });

            if (await btn.CountAsync() == 0 || !await btn.First.IsEnabledAsync())
            {
                item.Status = ItemStatus.NotLive;
                Raise();
                return false;
            }

            await btn.First.ClickAsync(new() { Timeout = 5000 });
            await page.WaitForTimeoutAsync(1500);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            item.Status = ItemStatus.Failed;
            AddLog($"{item.Label}: {e.Message.Split('\n')[0]}");
            return false;
        }
    }

    private async Task OnAddedAsync(IBrowserContext ctx, WatchItem item)
    {
        item.Status = ItemStatus.Added;
        LastSuccess = item;
        _store.Save();
        AddLog($"In your cart: {item.Label}");
        ItemAdded?.Invoke(item);
        _ = PushAsync($"IN YOUR CART: {item.Label}. Check out now!");

        if (OperatingSystem.IsWindows())
            _ = Task.Run(() =>
            {
                for (var i = 0; i < 10; i++) { Console.Beep(1200, 300); Thread.Sleep(200); }
            });

        try
        {
            var checkout = await ctx.NewPageAsync();
            var target = Settings.OpenCheckout ? "https://www.target.com/checkout" : "https://www.target.com/cart";
            await checkout.GotoAsync(target, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 20000 });
            await checkout.BringToFrontAsync();
        }
        catch (Exception e)
        {
            AddLog($"Couldn't open checkout ({e.Message.Split('\n')[0]}). Open your cart in Chrome.");
        }
    }

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
            AddLog($"Phone alert failed: {e.Message}");
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

    private static string LabelFrom(Uri uri)
    {
        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var p = Array.IndexOf(segs, "p");
        if (p >= 0 && p + 1 < segs.Length && segs[p + 1] != "-")
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(segs[p + 1].Replace('-', ' ').Trim());
        var tcin = segs.FirstOrDefault(s => s.StartsWith("A-"));
        return tcin is null ? uri.Host : $"Item {tcin[2..]}";
    }
}
