using System.Text.Json;
using LLMRouter.Core.Bots;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using LLMRouter.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-065: bots & integrações — copilot chat, issue-agent, telegram webhook, vnc-session, cursor-cli, dahl tokens.</summary>
public static class BotsEndpoints
{
    /// <summary>Mapeia rotas de bots.</summary>
    public static void Map(WebApplication app)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        app.MapPost("/api/copilot/chat", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var msg = b.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(msg)) return Results.BadRequest(new { error = "message" });
            return Results.Json(await BotCore.CopilotAsync(db, msg), json);
        });

        // issue-agent
        app.MapGet("/api/issue-agent/runs", async (LlmRouterDbContext db, int limit = 50) =>
            Results.Json(new { runs = await db.IssueAgentRuns.OrderByDescending(r => r.Id).Take(Math.Clamp(limit, 1, 200))
                .Select(r => new { r.Id, r.Source, r.Issue, r.State, r.PrUrl, r.At }).ToListAsync() }, json));
        app.MapPost("/api/issue-agent/runs", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var src = b.TryGetProperty("source", out var s) ? s.GetString() ?? "manual" : "manual";
            var issue = b.TryGetProperty("issue", out var i) ? i.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(issue)) return Results.BadRequest(new { error = "issue" });
            var run = new IssueAgentRun { Source = src, Issue = issue, State = "queued", Detail = "aceito — fluxo Devin-style pendente", At = DateTime.UtcNow.ToString("O") };
            db.IssueAgentRuns.Add(run);
            await Extras.AuditAsync(db, "issueAgent.run", issue);
            await db.SaveChangesAsync();
            return Results.Json(new { run.Id, run.State }, json);
        });

        // telegram webhook → sendMessage
        app.MapPost("/api/telegram/update", async (LlmRouterDbContext db, IHttpClientFactory hf, HttpRequest req, CancellationToken ct) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            if (!b.TryGetProperty("message", out var msg)) return Results.Json(new { ok = true }, json);
            var chatId = msg.TryGetProperty("chat", out var c) && c.TryGetProperty("id", out var cid) ? cid.GetRawText() : null;
            var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var reply = await BotCore.ReplyAsync(db, text);
            var token = "";
            var st = await db.Settings.FindAsync(1);
            try
            {
                if (st?.Data?.StartsWith('{') == true) { using var d = JsonDocument.Parse(st.Data); if (d.RootElement.TryGetProperty("telegram", out var tg) && tg.TryGetProperty("botToken", out var tk)) token = tk.GetString() ?? ""; }
            }
            catch { /* settings blob malformado — sem reply externo */ }
            var sent = false;
            if (chatId is not null && !string.IsNullOrEmpty(token))
            {
                try
                {
                    var r = await hf.CreateClient("gami").PostAsync($"https://api.telegram.org/bot{token}/sendMessage",
                        JsonContent.Create(new { chat_id = long.Parse(chatId), text = reply }), ct);
                    sent = r.IsSuccessStatusCode;
                }
                catch { }
            }
            return Results.Json(new { ok = true, reply, sent }, json);
        });

        // vnc-session (ServiceManager estático, catálogo "novnc")
        app.MapGet("/api/vnc-session/status", () => Results.Json(new { id = "novnc", known = ServiceManager.Find("novnc") is not null }, json));
        app.MapPost("/api/vnc-session/start", async (LlmRouterDbContext db) =>
        { var r = await ServiceManager.StartAsync("novnc"); await Extras.AuditAsync(db, "vnc.start", ""); return Results.Json(new { r.ok, r.detail }, json); });
        app.MapPost("/api/vnc-session/stop", async (LlmRouterDbContext db) =>
        { var r = await ServiceManager.StopAsync("novnc"); await Extras.AuditAsync(db, "vnc.stop", ""); return Results.Json(new { r.ok, r.detail }, json); });

        // cursor-cli passthrough
        app.Map("/api/cursor-cli/{**path}", async (HttpContext ctx, LlmRouterDbContext db, IHttpClientFactory hf, string? path) =>
        {
            var baseUrl = "";
            var st = await db.Settings.FindAsync(1);
            try
            {
                if (st?.Data?.StartsWith('{') == true) { using var d = JsonDocument.Parse(st.Data); if (d.RootElement.TryGetProperty("cursorCli", out var cc) && cc.TryGetProperty("baseUrl", out var u)) baseUrl = u.GetString() ?? ""; }
            }
            catch { /* settings blob malformado */ }
            if (string.IsNullOrEmpty(baseUrl)) return Results.BadRequest(new { error = "settings.data.cursorCli.baseUrl ausente" });
            var client = hf.CreateClient("gami");
            var target = $"{baseUrl.TrimEnd('/')}/{path}{ctx.Request.QueryString}";
            var outbound = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);
            if (ctx.Request.ContentLength > 0) { outbound.Content = new StreamContent(ctx.Request.Body); if (ctx.Request.ContentType is not null) outbound.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(ctx.Request.ContentType); }
            var res = await client.SendAsync(outbound, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)res.StatusCode;
            ctx.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? "application/json";
            await ctx.Response.Body.WriteAsync(await res.Content.ReadAsByteArrayAsync(ctx.RequestAborted), ctx.RequestAborted);
            return Results.Empty;
        });

        // dahl tokens
        app.MapPost("/api/dahl/tokens", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var subject = "dahl";
            try { var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body); if (b.TryGetProperty("subject", out var s)) subject = s.GetString() ?? subject; } catch { }
            return Results.Json(new { token = await BotCore.DahlIssueAsync(db, subject) }, json);
        });
        app.MapPost("/api/dahl/tokens/verify", async (LlmRouterDbContext db, HttpRequest req) =>
        {
            var b = await JsonSerializer.DeserializeAsync<JsonElement>(req.Body);
            var token = b.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            return Results.Json(new { valid = await BotCore.DahlVerifyAsync(db, token) }, json);
        });
    }
}
