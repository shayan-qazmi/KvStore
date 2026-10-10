namespace KvStore.Core;

public class KeyNotFoundInStoreException : Exception
{
    public KeyNotFoundInStoreException(string key)
        : base($"Key '{key}' was not found in the store.") { }
}

public class KvStore
{
    // SortedDictionary keeps keys in sorted order automatically (by an internal
    // red-black tree), which is exactly what we need later for GetRange() —
    // a plain Dictionary<> gives you no ordering guarantees at all.
    private readonly SortedDictionary<string, string> _data = new();

    public void Put(string key, string value)
    {
        _data[key] = value; // indexer either adds or overwrites — same call does both
    }

    public string Get(string key)
    {
        if (!_data.TryGetValue(key, out var value))
            throw new KeyNotFoundInStoreException(key);

        return value;
    }

    public void Delete(string key)
    {
        if (!_data.Remove(key))
            throw new KeyNotFoundInStoreException(key);
    }

    public IEnumerable<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        // Because SortedDictionary is ordered, we can just filter in one pass —
        // no manual sorting needed.
        foreach (var kvp in _data)
        {
            if (string.Compare(kvp.Key, startKey, StringComparison.Ordinal) >= 0 &&
                string.Compare(kvp.Key, endKey, StringComparison.Ordinal) <= 0)
            {
                yield return kvp;
            }
        }
    }
}