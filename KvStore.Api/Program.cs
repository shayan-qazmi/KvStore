using System.Text;
using KvStore.Core;

var builder = WebApplication.CreateBuilder(args);

// ---------- Register the store as a singleton ----------
// "Singleton" = ONE store shared by all requests. The framework creates it the
// first time somebody asks for it, and closes it properly when the app stops.
// Settings can be changed from outside without editing code:
//   Storage:Directory         folder for the database files
//   Storage:SyncOnEveryWrite  true (default, safe) or false (faster, see benchmarks)
builder.Services.AddSingleton(serviceProvider =>
{
    var config = serviceProvider.GetRequiredService<IConfiguration>();

    string directory = config["Storage:Directory"]
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KvStoreApi",
            "data");

    bool syncOnEveryWrite = config.GetValue("Storage:SyncOnEveryWrite", true);

    return new ConcurrentLsmStore(directory, syncOnEveryWrite: syncOnEveryWrite);
});

var app = builder.Build();

// Open the store when the server starts, so the first request is not slow.
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<ConcurrentLsmStore>());

// ---------- Endpoints ----------
// "MapXxx(path, function)" = when a request with this verb and path arrives,
// run this function. Parameters are filled in automatically:
//   {key} in the path     -> string key
//   ?start=...&end=...    -> string? start, string? end
//   ConcurrentLsmStore    -> our singleton, handed over by dependency injection

// A friendly page to check that the server is running.
app.MapGet("/", () => "KvStore API is running. Try GET /kv/{key}");

// PUT /kv/{key}   body = the value (plain text)   -> 204 No Content
app.MapPut("/kv/{key}", async (string key, HttpRequest request, ConcurrentLsmStore store) =>
{
    // Read the whole request body as text. "await" = while waiting for the bytes
    // to arrive, this thread is free to serve other requests.
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    string value = await reader.ReadToEndAsync();

    store.Put(key, value);
    return Results.NoContent(); // 204: saved, nothing to send back
});

// GET /kv/{key}   -> 200 + the value as text, or 404 if the key does not exist
app.MapGet("/kv/{key}", (string key, ConcurrentLsmStore store) =>
{
    try
    {
        return Results.Text(store.Get(key), "text/plain; charset=utf-8");
    }
    catch (KeyNotFoundInStoreException)
    {
        return Results.NotFound(); // 404
    }
});

// DELETE /kv/{key}   -> 204 if deleted, 404 if the key does not exist
app.MapDelete("/kv/{key}", (string key, ConcurrentLsmStore store) =>
{
    try
    {
        store.Delete(key);
        return Results.NoContent(); // 204
    }
    catch (KeyNotFoundInStoreException)
    {
        return Results.NotFound(); // 404
    }
});

// GET /kv?start=a&end=c   -> 200 + JSON list of {key, value}, sorted by key
app.MapGet("/kv", (string? start, string? end, ConcurrentLsmStore store) =>
{
    if (start is null || end is null)
        return Results.BadRequest("Both 'start' and 'end' query parameters are required."); // 400

    var items = store.GetRange(start, end)
        .Select(pair => new KeyValueDto(pair.Key, pair.Value));

    return Results.Ok(items); // 200, converted to JSON automatically
});

app.Run();

// The shape of one item in the JSON answer: {"key":"a","value":"1"}
public record KeyValueDto(string Key, string Value);

// This empty line of code makes the "Program" class visible to the test project.
public partial class Program { }