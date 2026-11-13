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
