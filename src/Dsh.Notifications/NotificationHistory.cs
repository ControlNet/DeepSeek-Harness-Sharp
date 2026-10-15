using System.Text.Json;

namespace Dsh.Notifications;

/**
 * Persistent notification history backed by an append-only JSONL file.
 * New notifications and read marks are appended; the file is rewritten (temp file + atomic move) only when retention drops entries.
 * Each notification is read at most once, so the file never holds more than two lines per retained notification.
 * One instance owns the file; concurrent writers from other processes are not coordinated.
 */
public sealed class NotificationHistory
{
    private readonly string _path;
    private readonly NotificationRetention _retention;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly List<NotificationRecord> _records;

    private NotificationHistory(string path, NotificationRetention retention, TimeProvider time, List<NotificationRecord> records)
    {
        _path = path;
        _retention = retention;
        _time = time;
        _records = records;
    }

    /** Raised after any change (append, read marks, trimming). */
    public event Action? Changed;

    public string FilePath => _path;

    /** Loads the history (a missing file is an empty history), applies retention and compacts the file if needed. */
    public static async Task<NotificationHistory> OpenAsync(
        string path,
        NotificationRetention? retention = null,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lines = File.Exists(fullPath) ? await File.ReadAllLinesAsync(fullPath, cancellationToken) : [];
        var (records, hadUnreadableLine) = Replay(lines);
        var history = new NotificationHistory(fullPath, retention ?? NotificationRetention.Default, time ?? TimeProvider.System, records);
        // A torn last line has no newline; rewriting keeps the next append from being glued onto it.
        if (history.ApplyRetention() || hadUnreadableLine)
            await history.RewriteAsync(cancellationToken);
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

    /** Appends a notification; returns false (and writes nothing) when its Id is already in the history. */
    public async Task<bool> AppendAsync(NotificationRecord notification, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                if (_records.Exists(record => record.Id == notification.Id))
                    return false;
                InsertByTime(notification);
            }
            await AppendLineAsync(new NotificationAdded(notification), cancellationToken);
            if (ApplyRetention())
                await RewriteAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        Changed?.Invoke();
        return true;
    }

    /** Marks the given notifications read; returns the ids that were unread before. */
    public Task<IReadOnlyList<string>> MarkReadAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        return MarkAsync(record => wanted.Contains(record.Id), cancellationToken);
    }

    /** Marks every matching notification read, e.g. everything of one session after the user opened it. */
    public Task<IReadOnlyList<string>> MarkAllReadAsync(NotificationFilter? filter = null, CancellationToken cancellationToken = default)
    {
        var matcher = filter ?? NotificationFilter.All;
        return MarkAsync(matcher.Matches, cancellationToken);
    }

    private async Task<IReadOnlyList<string>> MarkAsync(Func<NotificationRecord, bool> predicate, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        List<string> changed;
        try
        {
            changed = MarkInMemory(predicate);
            if (changed.Count > 0)
                await AppendLineAsync(new NotificationsRead(changed), cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
        if (changed.Count > 0)
            Changed?.Invoke();
        return changed;
    }

    private List<string> MarkInMemory(Func<NotificationRecord, bool> predicate)
    {
        var changed = new List<string>();
        lock (_stateLock)
        {
            for (var index = 0; index < _records.Count; index++)
            {
                if (_records[index].IsRead || !predicate(_records[index]))
                    continue;
                _records[index] = _records[index] with { IsRead = true };
                changed.Add(_records[index].Id);
            }
        }
        return changed;
    }

    /** Keeps the list ordered by creation time; producers on different machines may deliver slightly out of order. */
    private void InsertByTime(NotificationRecord notification)
    {
        var index = _records.FindLastIndex(record => record.CreatedAt <= notification.CreatedAt);
        _records.Insert(index + 1, notification);
    }

    /** Drops expired and overflowing notifications; returns whether anything was dropped. */
    private bool ApplyRetention()
    {
        var oldestAllowed = _time.GetUtcNow() - _retention.MaxAge;
        lock (_stateLock)
        {
            var before = _records.Count;
            _records.RemoveAll(record => record.CreatedAt < oldestAllowed);
            var overflow = _records.Count - _retention.MaxEntries;
            if (overflow > 0)
                _records.RemoveRange(0, overflow);
            return _records.Count != before;
        }
    }

    /** Replaces the file with one "added" line per retained notification (read state folded in). */
    private async Task RewriteAsync(CancellationToken cancellationToken)
    {
        List<string> lines;
        lock (_stateLock)
            lines = _records.Select(record => Serialize(new NotificationAdded(record))).ToList();
        var temporary = $"{_path}.tmp";
        await File.WriteAllLinesAsync(temporary, lines, cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    private Task AppendLineAsync(NotificationLogEntry entry, CancellationToken cancellationToken)
        => File.AppendAllTextAsync(_path, $"{Serialize(entry)}\n", cancellationToken);

    private static string Serialize(NotificationLogEntry entry)
        => JsonSerializer.Serialize(entry, NotificationsJsonContext.Default.NotificationLogEntry);

    /** Folds the log into records ordered by creation time; an unreadable line (e.g. torn by a crash mid-append) is skipped and reported. */
    private static (List<NotificationRecord> Records, bool HadUnreadableLine) Replay(IReadOnlyList<string> lines)
    {
        var records = new List<NotificationRecord>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var entries = lines.Where(line => !string.IsNullOrWhiteSpace(line)).Select(TryDeserialize).ToList();
        foreach (var entry in entries.OfType<NotificationLogEntry>())
        {
            if (entry is NotificationAdded { Notification: var added } && positions.TryAdd(added.Id, records.Count))
                records.Add(added);
            else if (entry is NotificationsRead { Ids: var ids })
                foreach (var id in ids.Where(positions.ContainsKey))
                    records[positions[id]] = records[positions[id]] with { IsRead = true };
        }
        return (records.OrderBy(record => record.CreatedAt).ToList(), entries.Contains(null));
    }

    private static NotificationLogEntry? TryDeserialize(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, NotificationsJsonContext.Default.NotificationLogEntry);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
