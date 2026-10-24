using System.Text.Json;

namespace Doudou.Core.Services;

/// <summary>与云端同步的历史摘要；不保存工程文件或图片。</summary>
public sealed class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Kind { get; set; } = "model";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public HistoryMeta Meta { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class HistoryMeta
{
    public int? Beads { get; set; }
    public int? Colors { get; set; }
    public string? Size { get; set; }
    public string? Source { get; set; }
    public string? Mode { get; set; }
    public int? Parts { get; set; }
}

/// <summary>本机历史摘要。未登录也会记录，登录后与云端合并。</summary>
public static class HistoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static string Path => System.IO.Path.Combine(AppPaths.UserData, "history.json");
    public const int MaxEntries = 200;

    public static List<HistoryEntry> Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Path)) return new List<HistoryEntry>();
                var list = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path), Json) ?? new();
                return Normalize(list);
            }
            catch (Exception) { return new List<HistoryEntry>(); }
        }
    }

    public static HistoryEntry Add(HistoryEntry entry)
    {
        lock (Gate)
        {
            var list = LoadUnsafe();
            if (list.All(e => !string.Equals(e.Id, entry.Id, StringComparison.OrdinalIgnoreCase)))
                list.Add(entry);
            SaveUnsafe(Normalize(list));
            return entry;
        }
    }

    public static List<HistoryEntry> Merge(IEnumerable<HistoryEntry> remote)
    {
        lock (Gate)
        {
            var byId = new Dictionary<string, HistoryEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in LoadUnsafe()) byId[entry.Id] = entry;
            foreach (var entry in remote)
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Id) && !byId.ContainsKey(entry.Id))
                    byId[entry.Id] = entry;
            var merged = Normalize(byId.Values);
            SaveUnsafe(merged);
            return merged;
        }
    }

    public static void Save(IEnumerable<HistoryEntry> entries)
    {
        lock (Gate) SaveUnsafe(Normalize(entries));
    }

    private static List<HistoryEntry> LoadUnsafe()
    {
        try
        {
            if (!File.Exists(Path)) return new List<HistoryEntry>();
            return JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(Path), Json) ?? new();
        }
        catch (Exception) { return new List<HistoryEntry>(); }
    }

    private static void SaveUnsafe(List<HistoryEntry> list)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.UserData);
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, Path, overwrite: true);
        }
        catch (Exception) { /* 本地历史写不进去不影响主流程 */ }
    }

    private static List<HistoryEntry> Normalize(IEnumerable<HistoryEntry> entries) =>
        entries
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id) && !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(e => e.CreatedAt)
            .Take(MaxEntries)
            .ToList();
}
