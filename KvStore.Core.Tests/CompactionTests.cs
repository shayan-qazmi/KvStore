using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class CompactionTests : IDisposable
{
    // Each test gets its own temp folder.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kvstore-compact-{Guid.NewGuid()}");

    // Automatic compaction is switched off (threshold 1000), so the test decides when it happens.
    private LsmStore OpenManual() =>
        new LsmStore(_dir, maxMemtableEntries: 100, compactionThreshold: 1000);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Compact_Merges_All_SsTables_Into_One()
    {
        using var store = OpenManual();
        store.Put("a", "1");
        store.Put("b", "2");
        store.Flush(); // table 1
        store.Put("c", "3");
        store.Flush(); // table 2
        store.Put("d", "4");
        store.Flush(); // table 3

        Assert.Equal(3, store.SsTableCount);

        store.Compact();

        Assert.Equal(1, store.SsTableCount);
        Assert.Single(Directory.GetFiles(_dir, "*.sst")); // old files are gone from disk
        Assert.Equal("1", store.Get("a"));
        Assert.Equal("2", store.Get("b"));
        Assert.Equal("3", store.Get("c"));
        Assert.Equal("4", store.Get("d"));
    }

    [Fact]
    public void Compact_Removes_Overwritten_Values()
    {
        using var store = OpenManual();
        store.Put("k", "old-value");
        store.Flush();
        store.Put("k", "new-value");
        store.Flush();

        store.Compact();

        Assert.Equal("new-value", store.Get("k"));

        // Look inside the file itself: the old value must be physically gone.
        string raw = File.ReadAllText(Directory.GetFiles(_dir, "*.sst")[0]);
        Assert.DoesNotContain("old-value", raw);
        Assert.Contains("new-value", raw);
    }

    [Fact]
    public void Compact_Removes_Deleted_Keys_For_Good()
    {
        using var store = OpenManual();
        store.Put("secret-key", "secret-value");
        store.Put("x", "1");
        store.Flush();       // table 1 holds the secret
        store.Delete("secret-key");
        store.Flush();       // table 2 holds the tombstone

        store.Compact();

        Assert.Throws<KeyNotFoundInStoreException>(() => store.Get("secret-key"));
        Assert.Single(store.GetRange("a", "z")); // only "x" is left

        // The key must be physically gone from the file, tombstone included.
        string raw = File.ReadAllText(Directory.GetFiles(_dir, "*.sst")[0]);
        Assert.DoesNotContain("secret-key", raw);
    }

    [Fact]
    public void Compaction_Runs_Automatically_When_Threshold_Is_Reached()
    {
        // Flush every 2 entries, compact when 3 tables exist.
        using var store = new LsmStore(_dir, maxMemtableEntries: 2, compactionThreshold: 3);

        for (int i = 1; i <= 6; i++)
            store.Put($"k{i}", $"v{i}");
        // Flushes after put 2, 4 and 6. The third flush makes 3 tables -> compaction -> 1.

        Assert.Equal(1, store.SsTableCount);

        for (int i = 1; i <= 6; i++)
            Assert.Equal($"v{i}", store.Get($"k{i}"));
    }

    [Fact]
    public void Data_Survives_Restart_With_Compaction_Running()
    {
        using (var store = new LsmStore(_dir, 2, 3))
        {
            for (int i = 1; i <= 9; i++)
                store.Put($"key{i}", $"value{i}");

            store.Delete("key2");

            // Compaction keeps the number of tables below the threshold.
            Assert.True(store.SsTableCount <= 2);
        }

        using var reopened = new LsmStore(_dir, 2, 3);
        Assert.True(reopened.SsTableCount <= 2);

        for (int i = 1; i <= 9; i++)
        {
            if (i == 2)
                Assert.Throws<KeyNotFoundInStoreException>(() => reopened.Get("key2"));
            else
                Assert.Equal($"value{i}", reopened.Get($"key{i}"));
        }
    }

    [Fact]
    public void Crash_After_Merged_File_Written_But_Before_Old_Files_Deleted_Is_Still_Correct()
    {
        Dictionary<string, byte[]> oldFiles;

        using (var store = OpenManual())
        {
            store.Put("k", "v");
            store.Put("y", "1");
            store.Flush();               // table 1: k=v, y=1
            store.Delete("k");
            store.Put("z", "2");
            store.Flush();               // table 2: k=tombstone, z=2

            // Keep copies of the two old files.
            oldFiles = Directory.GetFiles(_dir, "*.sst")
                .ToDictionary(path => path, path => File.ReadAllBytes(path));

            store.Compact();             // writes table 3, deletes tables 1 and 2
        }

        // Simulate a crash that happened BEFORE the old files were deleted:
        // put the old files back next to the merged one.
        foreach (var (path, bytes) in oldFiles)
            File.WriteAllBytes(path, bytes);

        using var reopened = OpenManual();
        Assert.Equal(3, reopened.SsTableCount); // old 1, old 2 and the merged one

        // "k" was deleted. The merged table no longer mentions it, but old table 2
        // still has the tombstone, so "k" must stay deleted.
        Assert.Throws<KeyNotFoundInStoreException>(() => reopened.Get("k"));
        Assert.Equal("1", reopened.Get("y"));
        Assert.Equal("2", reopened.Get("z"));
        Assert.Equal(new[] { "y", "z" }, reopened.GetRange("a", "z").Select(p => p.Key).ToList());
    }
}