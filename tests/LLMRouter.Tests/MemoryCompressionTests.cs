using System.Text.Json;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Gateway;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-026: memory scoring, context compression, skills injection.</summary>
public class MemoryCompressionTests
{
    private static JsonElement El(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void MemorySearch_ranks_by_term_frequency()
    {
        var items = El(new[]
        {
            new { id = "a", content = "deploy notes about kubernetes", at = DateTime.UtcNow, tags = "" },
            new { id = "b", content = "kubernetes kubernetes kubernetes runbook", at = DateTime.UtcNow, tags = "" },
            new { id = "c", content = "unrelated gardening journal", at = DateTime.UtcNow, tags = "" },
        });
        var hits = MemorySearch.Search(items, "kubernetes");
        hits.Count.ShouldBe(2);
        hits[0].GetProperty("id").GetString().ShouldBe("b");
        hits[0].TryGetProperty("score", out var sc).ShouldBeTrue();
        sc.GetDouble().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void MemorySearch_recency_breaks_ties()
    {
        var items = El(new[]
        {
            new { id = "old", content = "same note text", at = DateTime.UtcNow.AddDays(-120), tags = "" },
            new { id = "new", content = "same note text", at = DateTime.UtcNow, tags = "" },
        });
        var hits = MemorySearch.Search(items, "note text");
        hits[0].GetProperty("id").GetString().ShouldBe("new");
    }

    [Fact]
    public void MemorySearch_no_query_returns_all()
    {
        var items = El(new[] { new { id = "a", content = "x" }, new { id = "b", content = "y" } });
        MemorySearch.Search(items, null).Count.ShouldBe(2);
    }

    [Fact]
    public void Compress_drops_middle_keeps_system_and_tail()
    {
        var big = new string('x', 40000);
        var msgs = new List<object> { new { role = "system", content = "you are helpful" } };
        for (var i = 0; i < 20; i++)
            msgs.Add(new { role = i % 2 == 0 ? "user" : "assistant", content = $"msg-{i} {big}" });
        msgs.Add(new { role = "user", content = "final question" });

        var r = ContextCompressor.Apply(El(new { model = "m", messages = msgs }), maxTokens: 4000);
        r.Compressed.ShouldBeTrue();
        r.Dropped.ShouldBeGreaterThan(0);

        var arr = r.Body.GetProperty("messages").EnumerateArray().ToList();
        arr[0].GetProperty("role").GetString().ShouldBe("system");
        arr[0].GetProperty("content").GetString().ShouldBe("you are helpful");
        arr[1].GetProperty("content").GetString()!.ShouldContain("context compressed");
        arr[^1].GetProperty("content").GetString()!.ShouldContain("final question");
    }

    [Fact]
    public void Compress_below_budget_is_noop()
    {
        var r = ContextCompressor.Apply(El(new
        {
            messages = new[] { new { role = "user", content = "hi" } },
        }), maxTokens: 100000);
        r.Compressed.ShouldBeFalse();
    }

    [Fact]
    public void InjectSystem_appends_to_existing_system_message()
    {
        var body = El(new
        {
            messages = new object[]
            {
                new { role = "system", content = "base" },
                new { role = "user", content = "hi" },
            },
        });
        var outb = ContextCompressor.InjectSystem(body, "Active skills:\n- coding");
        var sys = outb.GetProperty("messages")[0];
        sys.GetProperty("content").GetString().ShouldBe("base\n\nActive skills:\n- coding");
    }

    [Fact]
    public void InjectSystem_prepends_when_no_system()
    {
        var body = El(new { messages = new[] { new { role = "user", content = "hi" } } });
        var outb = ContextCompressor.InjectSystem(body, "skills!");
        var msgs = outb.GetProperty("messages").EnumerateArray().ToList();
        msgs[0].GetProperty("role").GetString().ShouldBe("system");
        msgs[0].GetProperty("content").GetString().ShouldBe("skills!");
    }
}
