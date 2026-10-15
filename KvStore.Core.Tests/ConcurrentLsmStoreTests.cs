using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class ConcurrentLsmStoreTests : IDisposable
{
    // Each test gets its own temp folder.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kvstore-conc-{Guid.NewGuid()}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Many_Threads_Writing_At_Once_Lose_Nothing()
    {
        // Tiny limits, so flushes and compactions happen WHILE threads are writing.
        // syncOnEveryWrite is off only to keep the test quick; the lock is what we test.
        using var store = new ConcurrentLsmStore(
            _dir, maxMemtableEntries: 10, compactionThreshold: 3, syncOnEveryWrite: false);

        const int threads = 8;
        const int perThread = 100;

        // Parallel.For runs the loop body on several threads at the same time.
        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < perThread; i++)
                store.Put($"t{t}-key{i:D3}", $"value-{t}-{i}");
        });

        // Every single write from every thread must be there.
        for (int t = 0; t < threads; t++)
            for (int i = 0; i < perThread; i++)
                Assert.Equal($"value-{t}-{i}", store.Get($"t{t}-key{i:D3}"));

        Assert.Equal(threads * perThread, store.GetRange("a", "z").Count);
    }

    [Fact]
    public void Reading_While_Writing_Does_Not_Crash()
    {
        using var store = new ConcurrentLsmStore(
            _dir, maxMemtableEntries: 10, compactionThreshold: 3, syncOnEveryWrite: false);

        store.Put("seed", "1");

        // One thread writes new keys...
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
                store.Put($"w{i:D3}", "x");
        });

        // ...while another thread keeps reading and scanning.
        // Without the lock, scanning a dictionary that is being changed throws an exception.
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 300; i++)
            {
                Assert.Equal("1", store.Get("seed"));
                store.GetRange("a", "z");
            }
        });

        // If either thread threw, WaitAll throws too and the test fails.
        Task.WaitAll(writer, reader);
    }
}