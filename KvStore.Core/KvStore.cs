using System.Text;

namespace KvStore.Core;

public class KeyNotFoundInStoreException : Exception
{
    public KeyNotFoundInStoreException(string key)
        : base($"Key '{key}' was not found in the store.") { }
}

public class KvStore : IDisposable
{
    private readonly SortedDictionary<string, string> _data = new();

    // These stay null when the store is in-memory only.
    private readonly FileStream? _file;
    private readonly BinaryWriter? _writer;

    // In-memory only (exactly what Phase 1 did).
    public KvStore() { }

    // File-backed: loads existing data, then keeps the file open for appending.
    public KvStore(string filePath)
    {
        if (File.Exists(filePath))
            LoadFromFile(filePath);

        _file = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new BinaryWriter(_file, Encoding.UTF8);
    }

    public void Put(string key, string value)
    {
        AppendToFile(key, value, isDeleted: false); // file first...
        _data[key] = value;                         // ...then memory
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

        AppendToFile(key, "", isDeleted: true); // a tombstone record
        _data.Remove(key);
    }

    public IEnumerable<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        foreach (var kvp in _data)
        {
            if (string.Compare(kvp.Key, startKey, StringComparison.Ordinal) >= 0 &&
                string.Compare(kvp.Key, endKey, StringComparison.Ordinal) <= 0)
            {
                yield return kvp;
            }
        }
    }

    private void AppendToFile(string key, string value, bool isDeleted)
    {
        if (_writer is null) return; // in-memory mode: nothing to write

        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        _writer.Write(keyBytes.Length);   // int -> 4 bytes
        _writer.Write(keyBytes);          // the raw key bytes
        _writer.Write(valueBytes.Length); // int -> 4 bytes
        _writer.Write(valueBytes);        // the raw value bytes
        _writer.Write(isDeleted);         // bool -> 1 byte
        _writer.Flush();                  // push buffered bytes to the file
    }

    private void LoadFromFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        // Replay every record from the start; later records win.
        while (stream.Position < stream.Length)
        {
            int keyLength = reader.ReadInt32();
            string key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
            int valueLength = reader.ReadInt32();
            string value = Encoding.UTF8.GetString(reader.ReadBytes(valueLength));
            bool isDeleted = reader.ReadBoolean();

            if (isDeleted)
                _data.Remove(key);
            else
                _data[key] = value;
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _file?.Dispose();
    }
}