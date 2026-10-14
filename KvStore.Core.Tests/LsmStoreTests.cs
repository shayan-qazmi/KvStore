using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class LsmStoreTests : IDisposable
{
    // Each test gets its own temp FOLDER with a unique name.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kvstore-lsm-{Guid.NewGuid()}");

    private string WalPath => Path.Combine(_dir, "wal.log");

    // Opens a store on this test's folder. 'limit' = memtable size before a flush.
    private LsmStore Open(int limit = 100) => new LsmStore(_dir, limit);

    // Runs after every test: delete the temp folder and everything in it.
    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Basic_Put_Get_Delete_Work()
    {
        using var store = Open();
        store.Put("k", "v");
        Assert.Equal("v", store.Get("k"));

        store.Put("k", "v2"); // overwrite
        Assert.Equal("v2", store.Get("k"));

        store.Delete("k");
        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("k"));
        Assert.Throws<KeyNotFoundInStoreException>(() => store.Delete("k"));
    }

    [Fact]
    public void Flush_Creates_SsTable_And_Empties_The_Wal()
    {
        using var store = Open(limit: 3);
        store.Put("a", "1");
        store.Put("b", "2");

        // Two entries: not full yet, so no SSTable, and the WAL holds the data.
        Assert.Equal(0, store.SsTableCount);
        Assert.True(new FileInfo(WalPath).Length > 0);

        store.Put("c", "3"); // third entry hits the limit -> flush

        Assert.Equal(1, store.SsTableCount);
        Assert.Single(Directory.GetFiles(_dir, "*.sst"));
        Assert.Equal(0, new FileInfo(WalPath).Length); // WAL emptied
    }

    [Fact]
    public void Reads_Find_Data_In_SsTables_And_Memtable()
    {
        using var store = Open(limit: 2);
        store.Put("a", "1");
        store.Put("b", "2"); // flush -> table 1
        store.Put("c", "3");
        store.Put("d", "4"); // flush -> table 2
        store.Put("e", "5"); // stays in the memtable

        Assert.Equal(2, store.SsTableCount);
        Assert.Equal("1", store.Get("a"));
        Assert.Equal("2", store.Get("b"));
        Assert.Equal("3", store.Get("c"));
        Assert.Equal("4", store.Get("d"));
        Assert.Equal("5", store.Get("e"));
        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("zzz"));
    }

    [Fact]
    public void Newer_Value_Hides_Older_Value()
    {
        using var store = Open(limit: 2);
        store.Put("k", "old");
        store.Put("x", "1"); // flush -> table 1 holds k=old
        store.Put("k", "new");
        store.Put("y", "2"); // flush -> table 2 holds k=new

        Assert.Equal(2, store.SsTableCount);
        Assert.Equal("new", store.Get("k")); // newest table wins
    }

    [Fact]
    public void Delete_Hides_Key_That_Lives_In_An_SsTable()
    {
        using var store = Open();
        store.Put("k", "v");
        store.Flush(); // k now lives only in an SSTable

        store.Delete("k"); // writes a tombstone

        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("k"));

        store.Flush(); // the tombstone itself becomes an SSTable
        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("k"));
    }

    [Fact]
    public void Everything_Survives_Restart()
    {
        using (var store = Open(limit: 3))
        {
            // 7 puts: two flushes (3 + 3), and 1 entry left only in the WAL.
            for (int i = 1; i <= 7; i++)
                store.Put($"key{i}", $"value{i}");
        }

        using var reopened = Open(limit: 3);
        Assert.Equal(2, reopened.SsTableCount);

        for (int i = 1; i <= 7; i++)
            Assert.Equal($"value{i}", reopened.Get($"key{i}"));
    }

    [Fact]
    public void Deleted_Key_Stays_Deleted_After_Restart()
    {
        using (var store = Open())
        {
            store.Put("k", "v");
            store.Flush();
            store.Delete("k"); // the tombstone is only in the WAL
        }

        using var reopened = Open();
        Assert.Throws<KeyNotFoundInStoreException>(() => reopened.Get("k"));
    }

    [Fact]
    public void GetRange_Merges_Memtable_And_SsTables()
    {
        using var store = Open();
        store.Put("a", "1");
        store.Put("b", "2");
        store.Flush(); // table 1: a, b
        store.Put("c", "3");
        store.Put("d", "4");
        store.Flush(); // table 2: c, d
        store.Put("e", "5"); // memtable: e
        store.Delete("b");   // memtable: b (tombstone), e

        var keys = store.GetRange("a", "z").Select(p => p.Key).ToList();

        Assert.Equal(new[] { "a", "c", "d", "e" }, keys); // b is gone, order is sorted
    }

    [Fact]
    public void Replaying_Wal_After_Flush_Is_Harmless()
    {
        // Simulates a crash between "SSTable written" and "WAL emptied".
        using (var store = Open())
        {
            store.Put("a", "1");
            store.Put("b", "2");
        }
        byte[] walBefore = File.ReadAllBytes(WalPath); // WAL holding a and b

        using (var store = Open())
        {
            store.Flush(); // a and b go into an SSTable, WAL is emptied
        }

        // Put the old WAL contents back, as if the crash stopped the reset.
        File.WriteAllBytes(WalPath, walBefore);

        using var reopened = Open();
        Assert.Equal(1, reopened.SsTableCount);
        Assert.Equal("1", reopened.Get("a"));
        Assert.Equal("2", reopened.Get("b"));
        Assert.Equal(2, reopened.GetRange("a", "z").Count()); // no duplicates
    }

    [Fact]
    public void Leftover_Temp_File_Is_Removed()
    {
        // Simulates a crash in the middle of writing an SSTable.
        Directory.CreateDirectory(_dir);
        string tempFile = Path.Combine(_dir, "sst-0000000001.sst.tmp");
        File.WriteAllText(tempFile, "half-written garbage");

        using var store = Open();
        store.Put("k", "v");

        Assert.Equal("v", store.Get("k"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp")); // cleaned up on startup
    }

    [Fact]
    public void Keys_Are_Sorted_By_Character_Value()
    {
        using var store = Open(limit: 2);
        store.Put("b", "1");
        store.Put("A", "2"); // flush
        store.Put("a", "3");
        store.Put("B", "4"); // flush

        var keys = store.GetRange("A", "z").Select(p => p.Key).ToList();

        // Ordinal order: capital letters come before lowercase letters.
        Assert.Equal(new[] { "A", "B", "a", "b" }, keys);
    }
}