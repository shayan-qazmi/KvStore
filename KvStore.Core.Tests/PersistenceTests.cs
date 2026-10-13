using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class PersistenceTests : IDisposable
{
    // A unique temp file per test, so tests never interfere with each other.
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kvstore-test-{Guid.NewGuid()}.db");

    // xUnit creates a fresh instance of this class for every test and calls
    // Dispose() afterwards, so this deletes the temp file after each test.
    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void Data_Survives_Restart()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("name", "shayan");
            store.Put("color", "blue");
        } // store is closed here, which simulates the program exiting

        using (var reopened = new KvStore(_path))
        {
            Assert.Equal("shayan", reopened.Get("name"));
            Assert.Equal("blue", reopened.Get("color"));
        }
    }

    [Fact]
    public void Deleted_Key_Stays_Deleted_After_Restart()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("k", "v");
            store.Delete("k");
        }

        using (var reopened = new KvStore(_path))
        {
            Assert.Throws<KeyNotFoundInStoreException>(() => reopened.Get("k"));
        }
    }

    [Fact]
    public void Latest_Value_Wins_After_Restart()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("k", "first");
            store.Put("k", "second");
        }

        using (var reopened = new KvStore(_path))
        {
            Assert.Equal("second", reopened.Get("k"));
        }
    }

    [Fact]
    public void Non_Ascii_Text_Survives_Restart()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("größe", "日本語 héllo");
        }

        using (var reopened = new KvStore(_path))
        {
            Assert.Equal("日本語 héllo", reopened.Get("größe"));
        }
    }

    [Fact]
    public void Missing_File_Starts_Empty()
    {
        using var store = new KvStore(_path);

        Assert.Empty(store.GetRange("a", "z"));
    }

    [Fact]
    public void Record_Is_Written_In_The_Documented_Format()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("hi", "yo");
        }

        byte[] expected =
        {
            2, 0, 0, 0,   // key length = 2
            0x68, 0x69,   // "hi"
            2, 0, 0, 0,   // value length = 2
            0x79, 0x6F,   // "yo"
            0             // deleted flag: not deleted
        };

        Assert.Equal(expected, File.ReadAllBytes(_path));
    }
}