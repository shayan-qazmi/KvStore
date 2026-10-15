using System.IO.Hashing; // Crc32 (from the NuGet package)
using System.Text;

namespace KvStore.Core;

// A crash-safe, append-only log file.
// Every record is: [4 bytes checksum][4 bytes body length][body]
// body = [4 key length][key][4 value length][value][1 deleted flag]
public sealed class WriteAheadLog : IDisposable
{
    // Header = 4 bytes checksum + 4 bytes body length.
    private const int HeaderSize = 8;

    private readonly FileStream _file;
    private readonly BinaryWriter _writer;

    // true  = force every record onto the physical disk (safest, slower)
    // false = hand records to Windows only (faster; a power cut can lose the last few)
    private readonly bool _syncOnEveryWrite;

    private bool _disposed;

    // Private: the only way to get a log is WriteAheadLog.Open(...).
    private WriteAheadLog(FileStream file, bool syncOnEveryWrite)
    {
        _file = file;
        _writer = new BinaryWriter(file, Encoding.UTF8);
        _syncOnEveryWrite = syncOnEveryWrite;
    }

    // Opens (or creates) the log file.
    // 'apply' is called once for every good record already in the file,
    // in order, so the caller can rebuild its memory.
    // Anything broken at the end of the file is cut off.
    public static WriteAheadLog Open(
        string path,
        Action<string, string, bool> apply,
        bool syncOnEveryWrite = true)
    {
        long goodLength = 0; // how many bytes at the start are good records

        if (File.Exists(path))
            goodLength = Replay(path, apply);

        // OpenOrCreate = use the file if it exists, otherwise make a new one.
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

        // Cut off a half-written record left by a crash (does nothing if the file was fine).
        file.SetLength(goodLength);

        // Go to the end so new records are added after the good ones.
        file.Seek(0, SeekOrigin.End);

        return new WriteAheadLog(file, syncOnEveryWrite);
    }

    // Adds one record to the end of the log.
    public void Append(string key, string value, bool isDeleted)
    {
        byte[] body = BuildBody(key, value, isDeleted);

        // A number calculated from the body. If any byte changes later,
        // the number no longer matches, and we know the record is damaged.
        uint checksum = Crc32.HashToUInt32(body);

        _writer.Write(checksum);    // 4 bytes
        _writer.Write(body.Length); // 4 bytes
        _writer.Write(body);        // the body itself

        _writer.Flush(); // move bytes from the writer into Windows

        // Force the bytes onto the physical disk. This is the slow part.
        if (_syncOnEveryWrite)
            _file.Flush(true);
    }

    // Empties the log. Called after the memtable was safely saved as an SSTable.
    public void Reset()
    {
        _file.SetLength(0);
        _file.Seek(0, SeekOrigin.Begin);
        _file.Flush(true);
    }

    // Builds the body of a record in memory.
    private static byte[] BuildBody(string key, string value, bool isDeleted)
    {
        // Files hold bytes, not text, so turn the text into bytes first.
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value);

        // MemoryStream = a "file" that lives in memory.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(keyBytes.Length);
        writer.Write(keyBytes);
        writer.Write(valueBytes.Length);
        writer.Write(valueBytes);
        writer.Write(isDeleted);
        writer.Flush();

        return stream.ToArray();
    }

    // Reads the whole file, calling 'apply' for each good record.
    // Stops at the first broken record.
    // Returns how many bytes at the start of the file are good.
    private static long Replay(string path, Action<string, string, bool> apply)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        long lastGoodPosition = 0;

        while (true)
        {
            // Not enough bytes left for a header: end of file, or a crash cut it in half.
            if (stream.Length - stream.Position < HeaderSize)
                break;

            uint storedChecksum = reader.ReadUInt32();
            int bodyLength = reader.ReadInt32();

            // A negative length, or one longer than the rest of the file, is garbage.
            if (bodyLength < 0 || bodyLength > stream.Length - stream.Position)
                break;

            byte[] body = reader.ReadBytes(bodyLength);

            // Recalculate the checksum. A different number means a damaged record.
            if (Crc32.HashToUInt32(body) != storedChecksum)
                break;

            // The record is good: read its parts and hand them to the caller.
            using var bodyStream = new MemoryStream(body);
            using var bodyReader = new BinaryReader(bodyStream, Encoding.UTF8);

            int keyLength = bodyReader.ReadInt32();
            string key = Encoding.UTF8.GetString(bodyReader.ReadBytes(keyLength));
            int valueLength = bodyReader.ReadInt32();
            string value = Encoding.UTF8.GetString(bodyReader.ReadBytes(valueLength));
            bool isDeleted = bodyReader.ReadBoolean();

            apply(key, value, isDeleted);

            // Remember where the last good record ended.
            lastGoodPosition = stream.Position;
        }

        return lastGoodPosition;
    }

    // Closes the file.
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        // In fast mode, records may still be only in Windows' memory.
        // A normal shutdown pushes them to the disk once, here.
        if (!_syncOnEveryWrite)
        {
            _writer.Flush();
            _file.Flush(true);
        }

        _writer.Dispose();
        _file.Dispose();
    }
}