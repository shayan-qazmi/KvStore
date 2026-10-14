using System.Text;

namespace KvStore.Core;

// One stored value plus a flag saying "this key was deleted" (a tombstone).
// A record struct is a tiny type that just holds values.
public readonly record struct Entry(string Value, bool IsDeleted);

// An SSTable (Sorted String Table): a file of entries sorted by key.
// It is written ONCE and never changed afterwards.
// File layout, per entry: [4 key length][key][1 deleted flag][4 value length][value]
public sealed class SsTable
{
    // Where one value sits inside the file.
    private readonly record struct Location(long ValueOffset, int ValueLength, bool IsDeleted);

    public string FilePath { get; }

    // Index kept in memory: key -> where its value is in the file.
    // Only keys are in memory; the (possibly big) values stay on disk.
    private readonly SortedDictionary<string, Location> _index = new(StringComparer.Ordinal);

    // Private: use SsTable.Write(...) or SsTable.Open(...).
    private SsTable(string filePath)
    {
        FilePath = filePath;
    }

    // Writes sorted entries to a new SSTable file.
    // 'sortedEntries' MUST already be sorted by key (a SortedDictionary is).
    public static SsTable Write(string path, IEnumerable<KeyValuePair<string, Entry>> sortedEntries)
    {
        // Write to a temporary file first, then rename. If we crash while writing,
        // only a .tmp file is left behind, never a half-finished SSTable.
        string tempPath = path + ".tmp";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            foreach (var (key, entry) in sortedEntries)
            {
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                byte[] valueBytes = Encoding.UTF8.GetBytes(entry.Value);

                writer.Write(keyBytes.Length);
                writer.Write(keyBytes);
                writer.Write(entry.IsDeleted);
                writer.Write(valueBytes.Length);
                writer.Write(valueBytes);
            }

            writer.Flush();
            stream.Flush(true); // force everything onto the disk before the rename
        }

        // The file is complete, so give it its real name.
        File.Move(tempPath, path);

        return Open(path);
    }

    // Opens an existing SSTable and builds the in-memory index by reading through it once.
    public static SsTable Open(string path)
    {
        var table = new SsTable(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        while (stream.Position < stream.Length)
        {
            int keyLength = reader.ReadInt32();
            string key = Encoding.UTF8.GetString(reader.ReadBytes(keyLength));
            bool isDeleted = reader.ReadBoolean();
            int valueLength = reader.ReadInt32();

            // Remember where the value starts, then jump over it without reading it.
            long valueOffset = stream.Position;
            stream.Seek(valueLength, SeekOrigin.Current);

            table._index[key] = new Location(valueOffset, valueLength, isDeleted);
        }

        return table;
    }

    // Looks for one key. Returns false if this table has no entry for it.
    // Returns true if it does (the entry may be a tombstone: check IsDeleted).
    public bool TryGet(string key, out Entry entry)
    {
        if (!_index.TryGetValue(key, out var location))
        {
            entry = new Entry("", false);
            return false;
        }

        // A tombstone has no value to read.
        if (location.IsDeleted)
        {
            entry = new Entry("", true);
            return true;
        }

        // Open the file, jump straight to the value, read exactly its bytes.
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Seek(location.ValueOffset, SeekOrigin.Begin);

        byte[] bytes = new byte[location.ValueLength];
        stream.ReadExactly(bytes);

        entry = new Entry(Encoding.UTF8.GetString(bytes), false);
        return true;
    }

    // Returns all entries with startKey <= key <= endKey, in sorted order.
    // Tombstones are included, so the caller can hide older values with them.
    public IEnumerable<KeyValuePair<string, Entry>> ReadRange(string startKey, string endKey)
    {
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        foreach (var (key, location) in _index)
        {
            if (string.CompareOrdinal(key, startKey) < 0)
                continue;                 // before the range: skip
            if (string.CompareOrdinal(key, endKey) > 0)
                break;                    // sorted, so nothing later can be in range: stop

            Entry entry;
            if (location.IsDeleted)
            {
                entry = new Entry("", true);
            }
            else
            {
                stream.Seek(location.ValueOffset, SeekOrigin.Begin);
                byte[] bytes = new byte[location.ValueLength];
                stream.ReadExactly(bytes);
                entry = new Entry(Encoding.UTF8.GetString(bytes), false);
            }

            yield return new KeyValuePair<string, Entry>(key, entry);
        }
    }
}