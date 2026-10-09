using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-040: /api + /v1 Files API round-trip and batch input_file_id.</summary>
public class FileApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public FileApiTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath }))
             .ConfigureServices(s =>
                // batch runners loop back into the app; in tests they must go
                // through the in-memory TestServer handler, not real sockets
                s.AddHttpClient("batches").ConfigurePrimaryHttpMessageHandler(() => _serverHandler())));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private HttpMessageHandler _serverHandler() => _factory.Server.CreateHandler();

    public void Dispose()
    {
        _client.Dispose(); _factory.Dispose();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
    }

    private async Task LoginAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static MultipartFormDataContent UploadBody(string text, string name, string purpose)
    {
        var c = new MultipartFormDataContent();
        c.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text)), "file", name);
        c.Add(new StringContent(purpose), "purpose");
        return c;
    }

    [Fact]
    public async Task Upload_list_content_delete_round_trip()
    {
        await LoginAsync();
        var up = await _client.PostAsync("/api/files", UploadBody("hello files", "a.txt", "assistants"));
        up.StatusCode.ShouldBe(HttpStatusCode.OK);
        var f = await up.Content.ReadFromJsonAsync<JsonElement>();
        var id = f.GetProperty("id").GetString()!;
        f.GetProperty("object").GetString().ShouldBe("file");

        var list = await _client.GetFromJsonAsync<JsonElement>("/api/files");
        list.GetProperty("data").EnumerateArray().ShouldContain(x => x.GetProperty("id").GetString() == id);

        var meta = await _client.GetFromJsonAsync<JsonElement>($"/api/files/{id}");
        meta.GetProperty("filename").GetString().ShouldBe("a.txt");

        var content = await _client.GetStringAsync($"/api/files/{id}/content");
        content.ShouldBe("hello files");

        (await _client.DeleteAsync($"/api/files/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var gone = await _client.GetAsync($"/api/files/{id}");
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task V1_files_requires_api_key()
    {
        // no key → 401
        (await _client.GetAsync("/v1/files")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options);
        db.EnsureCreated();
        db.ApiKeys.Add(new ApiKey { Id = "k1", Key = "sk-files-test", IsActive = true });
        await db.SaveChangesAsync();

        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/files");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "sk-files-test");
        (await _client.SendAsync(req)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Batch_consumes_input_file_and_writes_output_file()
    {
        const string key = "sk-batch-test";
        await using (var db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options))
        {
            db.EnsureCreated();
            db.ApiKeys.Add(new ApiKey { Id = "k1", Key = key, IsActive = true });
            await db.SaveChangesAsync();
        }

        // upload JSONL via /v1/files (api-key auth)
        var jsonl = "{\"custom_id\":\"r1\",\"method\":\"POST\",\"url\":\"/v1/chat/completions\",\"body\":{\"model\":\"openai/x\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}}\n";
        var upReq = new HttpRequestMessage(HttpMethod.Post, "/v1/files") { Content = UploadBody(jsonl, "in.jsonl", "batch") };
        upReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var up = await _client.SendAsync(upReq);
        up.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fileId = (await up.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var bReq = new HttpRequestMessage(HttpMethod.Post, "/v1/batches")
        {
            Content = JsonContent.Create(new { input_file_id = fileId, endpoint = "/v1/chat/completions" }),
        };
        bReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var b = await _client.SendAsync(bReq);
        b.StatusCode.ShouldBe(HttpStatusCode.OK);
        var batchId = (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // poll until the background runner finishes (inner call hits the real
        // gateway → 400 model_not_found, still a completed request)
        JsonElement batch = default;
        for (var i = 0; i < 40; i++)
        {
            var gReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/batches/{batchId}");
            gReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            batch = await (await _client.SendAsync(gReq)).Content.ReadFromJsonAsync<JsonElement>();
            if (batch.GetProperty("status").GetString() is "completed" or "failed" or "cancelled") break;
            await Task.Delay(250);
        }
        batch.GetProperty("status").GetString().ShouldBe("completed");
        batch.GetProperty("request_counts").GetProperty("completed").GetInt32().ShouldBe(1);
        batch.GetProperty("output_file_id").GetString().ShouldNotBeNullOrEmpty();

        // the output file is a real files row with JSONL content
        var outputId = batch.GetProperty("output_file_id").GetString()!;
        var cReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/files/{outputId}/content");
        cReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        var outContent = await (await _client.SendAsync(cReq)).Content.ReadAsStringAsync();
        outContent.ShouldContain("batch_req_1");
        outContent.ShouldContain("status_code");
    }
}
