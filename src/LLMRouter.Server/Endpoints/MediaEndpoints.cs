using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-063: /v1 media gateway surface — audio, images, videos, music, ocr,
/// segment, moderations, multimodal-embeddings, rerank, search, web fetch/map,
/// voices, speech-to-text/text-to-speech and a documented /v1/ws stub.
/// All dispatch through the shared passthrough forwarder (auth, rate limits,
/// plugins, chaos, cascade, usage logging) — media kind drives resolution.
/// </summary>
public static class MediaEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void Map(WebApplication app)
    {
        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);

            // POST media routes (kind resolved from path inside the forwarder)
            foreach (var p in new[]
            {
                "/audio/translations", "/images/edits", "/images/upscale",
                "/videos/generations", "/music/generations", "/ocr", "/segment",
                "/multimodal-embeddings", "/rerank", "/web/map",
                "/speech-to-text", "/voices",
            })
                g.MapMethods(p, ["POST"], (HttpContext c) => GatewayEndpoints.Passthrough(c));

            g.MapMethods("/text-to-speech/{voiceId}", ["POST"],
                (HttpContext c) => GatewayEndpoints.Passthrough(c, "tts"));

            // GET media routes
            g.MapGet("/videos/generations/{id}",
                (HttpContext c) => GatewayEndpoints.Passthrough(c, "video"));
            g.MapGet("/voices", (HttpContext c) => GatewayEndpoints.Passthrough(c, "tts"));

            // documented websocket stub — relay not implemented
            g.MapGet("/ws", (HttpContext ctx) =>
            {
                ctx.Response.StatusCode = 501;
                return Results.Json(new
                {
                    error = new
                    {
                        type = "not_implemented",
                        message = "WebSocket relay (/v1/ws) is not implemented in this gateway.",
                    },
                }, JsonOpts);
            });
        }

        // search usage analytics (media-kind 'search' usage rows)
        app.MapGet("/api/usage/search-analytics", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var days = int.TryParse(ctx.Request.Query["days"], out var d) ? Math.Clamp(d, 1, 365) : 7;
            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss");
            var rows = await db.UsageHistory
                .Where(r => r.Endpoint == "search" && string.Compare(r.Timestamp, cutoff) >= 0)
                .ToListAsync();
            var ok = rows.Count(r => r.Status is "200" or "ok" or "success");
            return Results.Json(new
            {
                days,
                requests = rows.Count,
                successRate = rows.Count > 0 ? Math.Round(ok * 10000.0 / rows.Count) / 100.0 : 0,
                avgLatencyMs = rows.Count > 0 ? (long)rows.Average(r => r.LatencyMs) : 0,
                byProvider = rows.GroupBy(r => r.Provider ?? "?")
                    .Select(x => new { provider = x.Key, requests = x.Count() })
                    .OrderByDescending(x => x.requests),
                byModel = rows.GroupBy(r => r.Model ?? "?")
                    .Select(x => new { model = x.Key, requests = x.Count() })
                    .OrderByDescending(x => x.requests),
                daily = rows.GroupBy(r => r.Timestamp[..10]).OrderBy(x => x.Key)
                    .Select(x => new { date = x.Key, requests = x.Count() }),
            }, JsonOpts);
        }).RequireAuthorization();
    }
}
