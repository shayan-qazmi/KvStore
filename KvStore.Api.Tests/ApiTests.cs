using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace KvStore.Api.Tests;

// Used to read the JSON answer of the range endpoint.
public record ApiItem(string Key, string Value);

public class ApiTests : IDisposable
{
    // Each test gets its own temp folder, so tests never share data.
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kvstore-api-{Guid.NewGuid()}");

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiTests()
    {
        // WebApplicationFactory starts our real API in memory.
        // UseSetting points its storage at this test's temp folder.
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Storage:Directory", _dir));

        // A client that sends requests straight into the in-memory server.
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose(); // stops the app, which closes the store's files

        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Helper: PUT a value. Keys are URL-encoded (a space becomes %20).
    private Task<HttpResponseMessage> PutAsync(string key, string value) =>
        _client.PutAsync(
            $"/kv/{Uri.EscapeDataString(key)}",
            new StringContent(value, Encoding.UTF8, "text/plain"));

    [Fact]
    public async Task Put_Then_Get_Returns_Value()
    {
        var put = await PutAsync("name", "shayan");
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode); // 204

        var get = await _client.GetAsync("/kv/name");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode); // 200
        Assert.Equal("shayan", await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Get_Missing_Key_Returns_404()
    {
        var response = await _client.GetAsync("/kv/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Then_Get_Returns_404()
    {
        await PutAsync("k", "v");

        var delete = await _client.DeleteAsync("/kv/k");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var get = await _client.GetAsync("/kv/k");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task Delete_Missing_Key_Returns_404()
    {
        var response = await _client.DeleteAsync("/kv/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Overwriting_A_Key_Updates_The_Value()
    {
        await PutAsync("k", "first");
        await PutAsync("k", "second");

        Assert.Equal("second", await _client.GetStringAsync("/kv/k"));
    }

    [Fact]
    public async Task Range_Returns_Sorted_Json()
    {
        // Saved in a mixed-up order on purpose.
        await PutAsync("b", "B");
        await PutAsync("d", "D");
        await PutAsync("a", "A");
        await PutAsync("c", "C");

        var response = await _client.GetAsync("/kv?start=a&end=c");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<ApiItem>>();

        Assert.NotNull(items);
        Assert.Equal(new[] { "a", "b", "c" }, items!.Select(i => i.Key).ToArray());
        Assert.Equal(new[] { "A", "B", "C" }, items!.Select(i => i.Value).ToArray());
    }

    [Fact]
    public async Task Range_Without_Parameters_Returns_400()
    {
        var response = await _client.GetAsync("/kv");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Keys_With_Spaces_And_Unicode_Values_Work()
    {
        string key = "hello world";
        string value = "日本語 héllo";

        await PutAsync(key, value);

        string back = await _client.GetStringAsync($"/kv/{Uri.EscapeDataString(key)}");
        Assert.Equal(value, back);
    }

    [Fact]
    public async Task Many_Requests_At_Once_Are_All_Saved()
    {
        // Send 100 PUT requests at the same time (this tests the lock for real).
        var tasks = Enumerable.Range(0, 100)
            .Select(i => PutAsync($"key{i:D3}", $"value{i}"))
            .ToList();

        var responses = await Task.WhenAll(tasks);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        var range = await _client.GetFromJsonAsync<List<ApiItem>>("/kv?start=key000&end=key999");
        Assert.Equal(100, range!.Count);
    }
}

// Separate class: it starts and stops the server twice on the SAME folder.
public class RestartTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"kvstore-api-restart-{Guid.NewGuid()}");

    private WebApplicationFactory<Program> CreateServer() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Storage:Directory", _dir));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Data_Survives_Stopping_And_Restarting_The_Server()
    {
        // First server: save something, then shut down.
        using (var firstServer = CreateServer())
        using (var client = firstServer.CreateClient())
        {
            var put = await client.PutAsync("/kv/name", new StringContent("shayan"));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        } // server stops here

        // Second server: same folder, brand new process state.
        using var secondServer = CreateServer();
        using var client2 = secondServer.CreateClient();

        Assert.Equal("shayan", await client2.GetStringAsync("/kv/name"));
    }
}