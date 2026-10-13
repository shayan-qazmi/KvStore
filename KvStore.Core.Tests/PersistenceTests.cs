using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class PersistenceTests : IDisposable
{
    // Each test gets its own temp file with a unique name,
    // so tests never interfere with each other.
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kvstore-test-{Guid.NewGuid()}.db");

    // xUnit makes a new copy of this class for every test and calls Dispose()
    // afterwards. We use it to delete the temp file.
    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // Helper: writes keys a, b, c and closes the store (like the program exiting).
    private void WriteThreeKeys()
    {
        using var store = new KvStore(_path);
        store.Put("a", "1");
        store.Put("b", "2");
        store.Put("c", "3");
    }

    // ---------- Tests from Phase 2 ----------

    [Fact]
    public void Data_Survives_Restart()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("name", "shayan");
            store.Put("color", "blue");
        } // closed here = program exited

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

    // ---------- Updated format test (new record layout) ----------

    [Fact]
    public void Record_Is_Written_In_The_Documented_Format()
    {
        using (var store = new KvStore(_path))
        {
            store.Put("hi", "yo");
        }

        byte[] bytes = File.ReadAllBytes(_path);

        // The body is 4 + 2 + 4 + 2 + 1 = 13 bytes, plus an 8 byte header = 21.
        Assert.Equal(21, bytes.Length);

        // Bytes 4..8 hold the body length (13, little-endian).
        Assert.Equal(new byte[] { 13, 0, 0, 0 }, bytes[4..8]);

        // Everything after the 8 byte header is the body.
        byte[] expectedBody =
        {
            2, 0, 0, 0,   // key length = 2
            0x68, 0x69,   // "hi"
            2, 0, 0, 0,   // value length = 2
            0x79, 0x6F,   // "yo"
            0             // deleted flag: not deleted
        };
        Assert.Equal(expectedBody, bytes[8..]);
    }

    // ---------- New Phase 3 crash tests ----------

    [Fact]
    public void Half_Written_Last_Record_Is_Ignored()
    {
        WriteThreeKeys();

        // Simulate a crash in the middle of writing record "c":
        // chop the last 5 bytes off the file.
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 5);
        }

        // Starting the store must NOT crash, and a and b must still be there.
        using var recovered = new KvStore(_path);
        Assert.Equal("1", recovered.Get("a"));
        Assert.Equal("2", recovered.Get("b"));
        Assert.Throws<KeyNotFoundInStoreException>(() => recovered.Get("c"));
    }

    [Fact]
    public void Store_Keeps_Working_After_Recovering_From_Half_Written_Record()
    {
        WriteThreeKeys();

        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write))
        {
            fs.SetLength(fs.Length - 5); // crash in the middle of "c"
        }

        // Recover, then write a new key.
        using (var recovered = new KvStore(_path))
        {
            recovered.Put("d", "4");
        }

        // Restart again. If the broken tail had NOT been cut off, the new
        // record "d" would sit behind garbage and never be read.
        using var final = new KvStore(_path);
        Assert.Equal("1", final.Get("a"));
        Assert.Equal("2", final.Get("b"));
        Assert.Equal("4", final.Get("d"));
    }

    [Fact]
    public void Damaged_Record_Is_Detected_By_Checksum()
    {
        WriteThreeKeys();

        // Flip the bits of one byte inside the last record's value.
        // bytes[^1] is the last byte (the deleted flag), so bytes[^2] is the
        // last byte of the value "3".
        byte[] bytes = File.ReadAllBytes(_path);
        bytes[^2] ^= 0xFF;
        File.WriteAllBytes(_path, bytes);

        using var recovered = new KvStore(_path);
        Assert.Equal("1", recovered.Get("a"));
        Assert.Equal("2", recovered.Get("b"));
        Assert.Throws<KeyNotFoundInStoreException>(() => recovered.Get("c"));
    }

    [Fact]
    public void Tiny_Garbage_At_End_Is_Ignored()
    {
        WriteThreeKeys();

        // Simulate a crash that wrote only 3 bytes of a new header.
        using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write))
        {
            fs.Write(new byte[] { 1, 2, 3 });
        }

        using var recovered = new KvStore(_path);
        Assert.Equal("1", recovered.Get("a"));
        Assert.Equal("2", recovered.Get("b"));
        Assert.Equal("3", recovered.Get("c"));
    }
}