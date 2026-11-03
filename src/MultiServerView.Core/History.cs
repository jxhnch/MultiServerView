using System.Text.Json;

namespace MultiServerView.Core;

public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Host { get; set; } = "";

    /// <summary>"ssh", "sftp", "rdp", "vnc" or "forward".</summary>
    public string Kind { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>"running", "ok" or "error".</summary>
    public string Result { get; set; } = "running";

    public string Message { get; set; } = "";
}

/// <summary>
/// Connection history ("Logs"). Stays local only (not in the vault, not synced) and never contains passwords
/// or terminal content - only when which host was connected and whether it worked.
/// </summary>
public sealed class HistoryStore
{
    private const int MaxEntries = 500;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<HistoryEntry> _items;

    public HistoryStore(string path)
    {
        _path = path;
        _items = Load();
    }

    public string Begin(ServerEntry server, string kind)
    {
        var entry = new HistoryEntry
        {
            ServerId = server.Id,
            ServerName = server.Name,
            Host = $"{server.Host}:{server.Port}",
            Kind = kind,
            StartedAt = DateTimeOffset.UtcNow
        };
        lock (_gate)
        {
            _items.Add(entry);
            if (_items.Count > MaxEntries) _items.RemoveRange(0, _items.Count - MaxEntries);
            Save();
        }
        return entry.Id;
    }

    public void End(string id, bool ok, string message = "")
    {
        lock (_gate)
        {
            var e = _items.FirstOrDefault(x => x.Id == id);
            if (e == null || e.EndedAt != null) return;
            e.EndedAt = DateTimeOffset.UtcNow;
            e.Result = ok ? "ok" : "error";
            e.Message = message;
            Save();
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<HistoryEntry> List(int limit = 200)
    {
        lock (_gate) return _items.AsEnumerable().Reverse().Take(limit).ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
    }

    private List<HistoryEntry> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path), Json.Options) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* broken file -> start over */ }
        return new();
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, Json.Options));
        }
        catch (IOException) { /* history is not critical */ }
    }
}
