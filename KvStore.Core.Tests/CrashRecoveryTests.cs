using System.Diagnostics;
using KvStore.Core;
using Xunit;

namespace KvStore.Core.Tests;

public class CrashRecoveryTests : IDisposable
{
    // A unique temp file for this test. It is deleted afterwards.
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"kvstore-crash-{Guid.NewGuid()}.db");

    // Must be exactly the same as bigValue in the writer's Program.cs.
    private static readonly string BigValue = new string('x', 20_000);

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    // Finds the writer program that Visual Studio built.
    // The tests run inside:   <solution>\KvStore.Core.Tests\bin\<Config>\<Framework>\
    // The writer lives in:    <solution>\KvStore.CrashWriter\bin\<Config>\<Framework>\
    private static string FindWriterExe()
    {
        var frameworkDir = new DirectoryInfo(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string framework = frameworkDir.Name;                  // for example "net10.0"
        string config = frameworkDir.Parent!.Name;             // for example "Debug"

        // Go up 4 folders: framework -> config -> bin -> test project -> solution folder.
        DirectoryInfo solutionDir = frameworkDir.Parent!.Parent!.Parent!.Parent!;

        string exeName = "KvStore.CrashWriter" + (OperatingSystem.IsWindows() ? ".exe" : "");

        return Path.Combine(solutionDir.FullName, "KvStore.CrashWriter", "bin", config, framework, exeName);
    }

    // Starts the writer, waits until it has reported 'killAfterAcks' saved
    // records, then kills it instantly (like a crash).
    // Returns the number of the last record the writer reported as saved.
    private int RunWriterAndKill(string exe, int startIndex, int killAfterAcks)
    {
        var startInfo = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, // so we can read the numbers it prints
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(_path);
        startInfo.ArgumentList.Add(startIndex.ToString());

        using var process = Process.Start(startInfo)!;

        int lastAcknowledged = startIndex - 1;

        for (int n = 0; n < killAfterAcks; n++)
        {
            string? line = process.StandardOutput.ReadLine();

            // null means the writer stopped by itself, which should not happen.
            if (line is null)
                throw new InvalidOperationException(
                    "The writer stopped early: " + process.StandardError.ReadToEnd());

            lastAcknowledged = int.Parse(line);
        }

        // Kill = stop immediately. No cleanup, no Dispose, no chance to finish a write.
        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        return lastAcknowledged;
    }

    // Opens the store after a crash and checks everything is correct.
    // Returns the number of the last record that exists in the store.
    private int VerifyAfterCrash(int lastAcknowledged)
    {
        // Opening must NOT throw, even if the file ends with a half-written record.
        using var store = new KvStore(_path);

        var pairs = store.GetRange("key-000000", "key-999999").ToList();

        // Records must be key-000000, key-000001, ... with NO gaps,
        // and every value must be complete and correct.
        for (int i = 0; i < pairs.Count; i++)
        {
            Assert.Equal($"key-{i:D6}", pairs[i].Key);
            Assert.Equal($"{i}-{BigValue}", pairs[i].Value);
        }

        // Everything the writer reported as saved must still be here.
        // (There can be MORE records than we saw, because the writer kept
        // working for a moment before the kill.)
        Assert.True(pairs.Count - 1 >= lastAcknowledged,
            $"Lost data! Writer reported {lastAcknowledged} as saved, but the store only has up to {pairs.Count - 1}.");

        return pairs.Count - 1;
    }

    [Fact]
    public void Saved_Records_Survive_Being_Killed_Ten_Times()
    {
        string exe = FindWriterExe();
        Assert.True(File.Exists(exe), $"Writer not found at: {exe}. Build the whole solution first.");

        var random = new Random();
        int nextIndex = 0; // where the next writer should start counting

        for (int round = 1; round <= 10; round++)
        {
            // Kill after a random number of saves, so each round is different.
            int killAfter = random.Next(20, 80);

            int lastAcknowledged = RunWriterAndKill(exe, nextIndex, killAfter);
            int lastRecovered = VerifyAfterCrash(lastAcknowledged);

            nextIndex = lastRecovered + 1;
        }

        // 10 rounds x at least 20 saves each = at least 200 records survived.
        Assert.True(nextIndex >= 200, $"Only {nextIndex} records survived.");
    }
}