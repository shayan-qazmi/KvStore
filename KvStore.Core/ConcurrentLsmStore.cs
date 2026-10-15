namespace KvStore.Core;

// A thread-safe wrapper around LsmStore.
// A web server handles many requests at the same time on different threads.
// LsmStore is NOT safe for that, so every call here takes a "lock" first:
// only ONE thread at a time can be inside the store; the others wait their turn.
public sealed class ConcurrentLsmStore : IDisposable
{
    private readonly LsmStore _store;

    // The object we lock on. Every method uses the same one.
    private readonly object _gate = new();

    public ConcurrentLsmStore(
        string directory,
        int maxMemtableEntries = 1000,
        int compactionThreshold = 4,
        bool syncOnEveryWrite = true)
    {
        _store = new LsmStore(directory, maxMemtableEntries, compactionThreshold, syncOnEveryWrite);
    }

    // lock (_gate) { ... } means: wait until nobody else holds the lock, take it,
    // run the code, then give it back (even if the code throws an exception).
    public void Put(string key, string value)
    {
        lock (_gate)
            _store.Put(key, value);
    }

    public string Get(string key)
    {
        lock (_gate)
            return _store.Get(key);
    }

    public void Delete(string key)
    {
        lock (_gate)
            _store.Delete(key);
    }

    // LsmStore.GetRange produces results lazily (one at a time, later).
    // A lock must NOT stay held while a caller slowly loops over lazy results,
    // so we build the complete list INSIDE the lock and return the finished list.
    public List<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        lock (_gate)
            return _store.GetRange(startKey, endKey).ToList();
    }

    public void Flush()
    {
        lock (_gate)
            _store.Flush();
    }

    public void Compact()
    {
        lock (_gate)
            _store.Compact();
    }

    public int SsTableCount
    {
        get
        {
            lock (_gate)
                return _store.SsTableCount;
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _store.Dispose();
    }
}