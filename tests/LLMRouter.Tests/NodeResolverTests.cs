using LLMRouter.Core.Data;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LLMRouter.Tests;

public class NodeResolverTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _conn;
    private readonly LlmRouterDbContext _db;

    public NodeResolverTests()
    {
        _conn = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _db = new LlmRouterDbContext(new DbContextOptionsBuilder<LlmRouterDbContext>()
            .UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Theory]
    [InlineData("openai-compatible-chat-abc123", "chat")]
    [InlineData("openai-compatible-responses-xyz", "responses")]
    [InlineData("anthropic-compatible-abc", null)]
    [InlineData("claude-code-abc", null)]
    [InlineData("openai", null)]
    public void ResolveOpenAICompatibleApiType_maps_prefixes(string id, string? expected)
        => NodeResolver.ResolveOpenAICompatibleApiType(id).ShouldBe(expected);

    [Theory]
    [InlineData("openai-compatible-chat-abc", true)]
    [InlineData("anthropic-compatible-abc", true)]
    [InlineData("claude-code-abc", true)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    public void IsNodeProviderId_detects_node_prefixes(string id, bool expected)
        => NodeResolver.IsNodeProviderId(id).ShouldBe(expected);

    [Fact]
    public async Task ResolveAsync_returns_synthetic_entry_for_chat_node()
    {
        _db.ProviderNodes.Add(new ProviderNode
        {
            Id = "openai-compatible-chat-a1b2c3d4e5f6",
            Type = "openai-compatible-chat",
            Name = "local llama",
            Data = """{"baseUrl":"http://localhost:8080","models":[{"id":"llama3.2"},{"id":"qwen3"}]}""",
            CreatedAt = "2026-01-01 00:00:00",
            UpdatedAt = "2026-01-01 00:00:00",
        });
        await _db.SaveChangesAsync();

        var p = await NodeResolver.ResolveAsync(_db, "openai-compatible-chat-a1b2c3d4e5f6");
        p.ShouldNotBeNull();
        p!.Id.ShouldBe("openai-compatible-chat-a1b2c3d4e5f6");
        p.Format.ShouldBe("openai");
        p.BaseUrl.ShouldBe("http://localhost:8080");
        p.Models!.Select(m => m.Id).ShouldBe(["llama3.2", "qwen3"]);
    }

    [Fact]
    public async Task ResolveAsync_responses_node_maps_to_responsesApi()
    {
        _db.ProviderNodes.Add(new ProviderNode
        {
            Id = "openai-compatible-responses-ff00aa11bb22",
            Type = "openai-compatible-responses",
            Data = """{"baseUrl":"http://x.test"}""",
            CreatedAt = "2026-01-01 00:00:00",
            UpdatedAt = "2026-01-01 00:00:00",
        });
        await _db.SaveChangesAsync();
        var p = await NodeResolver.ResolveAsync(_db, "openai-compatible-responses-ff00aa11bb22");
        p!.Format.ShouldBe("responsesApi");
    }

    [Fact]
    public async Task ResolveAsync_unknown_node_id_returns_shell_with_empty_baseUrl()
    {
        var p = await NodeResolver.ResolveAsync(_db, "openai-compatible-chat-missing");
        p.ShouldNotBeNull();
        p!.BaseUrl.ShouldBe("");
    }

    [Fact]
    public async Task ResolveAsync_non_node_id_returns_null()
        => (await NodeResolver.ResolveAsync(_db, "openai")).ShouldBeNull();

    [Fact]
    public async Task ResolveAsync_anthropic_node_maps_to_claude()
    {
        _db.ProviderNodes.Add(new ProviderNode
        {
            Id = "anthropic-compatible-111122223333",
            Type = "anthropic-compatible",
            Data = """{"baseUrl":"http://anth.local"}""",
            CreatedAt = "2026-01-01 00:00:00",
            UpdatedAt = "2026-01-01 00:00:00",
        });
        await _db.SaveChangesAsync();
        var p = await NodeResolver.ResolveAsync(_db, "anthropic-compatible-111122223333");
        p!.Format.ShouldBe("claude");
    }
}
