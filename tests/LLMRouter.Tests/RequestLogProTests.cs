using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-148: saved views + facets + tail publish + logContent modes.</summary>
public class RequestLogProTests : WebAppTestBase
{
    private async Task SeedLog(string id, string? provider, string? model, string? status,
        string? prompt = null, string? response = null)
    {
        await using var db = Db();
        db.RequestDetails.Add(new RequestDetail
        {
            Id = id, Timestamp = "2026-10-19 10:00:00", Provider = provider,
            Model = model, Status = status, Data = "{}", Prompt = prompt, Response = response,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Views_Crud()
    {
        await LoginAsync();
        var res = await Client.PostAsJsonAsync("/api/logs/views",
            new { name = "errors-5xx", filters = new { status = "5xx", model = "m1" } });
        res.EnsureSuccessStatusCode();
        var v = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = v.GetProperty("id").GetString()!;
        id.ShouldNotBeEmpty();

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/logs/views");
        list.GetProperty("views").GetArrayLength().ShouldBe(1);
        list.GetProperty("views")[0].GetProperty("name").GetString().ShouldBe("errors-5xx");
        list.GetProperty("views")[0].GetProperty("filters").GetProperty("status").GetString().ShouldBe("5xx");

        // update same id
        var res2 = await Client.PostAsJsonAsync("/api/logs/views",
            new { id, name = "renamed", filters = new { status = "4xx" } });
        res2.EnsureSuccessStatusCode();
        var list2 = await Client.GetFromJsonAsync<JsonElement>("/api/logs/views");
        list2.GetProperty("views").GetArrayLength().ShouldBe(1);
        list2.GetProperty("views")[0].GetProperty("name").GetString().ShouldBe("renamed");

        var del = await Client.DeleteAsync($"/api/logs/views/{id}");
        del.EnsureSuccessStatusCode();
        var list3 = await Client.GetFromJsonAsync<JsonElement>("/api/logs/views");
        list3.GetProperty("views").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Views_Validates()
    {
        await LoginAsync();
        var r1 = await Client.PostAsJsonAsync("/api/logs/views", new { name = "", filters = new { } });
        r1.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var r2 = await Client.PostAsJsonAsync("/api/logs/views", new { name = "x" });
        r2.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Facets_Counts()
    {
        await LoginAsync();
        await SeedLog("a1", "openai", "gpt-4o", "200");
        await SeedLog("a2", "openai", "gpt-4o", "500");
        await SeedLog("a3", "anthropic", "claude", "200");
        var f = await Client.GetFromJsonAsync<JsonElement>("/api/logs/facets");
        f.GetProperty("providers").GetProperty("openai").GetInt32().ShouldBe(2);
        f.GetProperty("providers").GetProperty("anthropic").GetInt32().ShouldBe(1);
        f.GetProperty("models").GetProperty("gpt-4o").GetInt32().ShouldBe(2);
        f.GetProperty("statuses").GetProperty("200").GetInt32().ShouldBe(2);
        // filtered facet respects query
        var f2 = await Client.GetFromJsonAsync<JsonElement>("/api/logs/facets?provider=openai");
        f2.GetProperty("providers").TryGetProperty("anthropic", out _).ShouldBeFalse();
    }

    [Fact]
    public void Tail_Publishes()
    {
        var r = new RequestDetail { Id = "t1", Timestamp = "x", Provider = "p", Model = "m", Status = "200" };
        Core.Gateway.RequestLogTail.Publish(r);
        Core.Gateway.RequestLogTail.Stream.TryRead(out var got).ShouldBeTrue();
        got!.Id.ShouldBe("t1");
    }

    [Fact]
    public async Task LogContent_Modes()
    {
        await LoginAsync();
        await SeedLog("lc1", "p", "m", "200",
            prompt: "email me at john@example.com or 4111 1111 1111 1111",
            response: "reply to jane@test.org");

        async Task SetMode(string mode)
        {
            await Client.PutAsJsonAsync("/api/settings", new { logContent = new { mode } });
        }

        // full (default)
        await SetMode("full");
        var full = await Client.GetFromJsonAsync<JsonElement>("/api/logs/lc1");
        full.GetProperty("log").GetProperty("prompt").GetString().ShouldContain("john@example.com");

        // mask
        await SetMode("mask");
        var masked = await Client.GetFromJsonAsync<JsonElement>("/api/logs/lc1");
        var mp = masked.GetProperty("log").GetProperty("prompt").GetString()!;
        mp.ShouldNotContain("john@example.com");
        mp.ShouldContain("[email]");

        // hash
        await SetMode("hash");
        var hashed = await Client.GetFromJsonAsync<JsonElement>("/api/logs/lc1");
        hashed.GetProperty("log").GetProperty("prompt").GetString()!.ShouldStartWith("sha256:");

        // drop
        await SetMode("drop");
        var dropped = await Client.GetFromJsonAsync<JsonElement>("/api/logs/lc1");
        dropped.GetProperty("log").GetProperty("prompt").ValueKind.ShouldBe(JsonValueKind.Null);

        await SetMode("full"); // restore
    }
}
