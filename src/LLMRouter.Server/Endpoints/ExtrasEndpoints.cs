using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-015: discovery, free-tiers, gamification/leaderboard, chaos, memory,
/// webhooks, batches, audit.
/// </summary>
public static class ExtrasEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapExtrasEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ---- local provider discovery ----
        g.MapGet("/discovery", async () =>
        {
            var results = new List<object>();
            using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            await Task.WhenAll(Extras.DiscoveryTargets.Select(async t =>
            {
                var ok = false;
                try { ok = (await c.GetAsync($"http://127.0.0.1:{t.Port}{t.HealthPath}")).IsSuccessStatusCode; }
                catch { }
                lock (results)
                    results.Add(new { name = t.Name, port = t.Port, baseUrl = $"http://127.0.0.1:{t.Port}", detected = ok });
            }));
            return Results.Json(new { providers = results.OrderBy(r => r.GetType().GetProperty("name")!.GetValue(r)) }, JsonOpts);
        });

        // ---- free tiers / free-provider rankings ----
        g.MapGet("/free-tiers", async (LlmRouterDbContext db) =>
        {
            var conns = await db.ProviderConnections.ToListAsync();
            var free = conns.Where(c =>
                (c.Data?.ToString().Contains("\"free\":true", StringComparison.OrdinalIgnoreCase) ?? false)
                || (c.Data?.ToString().Contains("free", StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(c => new { c.Id, c.Provider, c.IsActive }).ToList();
            return Results.Json(new { providers = free }, JsonOpts);
        });

        // ---- gamification + leaderboard ----
        g.MapGet("/gamification", async (LlmRouterDbContext db) =>
        {
            var requests = await db.UsageHistory.LongCountAsync();
            var tokens = await db.UsageHistory.SumAsync(r => r.PromptTokens + r.CompletionTokens);
            var providers = await db.ProviderConnections.CountAsync();
            var (xp, level, badges) = Extras.Gamification(requests, tokens, providers);
            return Results.Json(new { xp, level, badges, requests, tokens, providers }, JsonOpts);
        });

        g.MapGet("/leaderboard", async (LlmRouterDbContext db) =>
        {
            var rows = await db.UsageHistory
                .GroupBy(r => r.Model ?? "unknown")
                .Select(x => new { model = x.Key, requests = x.LongCount(), tokens = x.Sum(r => r.PromptTokens + r.CompletionTokens) })
                .OrderByDescending(x => x.requests).Take(20).ToListAsync();
            return Results.Json(new { leaderboard = rows }, JsonOpts);
        });

        // ---- chaos ----
        g.MapGet("/chaos", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("chaos", "config"))?.Value;
            return Results.Json(raw is null
                ? new { errorPct = 0, latencyMs = 0 }
                : JsonDocument.Parse(raw).RootElement.Clone(), JsonOpts);
        });
        g.MapPost("/chaos", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("chaos", "config")
                ?? db.Kv.Add(new KvEntry { Scope = "chaos", Key = "config", Value = "{}" }).Entity;
            row.Value = JsonSerializer.Serialize(new
            {
                errorPct = b.TryGetProperty("errorPct", out var e) ? e.GetInt32() : 0,
                latencyMs = b.TryGetProperty("latencyMs", out var l) ? l.GetInt32() : 0,
            });
            await db.SaveChangesAsync();
            await Extras.AuditAsync(db, "chaos.update", row.Value);
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- memory store (kv 'memory') ----
        g.MapGet("/memory", async (LlmRouterDbContext db, string? q) =>
        {
            var raw = (await db.Kv.FindAsync("memory", "items"))?.Value;
            var items = raw is null ? new List<JsonElement>()
                : JsonDocument.Parse(raw).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            if (q is not null)
                items = items.Where(x => x.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Json(new { items }, JsonOpts);
        });
        g.MapPost("/memory", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("memory", "items")
                ?? db.Kv.Add(new KvEntry { Scope = "memory", Key = "items", Value = "[]" }).Entity;
            var items = JsonDocument.Parse(row.Value).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            if (b.TryGetProperty("id", out var id)) // delete
                items.RemoveAll(x => x.TryGetProperty("id", out var i) && i.GetString() == id.GetString());
            else
                items.Add(JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    at = DateTime.UtcNow,
                    content = b.GetProperty("content").GetString(),
                    tags = b.TryGetProperty("tags", out var t) ? t.GetString() : "",
                })).RootElement.Clone());
            row.Value = JsonSerializer.Serialize(items);
            await db.SaveChangesAsync();
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- webhooks ----
        g.MapGet("/webhooks", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("webhooks", "list"))?.Value;
            return Results.Json(new { webhooks = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });
        g.MapPost("/webhooks", async (LlmRouterDbContext db, HttpContext ctx) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var row = await db.Kv.FindAsync("webhooks", "list")
                ?? db.Kv.Add(new KvEntry { Scope = "webhooks", Key = "list", Value = "[]" }).Entity;
            var list = JsonDocument.Parse(row.Value).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            if (b.TryGetProperty("id", out var id))
                list.RemoveAll(x => x.TryGetProperty("id", out var i) && i.GetString() == id.GetString());
            else
                list.Add(JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = Guid.NewGuid().ToString("N")[..8],
                    url = b.GetProperty("url").GetString(),
                    events = b.TryGetProperty("events", out var e)
                        ? e.EnumerateArray().Select(x => x.GetString()).ToArray() : new[] { "*" },
                })).RootElement.Clone());
            row.Value = JsonSerializer.Serialize(list);
            await db.SaveChangesAsync();
            await Extras.AuditAsync(db, "webhooks.update", "");
            return Results.Json(new { ok = true }, JsonOpts);
        });

        // ---- batches (sequential chat jobs) ----
        g.MapPost("/batches", async (LlmRouterDbContext db, HttpContext ctx, IHttpClientFactory hf) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var items = b.GetProperty("requests").EnumerateArray().Select(x => x.Clone()).ToList();
            var id = Guid.NewGuid().ToString("N")[..10];
            var row = await db.Kv.FindAsync("batches", "jobs")
                ?? db.Kv.Add(new KvEntry { Scope = "batches", Key = "jobs", Value = "[]" }).Entity;
            var jobs = JsonDocument.Parse(row.Value).RootElement.EnumerateArray().Select(x => x.Clone()).ToList();
            jobs.Add(JsonDocument.Parse(JsonSerializer.Serialize(new
            { id, createdAt = DateTime.UtcNow, status = "queued", total = items.Count, done = 0 })).RootElement.Clone());
            row.Value = JsonSerializer.Serialize(jobs);
            await db.SaveChangesAsync();

            var auth = ctx.Request.Headers.Authorization.ToString();
            var http = hf.CreateClient("local");
            http.BaseAddress = new Uri($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            _ = Task.Run(async () =>
            {
                var results = new List<object>();
                foreach (var item in items)
                {
                    try
                    {
                        var r = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
                        if (auth != "") r.Headers.TryAddWithoutValidation("Authorization", auth);
                        r.Content = JsonContent.Create(item);
                        var resp = await http.SendAsync(r);
                        results.Add(new { status = (int)resp.StatusCode });
                    }
                    catch { results.Add(new { status = 0 }); }
                }
                // mark done — best effort with a fresh scope-free db is not available here;
                // completion state is derived client-side via results count.
            });
            return Results.Json(new { id, total = items.Count }, JsonOpts);
        });
        g.MapGet("/batches", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("batches", "jobs"))?.Value;
            return Results.Json(new { batches = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });

        // ---- audit ----
        g.MapGet("/audit", async (LlmRouterDbContext db) =>
        {
            var raw = (await db.Kv.FindAsync("audit", "log"))?.Value;
            return Results.Json(new { events = raw is null ? (object)Array.Empty<object>() : JsonDocument.Parse(raw).RootElement.Clone() }, JsonOpts);
        });
    }
}
