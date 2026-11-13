using System.Text.Json;
using System.Text.Json.Serialization;

namespace TargetDrop.Services;

/// <summary>Keeps the product list and settings in watchlist.json so they survive restarts.</summary>
public class StateStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path = Path.Combine(Directory.GetCurrentDirectory(), "watchlist.json");

    public object Sync { get; } = new();
    public SavedState State { get; }

    public StateStore()
    {
        try
        {
            State = File.Exists(_path)
                ? JsonSerializer.Deserialize<SavedState>(File.ReadAllText(_path), Opts) ?? new()
                : new();
        }
        catch
        {
            State = new();
        }

        // A fresh run doesn't know what's in your cart; start every product from "Ready".
        // Stock readings (Live / Sold out) are kept so the list isn't blank until the first check.
        foreach (var i in State.Items)
        {
            i.Status = ItemStatus.Waiting;
            i.Checks = 0;
            i.Note = null;
        }
    }

    public void Save()
    {
        lock (Sync)
        {
            try { File.WriteAllText(_path, JsonSerializer.Serialize(State, Opts)); }
            catch { /* non-fatal */ }
        }
    }
}
