namespace KvStore.Core;

// Thrown when someone asks for (or deletes) a key that does not exist.
public class KeyNotFoundInStoreException : Exception
{
    public KeyNotFoundInStoreException(string key)
        : base($"Key '{key}' was not found in the store.") { }
}

// The simple store: everything in memory, optionally backed by a log file.
// (The bigger LsmStore, which can hold more than fits in memory, comes later in this phase.)
public class KvStore : IDisposable
{
    // All data, kept sorted. Ordinal = sort by character value (same everywhere in this project).
    private readonly SortedDictionary<string, string> _data = new(StringComparer.Ordinal);

    // The log file. Stays null when the store is in-memory only ("?" means "can be null").
    private readonly WriteAheadLog? _log;

    // In-memory only. Nothing is saved to disk.
    public KvStore() { }

    // File-backed. Opens the log and replays old records into _data.
    public KvStore(string filePath)
    {
        // The part after "=>" runs once for every record found in the file.
        _log = WriteAheadLog.Open(filePath, (key, value, isDeleted) =>
        {
            if (isDeleted)
                _data.Remove(key);
            else
                _data[key] = value;
        });
    }

    public void Put(string key, string value)
    {
        // Log first, memory second. If logging fails, memory stays unchanged.
        _log?.Append(key, value, isDeleted: false);
        _data[key] = value;
    }

    public string Get(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundInStoreException(key);

        return value;
    }

    public void Delete(string key)
    {
        if (!_data.ContainsKey(key))
            throw new KeyNotFoundInStoreException(key);

        // A delete is logged as a record with the "deleted" flag set (a tombstone).
        _log?.Append(key, "", isDeleted: true);
        _data.Remove(key);
    }

    public IEnumerable<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        // Keys are sorted, so one pass is enough.
        foreach (var kvp in _data)
        {
            if (string.CompareOrdinal(kvp.Key, startKey) >= 0 &&
                string.CompareOrdinal(kvp.Key, endKey) <= 0)
            {
                yield return kvp;
            }
        }
    }

    // Closes the log file (if there is one).
    public void Dispose()
    {
        _log?.Dispose();
    }
}