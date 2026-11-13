# Drop Desk

A small local app for Target drops. Paste product links, arm it for 3:00 AM, and it
refreshes the pages in your own Chrome. The moment "Add to cart" goes live it clicks it,
opens checkout, plays an alarm, flashes the tab and (optionally) pushes to your phone.
You place the order yourself.

## Requirements
- .NET 8 SDK (https://dotnet.microsoft.com/download). On .NET 9, change `net8.0` to `net9.0` in TargetDrop.csproj.
- Google Chrome installed.

## Run it
```
cd DropDesk
dotnet run
```
Open http://localhost:5080.

If Playwright complains about missing browsers or a driver, run this once after building:
```
pwsh bin/Debug/net8.0/playwright.ps1 install chrome
```
(On macOS/Linux without PowerShell: `dotnet tool install --global PowerShell`, then run the line above.)

## First-time setup
1. Click **Open Chrome to log in to Target**. A separate Chrome window opens with its own profile.
   Log in, and make sure a default shipping address and card are saved. This profile is
   kept in `./chrome-profile`, so you only do this once.
2. Paste one or more product links (copied from the product page address bar) and click **Add**.
3. Optional: in **Drop settings**, add an ntfy topic for phone alerts (install the free ntfy app and subscribe to the same name).
4. Test it: add an item that is in stock right now and click **Watch now**. It should add to cart and open checkout.
   Remove it from your cart afterwards.

## Drop night
- Click **Arm for 3:00 AM** and leave the app tab and Chrome open.
- Turn off sleep on your computer and keep it plugged in.
- Drop time uses your computer's local clock. Adjust it if the drop is in another time zone.

## Notes
- Your list and settings are saved in `watchlist.json`.
- Keep the refresh delay at 5 seconds or more. Hammering the page gets your browser rate-limited,
  which the app detects and backs off from for 60 seconds.
- If Target changes its page and the button stops being found, the selector lives in
  `Services/DropWatcher.cs` in `TryAddAsync`.
