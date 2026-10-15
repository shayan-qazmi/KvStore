namespace KvStore.Core;

// The real engine: WAL + memtable + SSTables + compaction.
// All its files live in one folder:
//   wal.log               the write-ahead log (safe copy of the memtable)
//   sst-0000000001.sst    SSTable files (higher number = newer)
public sealed class LsmStore : IDisposable
{
    private readonly string _directory;
    private readonly int _maxMemtableEntries;
    private readonly int _compactionThreshold;

    // The memtable: newest writes, sorted, in memory.
    // It also holds tombstones, so a delete can hide a value stored in an SSTable.
    private readonly SortedDictionary<string, Entry> _memtable = new(StringComparer.Ordinal);

    // The SSTables on disk. Oldest first, newest last.
    private readonly List<SsTable> _tables = new();

    private readonly WriteAheadLog _wal;

    // Number used for the next SSTable file name.
    private int _nextTableId;

    // How many SSTable files exist right now (handy for tests).
    public int SsTableCount => _tables.Count;

    // maxMemtableEntries: how many entries the memtable holds before it is flushed.
    // compactionThreshold: when this many SSTables exist, they are merged into one.
    public LsmStore(string directory, int maxMemtableEntries = 1000, int compactionThreshold = 4)
    {
        if (maxMemtableEntries < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMemtableEntries));
        if (compactionThreshold < 2)
            throw new ArgumentOutOfRangeException(nameof(compactionThreshold));

        _directory = directory;
        _maxMemtableEntries = maxMemtableEntries;
        _compactionThreshold = compactionThreshold;

        Directory.CreateDirectory(directory); // does nothing if it already exists

        // A .tmp file means a crash happened in the middle of a flush or compaction.
        // That SSTable was never finished, so delete it. The other files still have the data.
        foreach (string tempFile in Directory.GetFiles(directory, "*.tmp"))
            File.Delete(tempFile);

        // Load existing SSTables. File names have zero-padded numbers,
        // so sorting by name also sorts by age (oldest first).
        string[] files = Directory.GetFiles(directory, "sst-*.sst");
        Array.Sort(files, StringComparer.Ordinal);

        foreach (string file in files)
            _tables.Add(SsTable.Open(file));

        _nextTableId = files.Length == 0 ? 1 : ParseTableId(files[^1]) + 1;

        // Open the WAL. Records still in it (not yet flushed) go back into the memtable.
        _wal = WriteAheadLog.Open(
            Path.Combine(directory, "wal.log"),
            (key, value, isDeleted) => _memtable[key] = new Entry(value, isDeleted));
    }

    public void Put(string key, string value)
    {
        // 1) Safe copy on disk first. 2) Then memory.
        _wal.Append(key, value, isDeleted: false);
        _memtable[key] = new Entry(value, false);

        FlushIfFull();
    }

    public string Get(string key)
    {
        if (!TryGetLive(key, out string value))
            throw new KeyNotFoundInStoreException(key);

        return value;
    }

    public void Delete(string key)
    {
        // Deleting a key that does not exist is an error (same rule as KvStore).
        if (!TryGetLive(key, out _))
            throw new KeyNotFoundInStoreException(key);

        // We can't edit old SSTable files, so we write a tombstone instead.
        _wal.Append(key, "", isDeleted: true);
        _memtable[key] = new Entry("", true);

        FlushIfFull();
    }

    public IEnumerable<KeyValuePair<string, string>> GetRange(string startKey, string endKey)
    {
        // Collect entries from OLDEST to NEWEST. A newer entry for the same key
        // replaces the older one, and a newer tombstone hides an older value.
        var merged = new SortedDictionary<string, Entry>(StringComparer.Ordinal);

        foreach (SsTable table in _tables)
            foreach (var kvp in table.ReadRange(startKey, endKey))
                merged[kvp.Key] = kvp.Value;

        foreach (var kvp in _memtable)
        {
            if (string.CompareOrdinal(kvp.Key, startKey) >= 0 &&
                string.CompareOrdinal(kvp.Key, endKey) <= 0)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        // Hand back only the keys that still exist.
        foreach (var kvp in merged)
        {
            if (!kvp.Value.IsDeleted)
                yield return new KeyValuePair<string, string>(kvp.Key, kvp.Value.Value);
        }
    }

    // Saves the memtable as a new SSTable file and empties it.
    // Public so tests (and later you) can force a flush.
    public void Flush()
    {
        if (_memtable.Count == 0)
            return;

        string path = Path.Combine(_directory, $"sst-{_nextTableId:D10}.sst");

        // Step 1: write the SSTable (atomic: temp file, then rename).
        // The memtable is a SortedDictionary, so it is already sorted by key.
        SsTable table = SsTable.Write(path, _memtable);

        _tables.Add(table);
        _nextTableId++;
        _memtable.Clear();

        // Step 2: only now empty the WAL, because the data is safe in the SSTable.
        // If we crash between step 1 and 2, the WAL just replays records that are
        // already in the SSTable. That is harmless.
        _wal.Reset();

        // Too many files make reads slow, so clean up when we reach the limit.
        if (_tables.Count >= _compactionThreshold)
            Compact();
    }

    // Merges ALL SSTables into one new SSTable.
    // - Only the newest value of each key is kept.
    // - Deleted keys (tombstones) are dropped for good. This is safe ONLY because
    //   we merge every table, so no older table is left that a tombstone must hide.
    // Public so tests can force a compaction.
    public void Compact()
    {
        if (_tables.Count < 2)
            return; // nothing to merge

        // Remember which tables we are merging (oldest first).
        var oldTables = new List<SsTable>(_tables);

        // The merged table gets a NEW, higher number, so it counts as the newest.
        string path = Path.Combine(_directory, $"sst-{_nextTableId:D10}.sst");

        // Step 1: write the merged table (atomic: temp file, then rename).
        // MergeTables hands out entries one at a time, so memory stays small.
        SsTable merged = SsTable.Write(path, MergeTables(oldTables));
        _nextTableId++;

        // Step 2: switch over in memory.
        _tables.Clear();
        _tables.Add(merged);

        // Step 3: delete the old files, OLDEST FIRST.
        // If we crash in the middle, the remaining old files plus the merged file
        // still give correct answers (the merged file is the newest). Deleting the
        // oldest first matters: a newer tombstone must never outlive... be removed
        // before the older value it hides.
        foreach (SsTable old in oldTables)
            File.Delete(old.FilePath);
    }

    // Merges several sorted tables (given oldest first) into ONE sorted stream.
    // Idea: look at the front entry of every table, take the smallest key,
    // and move forward in every table that had that key. The newest table wins.
    private static IEnumerable<KeyValuePair<string, Entry>> MergeTables(List<SsTable> tables)
    {
        // One "reader" per table. IEnumerator is what a foreach loop uses behind the scenes.
        var readers = new List<IEnumerator<KeyValuePair<string, Entry>>>();

        try
        {
            foreach (SsTable table in tables)
                readers.Add(table.ReadAll().GetEnumerator());

            // hasCurrent[i] = does reader i still have an entry? (false = that table is finished)
            var hasCurrent = new bool[readers.Count];
            for (int i = 0; i < readers.Count; i++)
                hasCurrent[i] = readers[i].MoveNext();

            while (true)
            {
                // 1) Find the smallest key among the front entries.
                string? smallest = null;
                for (int i = 0; i < readers.Count; i++)
                {
                    if (!hasCurrent[i])
                        continue;

                    string key = readers[i].Current.Key;
                    if (smallest is null || string.CompareOrdinal(key, smallest) < 0)
                        smallest = key;
                }

                // Every table is finished: we are done.
                if (smallest is null)
                    yield break;

                // 2) Take that key from every table that has it.
                //    Tables are oldest first, so a later table overwrites 'winner'
                //    and the NEWEST entry ends up winning.
                Entry winner = default;
                for (int i = 0; i < readers.Count; i++)
                {
                    if (hasCurrent[i] && readers[i].Current.Key == smallest)
                    {
                        winner = readers[i].Current.Value;
                        hasCurrent[i] = readers[i].MoveNext(); // move that table forward
                    }
                }

                // 3) Tombstones are dropped; real values go into the merged table.
                if (!winner.IsDeleted)
                    yield return new KeyValuePair<string, Entry>(smallest, winner);
            }
        }
        finally
        {
            // Always close every reader (and so every open file), even if something fails.
            foreach (var reader in readers)
                reader.Dispose();
        }
    }

    private void FlushIfFull()
    {
        if (_memtable.Count >= _maxMemtableEntries)
            Flush();
    }

    // Finds the newest entry for a key. Returns true only if the key exists
    // (not found, or deleted by a tombstone, both give false).
    private bool TryGetLive(string key, out string value)
    {
        // 1) The memtable has the newest data.
        if (_memtable.TryGetValue(key, out Entry entry))
        {
            value = entry.Value;
            return !entry.IsDeleted;
        }

        // 2) Then the SSTables, newest to oldest. The first one that knows the key wins.
        for (int i = _tables.Count - 1; i >= 0; i--)
        {
            if (_tables[i].TryGet(key, out Entry found))
            {
                value = found.Value;
                return !found.IsDeleted;
            }
        }

        // 3) Nobody knows this key.
        value = "";
        return false;
    }

    // "sst-0000000007.sst" -> 7
    private static int ParseTableId(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath); // "sst-0000000007"
        return int.Parse(name.Substring("sst-".Length));
    }

    // Closes the WAL. The memtable is NOT flushed here: its data is safe in the
    // WAL and gets replayed next time the store opens.
    public void Dispose()
    {
        _wal.Dispose();
    }
}