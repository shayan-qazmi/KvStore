using System.IO.Hashing; // from the NuGet package; gives us Crc32
using System.Text;

namespace KvStore.Core;

// Thrown when someone asks for (or deletes) a key that does not exist.
public class KeyNotFoundInStoreException : Exception
{
    public KeyNotFoundInStoreException(string key)
        : base($"Key '{key}' was not found in the store.") { }
}

// IDisposable means this class holds an open file, so it needs Dispose()
// (or a "using" block) to close the file when we are done.
public class KvStore : IDisposable
{
    // Every record starts with a header: 4 bytes checksum + 4 bytes body length.
    private const int HeaderSize = 8;

    // The in-memory copy of all data. SortedDictionary keeps keys in order,
    // which is what makes GetRange possible.
    private readonly SortedDictionary<string, string> _data = new();

    // The open file and the writer on top of it.
    // They stay null when the store is in-memory only (the "?" means "can be null").
    private readonly FileStream? _file;
    private readonly BinaryWriter? _writer;

    // In-memory only. Nothing is saved to disk.
    public KvStore() { }

    // File-backed. Loads old data from the file, then keeps the file open
    // so every new Put/Delete can be appended to it.
    public KvStore(string filePath)
    {
        long goodLength = 0; // how many bytes of the file are good records

        if (File.Exists(filePath))
            goodLength = LoadFromFile(filePath);

        // Open the file for writing. OpenOrCreate = use it if it exists,
        // otherwise make a new empty one.
        _file = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

        // Cut off anything after the last good record (a half-written record
        // from a crash). If the file was fine, this changes nothing.
        _file.SetLength(goodLength);

        // Move to the end so new records are added after the good ones.
        _file.Seek(0, SeekOrigin.End);

        _writer = new BinaryWriter(_file, Encoding.UTF8);
    }

    public void Put(string key, string value)
    {
        // Write to the file FIRST. If this throws, memory is not changed,
        // so memory and file never disagree.
        AppendToFile(key, value, isDeleted: false);
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

        // A delete is saved as a record with the "deleted" flag set (a tombstone).
        AppendToFile(key, "", isDeleted: true);
        _data.Remove(key);
    }

    public IEnumerable<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        // Keys are already sorted, so one pass through is enough.
        foreach (var kvp in _data)
        {
            if (string.Compare(kvp.Key, startKey, StringComparison.Ordinal) >= 0 &&
                string.Compare(kvp.Key, endKey, StringComparison.Ordinal) <= 0)
            {
                yield return kvp;
            }
        }
    }

    // Builds the "body" of a record: key length, key, value length, value, flag.
    private static byte[] BuildBody(string key, string value, bool isDeleted)
    {
        // A file holds bytes, not text, so we turn the text into bytes first.
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        // MemoryStream = a "file" that lives in memory. We build the body there.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(keyBytes.Length);   // int = 4 bytes
        writer.Write(keyBytes);          // the key bytes
        writer.Write(valueBytes.Length); // int = 4 bytes
        writer.Write(valueBytes);        // the value bytes
        writer.Write(isDeleted);         // bool = 1 byte
        writer.Flush();

        return stream.ToArray();
    }

    private void AppendToFile(string key, string value, bool isDeleted)
    {
        if (_writer is null || _file is null) return; // in-memory mode: nothing to save

        byte[] body = BuildBody(key, value, isDeleted);

        // The checksum is a number calculated from the body bytes.
        // If even one byte changes later, the number will not match.
        uint checksum = Crc32.HashToUInt32(body);

        _writer.Write(checksum);    // 4 bytes
        _writer.Write(body.Length); // 4 bytes
        _writer.Write(body);        // the body itself

        _writer.Flush();    // move bytes from the writer into the FileStream
        _file.Flush(true);  // force the bytes onto the physical disk (slower, but safe)
    }

    // Reads the file from the start and rebuilds _data.
    // Returns how many bytes at the start of the file are good records.
    private long LoadFromFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        long lastGoodPosition = 0;

        while (true)
        {
            // Not enough bytes left for a header: either we are at the end,
            // or a crash cut the header in half. Stop either way.
            if (stream.Length - stream.Position < HeaderSize)
                break;

            uint storedChecksum = reader.ReadUInt32();
            int bodyLength = reader.ReadInt32();

            // A negative length, or one longer than the rest of the file,
            // means the record is broken. Stop.
            if (bodyLength < 0 || bodyLength > stream.Length - stream.Position)
                break;

            byte[] body = reader.ReadBytes(bodyLength);

            // Recalculate the checksum. If it differs from the saved one,
            // the record is damaged. Stop.
            if (Crc32.HashToUInt32(body) != storedChecksum)
                break;

            // The record is good: apply it to memory.
            ApplyBody(body);

            // Remember where the last good record ended.
            lastGoodPosition = stream.Position;
        }

        return lastGoodPosition;
    }

    // Reads one good body and applies it to _data.
    private void ApplyBody(byte[] body)
    {
        using var stream = new MemoryStream(body);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        int keyLength = reader.ReadInt32();
        string key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
        int valueLength = reader.ReadInt32();
        string value = Encoding.UTF8.GetString(reader.ReadBytes(valueLength));
        bool isDeleted = reader.ReadBoolean();

        // Records are replayed in order, so later records overwrite earlier ones.
        if (isDeleted)
            _data.Remove(key);
        else
            _data[key] = value;
    }

    // Closes the file. Called automatically at the end of a "using" block.
    public void Dispose()
    {
        _writer?.Dispose();
        _file?.Dispose();
    }
}