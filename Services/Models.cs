namespace TargetDrop.Services;

public enum ItemStatus { Waiting, Checking, NotLive, Added, Failed }

public class WatchItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Url { get; set; } = "";
    public string Label { get; set; } = "";
    public ItemStatus Status { get; set; } = ItemStatus.Waiting;
    public DateTime? LastChecked { get; set; }
    public int Checks { get; set; }
}

public class DropSettings
{
    public string DropTime { get; set; } = "03:00";
    public int StartEarlyMinutes { get; set; } = 3;
    public int GiveUpAfterMinutes { get; set; } = 90;
    public int RefreshSeconds { get; set; } = 6;
    public string NtfyTopic { get; set; } = "";
    public bool OpenCheckout { get; set; } = true;
}

public class SavedState
{
    public DropSettings Settings { get; set; } = new();
    public List<WatchItem> Items { get; set; } = new();
}
