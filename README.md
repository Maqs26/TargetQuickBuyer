# Drop Desk

A small local app for grabbing Pokémon products on Target the second they drop.

Paste a target.com product link and hit **Add** (or Enter). It's checked immediately: if it's
available, it's added to your cart in your own logged-in Chrome and checkout opens in a side
window on the right of your screen. You place the order yourself. If it isn't available yet, it's
saved and tracked.

Saved links get a live badge (**LIVE** / Sold out / Coming soon) from a background stock checker,
so you can see at a glance what's buyable.

## Requirements
- .NET 8 SDK (https://dotnet.microsoft.com/download).
- Google Chrome installed.

## Run it
```
cd DropDesk
dotnet run --project TargetDrop.csproj
```
Or open `DropDesk.sln` in Visual Studio and press F5. The app opens at http://localhost:5080.
If that port is taken: `dotnet run --project TargetDrop.csproj -- --urls http://localhost:5090`.

If Playwright complains about a missing driver, run this once after building:
```
pwsh bin/Debug/net8.0/playwright.ps1 install chrome
```

## First-time setup
1. Click **Log in to Target** (bottom of the big card). A separate Chrome window opens with its
   own profile. Log in, and make sure a default shipping address and card are saved. The profile
   lives in `./chrome-profile` (git-ignored), so you only do this once.
2. Paste a product link (copy it from the product page's address bar) and hit **Add**:
   - Available → straight into your cart, checkout opens in a side window.
   - Not available → saved under **Saved products** with a Sold out / Coming soon badge. Tick
     *"If it's not live yet, add it to cart automatically when it goes live"* under the box to have
     the stock checker grab it the moment it flips.
   - **Keep refreshing it nonstop (drop mode)** hammers that one product every few seconds until
     it's live, for up to the "Keep trying" window in Drop settings (default 90 min).
   - Keep Drop Desk's Chrome open (log in once) so adds are instant; the very first add otherwise
     has to launch Chrome.
3. Optional: in **Drop settings**, add an ntfy topic for phone alerts (install the free ntfy app and
   subscribe to the same name).
4. Test it with something cheap that's in stock: **Buy now** should add it and open checkout.
   Remove it from your cart afterwards.

## Stock badges
- Checks every saved product every 2 minutes (change it in Drop settings, 0 = off) **while Drop
  Desk's Chrome is open**. It never pops Chrome open on its own. **Check stock now** opens it.
- When something flips to **LIVE**, the card glows, you get a chime, a desktop notification and a
  phone push. With auto-add ticked it clicks Add to cart right on the page it just checked and
  opens checkout in the side window.
- Products a watch is already refreshing are skipped by the checker.

## Drop night
- Click **Arm for 3:00 AM** to watch every saved product from a few minutes before the drop.
  Leave the app tab and Chrome open, turn off sleep, keep the computer plugged in.
- Drop time uses your computer's local clock.

## Notes
- Your list and settings are saved in `watchlist.json` (git-ignored).
- Keep the refresh delay at 5 seconds or more. Hammering Target gets your browser rate-limited;
  the app notices and backs off for 60 seconds.
- Watch tabs skip images and fonts so reloads are fast. Your checkout tab loads normally.
- Pre-order buttons count as live.
- If Target changes its page and the button stops being found, the selectors are at the top of
  `Services/DropWatcher.cs` (`AddToCartSelector`, plus the sold-out and coming-soon text patterns).
- This is browser automation on Target's site, which Target's terms of use don't allow. Use it
  at a human pace for your own purchases.
