using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-037: /api/db-backups export + import.</summary>
public class DbBackupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public DbBackupTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath })));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

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

    private void Seed()
    {
        var db = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();
        db.Combos.Add(new Combo { Id = "cb1", Name = "test-combo", Models = "[\"openai/gpt-4\"]", CreatedAt = "2026-01-01", UpdatedAt = "2026-01-01" });
        db.SaveChanges(); db.Dispose();
    }

    [Fact]
    public async Task Export_returns_sqlite_file()
    {
        await LoginAsync(); Seed();
        var resp = await _client.GetAsync("/api/db-backups/export");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        // SQLite header magic
        bytes.Length.ShouldBeGreaterThan(100);
        System.Text.Encoding.ASCII.GetString(bytes[..16]).ShouldBe("SQLite format 3\0");
        resp.Content.Headers.ContentDisposition!.FileName!.ShouldContain(".db");
    }

    [Fact]
    public async Task Export_all_returns_tables_with_rows()
    {
        await LoginAsync(); Seed();
        var r = await _client.GetFromJsonAsync<JsonElement>("/api/db-backups/export-all");
        var tables = r.GetProperty("tables");
        var combos = tables.GetProperty("combos").EnumerateArray().ToList();
        combos.Count.ShouldBe(1);
        combos[0].GetProperty("Name").GetString().ShouldBe("test-combo");
    }

    [Fact]
    public async Task Import_replaces_rows_and_reports_counts()
    {
        await LoginAsync(); Seed();
        var backup = new
        {
            tables = new Dictionary<string, object>
            {
                ["combos"] = new[]
                {
                    new Dictionary<string, object?> { ["id"] = "cbX", ["name"] = "imported", ["models"] = "[]", ["createdAt"] = "2026-01-02", ["updatedAt"] = "2026-01-02", ["stickyLimit"] = 1 },
                },
            },
        };
        var resp = await _client.PostAsJsonAsync("/api/db-backups/import", backup);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var r = await resp.Content.ReadFromJsonAsync<JsonElement>();
        r.GetProperty("importedRows").GetInt32().ShouldBe(1);

        // combos table now has only the imported row
        var all = await _client.GetFromJsonAsync<JsonElement>("/api/db-backups/export-all");
        var combos = all.GetProperty("tables").GetProperty("combos").EnumerateArray().ToList();
        combos.Count.ShouldBe(1);
        combos[0].GetProperty("Name").GetString().ShouldBe("imported");
    }

    [Fact]
    public async Task Import_rejects_malformed_body()
    {
        await LoginAsync();
        var resp = await _client.PostAsJsonAsync("/api/db-backups/import", new { notTables = 1 });
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // unknown table names are skipped, not errors
        var ok = await _client.PostAsJsonAsync("/api/db-backups/import",
            new { tables = new Dictionary<string, object> { ["no_such_table_xyz"] = new[] { new { a = 1 } } } });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
