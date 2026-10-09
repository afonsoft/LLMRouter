using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-033: RTK filters + pluggable memory backends.</summary>
public class RtkMemoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"llmr-test-{Guid.NewGuid():N}.db");
    private readonly string _vault = Path.Combine(Path.GetTempPath(), $"llmr-vault-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { }
        try { Directory.Delete(_vault, true); } catch { }
    }

    private LlmRouterDbContext Db()
    {
        var ctx = new LlmRouterDbContext(
            new DbContextOptionsBuilder<LlmRouterDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        ctx.EnsureCreated();
        return ctx;
    }

    private static RtkFilters.Config Cfg(string[] filters,
        string[]? skip = null, string[]? preserve = null, string[]? roles = null, int max = 500) =>
        new(true, filters, max, roles ?? ["system", "user", "assistant"], skip ?? [], preserve ?? []);

    [Fact]
    public void StripComments_removes_line_and_block_comments()
    {
        var r = RtkFilters.Apply("code\n// comment\n/* block\nmore */ tail\n# hash\nkeep", Cfg(["stripComments", "dropEmptyLines"]));
        r.ShouldContain("code");
        r.ShouldContain("keep");
        r.ShouldNotContain("comment");
        r.ShouldNotContain("hash");
        r.ShouldContain("tail");
    }

    [Fact]
    public void RedactSecrets_masks_key_patterns()
    {
        var r = RtkFilters.Apply("key sk-ant-oat01-abcdefghijklmnop end", Cfg(["redactSecrets"]));
        r.ShouldBe("key [REDACTED] end");
    }

    [Fact]
    public void TruncateLines_caps_long_lines()
    {
        var r = RtkFilters.Apply(new string('x', 100), Cfg(["truncateLines"], max: 20));
        r.ShouldBe(new string('x', 20) + "…");
    }

    [Fact]
    public void SkipAndPreserve_patterns_win()
    {
        var r = RtkFilters.Apply("drop-me\nkeep-me\n// c", Cfg(["stripComments"],
            skip: ["drop"], preserve: ["keep-me"]));
        r.ShouldBe("keep-me");
    }

    [Fact]
    public void ApplyToBody_respects_compressRoles()
    {
        var body = JsonSerializer.SerializeToElement(new
        {
            messages = new[]
            {
                new { role = "user", content = "a   b\n\nc" },
                new { role = "assistant", content = "x   y" },
            },
        });
        var (outBody, saved) = RtkFilters.ApplyToBody(body, Cfg(["collapseWhitespace", "dropEmptyLines"], roles: ["user"]));
        saved.ShouldBeGreaterThan(0);
        var msgs = outBody.GetProperty("messages");
        msgs[0].GetProperty("content").GetString().ShouldBe("a b\nc");
        msgs[1].GetProperty("content").GetString().ShouldBe("x   y"); // role excluded → untouched
    }

    [Fact]
    public async Task Obsidian_backend_roundtrip()
    {
        var settings = JsonSerializer.SerializeToElement(new
        {
            memory = new { backend = "obsidian" },
            obsidian = new { vaultPath = _vault },
        });
        using var db = Db();
        var hf = new HttpClientFactoryStub();
        var added = await MemoryStore.AddAsync(db, hf, settings, "vault note", "t1");
        File.Exists(Path.Combine(_vault, $"llmrouter-{added.Id}.md")).ShouldBeTrue();
        var list = await MemoryStore.ListAsync(db, hf, settings);
        list.Count.ShouldBe(1);
        list[0].Content.ShouldBe("vault note");
        list[0].Tags.ShouldBe("t1");
        (await MemoryStore.RemoveAsync(db, hf, settings, added.Id)).ShouldBeTrue();
        (await MemoryStore.ListAsync(db, hf, settings)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Kv_backend_roundtrip()
    {
        var settings = JsonSerializer.SerializeToElement(new { memory = new { backend = "kv" } });
        using var db = Db();
        var hf = new HttpClientFactoryStub();
        var a = await MemoryStore.AddAsync(db, hf, settings, "kv note", "");
        (await MemoryStore.ListAsync(db, hf, settings)).Count.ShouldBe(1);
        (await MemoryStore.RemoveAsync(db, hf, settings, a.Id)).ShouldBeTrue();
        (await MemoryStore.ListAsync(db, hf, settings)).ShouldBeEmpty();
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
