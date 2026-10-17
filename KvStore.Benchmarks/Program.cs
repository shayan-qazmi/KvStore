using System.Diagnostics;
using KvStore.Core;

// Usage (from the solution folder):
//   dotnet run -c Release --project KvStore.Benchmarks -- engine
//   dotnet run -c Release --project KvStore.Benchmarks -- engine --fast
//   dotnet run -c Release --project KvStore.Benchmarks -- http
// Extra options:  --sizes 1000,10000,100000    --url http://localhost:5080

// ---------- Read the command line ----------
string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "engine";
int[] sizes = { 1_000, 10_000 };          // how many keys to write in each round
bool syncOnEveryWrite = true;             // engine mode only; --fast turns it off
string baseUrl = "http://localhost:5080"; // http mode only

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sizes": sizes = args[++i].Split(',').Select(int.Parse).ToArray(); break;
        case "--fast": syncOnEveryWrite = false; break;
        case "--url": baseUrl = args[++i]; break;
    }
}

// A fixed 100-character filler, so every value is about 110 bytes.
string padding = new string('x', 100);
string MakeValue(int index) => $"value-{index}-{padding}";

// ---------- Choose what to measure ----------
if (mode == "engine")
{
    // Measures the database directly, with no network. Uses a temporary folder.
    string directory = Path.Combine(Path.GetTempPath(), $"kvstore-bench-{Guid.NewGuid()}");
    string label = syncOnEveryWrite ? "Engine, safe mode" : "Engine, fast mode";

    try
    {
        using (var store = new ConcurrentLsmStore(directory, syncOnEveryWrite: syncOnEveryWrite))
        {
            RunBenchmark(label, store.Put, store.Get);
        }
    }
    finally
    {
        // Delete the temporary database (after the store is closed).
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
else if (mode == "http")
{
    // Measures the whole path: HTTP request -> API -> store -> HTTP answer.
    // The API must already be running (see the commands above).
    // One HttpClient is reused for every request (creating one per request is a classic mistake).
    using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

    RunBenchmark(
        "HTTP API",
        (key, value) =>
        {
            using var content = new StringContent(value);
            using var response = client.PutAsync($"/kv/{Uri.EscapeDataString(key)}", content)
                .GetAwaiter().GetResult(); // wait for the answer before sending the next request
            response.EnsureSuccessStatusCode();
        },
        key => client.GetStringAsync($"/kv/{Uri.EscapeDataString(key)}")
            .GetAwaiter().GetResult());
}
else
{
    Console.WriteLine("Usage: engine [--fast] [--sizes 1000,10000] | http [--url http://localhost:5080]");
}

// ---------- The benchmark itself ----------
void RunBenchmark(string label, Action<string, string> put, Func<string, string> get)
{
    // Warm-up: the first calls are slower while .NET prepares the code. Not timed.
    for (int i = 0; i < 200; i++)
    {
        put($"warmup-{i}", "x");
        get($"warmup-{i}");
    }

    Console.WriteLine();
    Console.WriteLine($"Results for: {label}");
    Console.WriteLine("| Keys written | Keys already stored | Writes/sec | Reads/sec |");
    Console.WriteLine("|---|---|---|---|");

    long alreadyStored = 0;

    foreach (int size in sizes)
    {
        // Keys for this round. The prefix makes them different from earlier rounds,
        // so the store keeps GROWING from round to round.
        string[] keys = Enumerable.Range(0, size).Select(i => $"s{size}-key{i:D7}").ToArray();

        // ----- Writes: one after another -----
        Console.Error.WriteLine($"  writing {size:N0} keys ...");
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < size; i++)
            put(keys[i], MakeValue(i));
        timer.Stop();
        double writesPerSecond = size / timer.Elapsed.TotalSeconds;

        // ----- Reads: the same keys, in random order (seed 42 = same order every run) -----
        var random = new Random(42);
        int[] order = Enumerable.Range(0, size).OrderBy(_ => random.Next()).ToArray();

        Console.Error.WriteLine($"  reading {size:N0} keys ...");
        timer.Restart();
        foreach (int index in order)
        {
            // Also check the answer is correct, so we never time wrong results.
            if (get(keys[index]) != MakeValue(index))
                throw new InvalidOperationException($"Wrong value for {keys[index]}");
        }
        timer.Stop();
        double readsPerSecond = size / timer.Elapsed.TotalSeconds;

        Console.WriteLine($"| {size:N0} | {alreadyStored:N0} | {writesPerSecond:N0} | {readsPerSecond:N0} |");
        alreadyStored += size;
    }
}