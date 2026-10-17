using System.Text.Json;

namespace Dsh.Notifications;

/** Low-frequency notification state: one in-memory list and an atomically replaced JSON snapshot, including read marks. */
public sealed class NotificationHistory
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private List<NotificationRecord> _records;

    private NotificationHistory(string path, List<NotificationRecord> records)
    {
        _path = path;
        _records = records;
    }

    public event Action? Changed;

    public string FilePath => _path;

    /** The old JSONL file is read only when no snapshot exists, and is never changed or deleted by migration. */
    public static async Task<NotificationHistory> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var records = await JsonSerializer.DeserializeAsync(stream, NotificationsJsonContext.Default.IReadOnlyListNotificationRecord, cancellationToken)
                ?? throw new JsonException("the notification snapshot must be an array");
            return new NotificationHistory(fullPath, [.. records]);
        }
        var legacy = Path.ChangeExtension(fullPath, ".jsonl");
        var history = new NotificationHistory(fullPath, File.Exists(legacy) ? await ImportLegacyAsync(legacy, cancellationToken) : []);
        if (File.Exists(legacy))
            await history.SaveAsync(history._records, cancellationToken);
        return history;
    }

    /** Matching notifications, newest first. */
    public IReadOnlyList<NotificationRecord> Query(NotificationFilter? filter = null)
    {
        filter ??= NotificationFilter.All;
        lock (_stateLock)
            return _records.Where(filter.Matches).Reverse().ToList();
    }

    public int CountUnread(NotificationFilter? filter = null)
    {
        var unread = (filter ?? NotificationFilter.All) with { UnreadOnly = true };
        lock (_stateLock)
            return _records.Count(unread.Matches);
    }

    /** Duplicate producer ids do not change the snapshot or emit Changed. */
    public async Task<bool> AppendAsync(NotificationRecord notification, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            List<NotificationRecord> next;
            lock (_stateLock)
            {
                if (_records.Exists(record => record.Id == notification.Id))
                    return false;
                next = [.. _records];
            }
            var index = next.FindLastIndex(record => record.CreatedAt <= notification.CreatedAt);
            next.Insert(index + 1, notification);
            await SaveAsync(next, cancellationToken);
            lock (_stateLock)
                _records = next;
        }
        finally
        {
            _writeGate.Release();
        }
        Changed?.Invoke();
        return true;
    }

    public Task<IReadOnlyList<string>> MarkReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return MarkAsync(record => wanted.Contains(record.Id), cancellationToken);
    }

    public Task<IReadOnlyList<string>> MarkAllReadAsync(NotificationFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var matcher = filter ?? NotificationFilter.All;
        return MarkAsync(matcher.Matches, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> MarkAsync(Func<NotificationRecord, bool> predicate, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        List<string> changed = [];
        try
        {
            List<NotificationRecord> next;
            lock (_stateLock)
                next = _records.Select(record =>
                {
                    if (record.IsRead || !predicate(record))
                        return record;
                    changed.Add(record.Id);
                    return record with { IsRead = true };
                }).ToList();
            if (changed.Count > 0)
            {
                await SaveAsync(next, cancellationToken);
                lock (_stateLock)
                    _records = next;
            }
        }
        finally
        {
            _writeGate.Release();
        }
        if (changed.Count > 0)
            Changed?.Invoke();
        return changed;
    }

    private async Task SaveAsync(IReadOnlyList<NotificationRecord> records, CancellationToken cancellationToken)
    {
        var temporary = $"{_path}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(records, NotificationsJsonContext.Default.IReadOnlyListNotificationRecord), cancellationToken);
            if (File.Exists(_path))
                File.Replace(temporary, _path, null);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    /** One-time decoding of the former added/read wire records; this is not used for new writes or recovery of snapshots. */
    private static async Task<List<NotificationRecord>> ImportLegacyAsync(string path, CancellationToken cancellationToken)
    {
        var records = new Dictionary<string, NotificationRecord>(StringComparer.Ordinal);
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
                continue;
            try
            {
                using var document = JsonDocument.Parse(lines[index]);
                var entry = document.RootElement;
                switch (entry.GetProperty("type").GetString())
                {
                    case "added":
                        var added = entry.GetProperty("notification").Deserialize(NotificationsJsonContext.Default.NotificationRecord)
                            ?? throw new JsonException("a legacy notification must not be null");
                        records.TryAdd(added.Id, added);
                        break;
                    case "read":
                        foreach (var id in entry.GetProperty("ids").EnumerateArray())
                            if (id.GetString() is { } key && records.TryGetValue(key, out var record))
                                records[key] = record with { IsRead = true };
                        break;
                    default:
                        throw new JsonException("unknown legacy notification entry");
                }
            }
            catch (JsonException) when (index == lines.Length - 1)
            {
                // Old append-only files could end mid-record; all original bytes remain in the untouched source.
            }
        }
        return records.Values.OrderBy(record => record.CreatedAt).ToList();
    }
}
