using System.Globalization;
using System.Text.Json.Serialization;

namespace TargetDrop.Services;

public enum ItemStatus { Waiting, Checking, NotLive, Added, Failed }

/// <summary>What the product page says right now, from the background stock checker.</summary>
public enum StockState { Unknown, Live, SoldOut, ComingSoon, Unavailable }

public class WatchItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Url { get; set; } = "";
    public string Label { get; set; } = "";
    public ItemStatus Status { get; set; } = ItemStatus.Waiting;
    public DateTime? LastChecked { get; set; }
    public int Checks { get; set; }

    /// <summary>Last thing worth telling you about this item (purchase limit, refused add, etc.).</summary>
    public string? Note { get; set; }

    public StockState Stock { get; set; } = StockState.Unknown;
    public DateTime? StockCheckedAt { get; set; }
    public DateTime? LiveSince { get; set; }
    public string? Price { get; set; }
    public string? ImageUrl { get; set; }
}

public class DropSettings
{
    public string DropTime { get; set; } = "03:00";
    public int StartEarlyMinutes { get; set; } = 3;
    public int GiveUpAfterMinutes { get; set; } = 90;
    public int RefreshSeconds { get; set; } = 6;
    public string NtfyTopic { get; set; } = "";
    public bool OpenCheckout { get; set; } = true;

    /// <summary>How often the background checker looks at every saved product. 0 turns it off.</summary>
    public int StockCheckMinutes { get; set; } = 2;

    /// <summary>When the stock checker sees a saved product go live, go for the cart automatically.</summary>
    public bool AutoBuyWhenLive { get; set; }

    /// <summary>DropTime as a TimeOnly, for the time picker. Falls back to 3:00 AM if the saved value is bad.</summary>
    [JsonIgnore]
    public TimeOnly DropTimeValue
    {
        get => TimeOnly.TryParse(DropTime, CultureInfo.InvariantCulture, out var t) ? t : new TimeOnly(3, 0);
        set => DropTime = value.ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}

public class SavedState
{
    public DropSettings Settings { get; set; } = new();
    public List<WatchItem> Items { get; set; } = new();
}
