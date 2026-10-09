using System.Text.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Compression;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-034: context-compression APIs — engines catalog, preview/compare/retrieve, settings, combos, analytics, caveman config.</summary>
public static class CompressionEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<JsonObject> SettingsNode(LlmRouterDbContext db)
    {
        var s = await db.Settings.FirstOrDefaultAsync();
        var d = AuthEndpoints.Parse(s?.Data);
        return d.ValueKind == JsonValueKind.Object ? (JsonNode.Parse(d.GetRawText()) as JsonObject) ?? [] : [];
    }

    private static async Task SaveCompression(LlmRouterDbContext db, JsonObject compression)
    {
        var s = await db.Settings.FirstOrDefaultAsync()
            ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var d = AuthEndpoints.Parse(s.Data).Deserialize<Dictionary<string, JsonElement>>() ?? [];
        d["compression"] = JsonSerializer.SerializeToElement(compression);
        s.Data = JsonSerializer.Serialize(d);
        await db.SaveChangesAsync();
        await Core.Extras.Extras.AuditAsync(db, "settings.compression", "update");
    }

    private static JsonObject CompressionOf(JsonObject data) =>
        data["compression"] as JsonObject ?? [];

    private static object ComboDto(CompressionCombo c) => new
    {
        c.Id, c.Name, c.Description,
        pipeline = AuthEndpoints.Parse(c.Pipeline),
        languagePacks = AuthEndpoints.Parse(c.LanguagePacks),
        c.OutputMode, c.OutputModeIntensity, c.IsDefault, c.CreatedAt, c.UpdatedAt,
    };

    private static JsonObject? BodyToObject(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(body.GetRawText()) as JsonObject
            : new JsonObject
            {
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = body.ValueKind == JsonValueKind.String ? body.GetString() : body.GetRawText(),
                }),
            };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // ── catalog ────────────────────────────────────────────────────────
        g.MapGet("/compression/engines", () => Results.Json(new
        {
            engines = CompressionRegistry.Engines.Select(e => new
            {
                e.Id, e.Name, e.Description, e.Icon, e.Stackable, e.StackPriority,
                stable = e.Stable,
                configSchema = e.ConfigSchema.Select(f => new
                {
                    f.Key, f.Type, f.Label, f.Options, f.Min, f.Max, f.Description,
                    defaultValue = f.DefaultValue,
                }),
            }),
        }, JsonOpts));

        g.MapGet("/compression/rules", () => Results.Json(new { rules = CavemanRules.Metadata() }, JsonOpts));

        g.MapGet("/compression/language-packs", () => Results.Json(new
        {
            languages = new[] { "en" },
            packs = new[] { new { id = "en", name = "English", rules = CavemanRules.Rules.Length } },
        }, JsonOpts));

        // ── preview / compare / retrieve ───────────────────────────────────
        g.MapPost("/compression/preview", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var body = BodyToObject(req.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? JsonSerializer.SerializeToElement(new { messages = new[] { new { role = "user", content = t.GetString() } } })
                : req);
            if (body is null) return Results.BadRequest(new { error = "messages or text required" });

            var steps = new List<PipelineStep>();
            if (req.ValueKind == JsonValueKind.Object && req.TryGetProperty("engineId", out var eid)
                && eid.ValueKind == JsonValueKind.String && CompressionRegistry.Get(eid.GetString()!) is { } single)
                steps.Add(new PipelineStep(single.Id,
                    req.TryGetProperty("intensity", out var i) ? i.GetString() : null,
                    req.TryGetProperty("config", out var c) && c.ValueKind == JsonValueKind.Object
                        ? JsonNode.Parse(c.GetRawText()) as JsonObject : null));
            else if (req.ValueKind == JsonValueKind.Object && req.TryGetProperty("pipeline", out var pl) && pl.ValueKind == JsonValueKind.Array)
                steps = CompressionPipeline.ParseSteps(JsonNode.Parse(pl.GetRawText()));
            else
            {
                var mode = req.ValueKind == JsonValueKind.Object && req.TryGetProperty("mode", out var m)
                    ? m.GetString() ?? "stacked" : "stacked";
                var data = await SettingsNode(db);
                steps = CompressionPipeline.ResolvePlan(CompressionOf(data), null, null);
                if (steps.Count == 0)
                    steps = CompressionRegistry.ModeToEngines.GetValueOrDefault(mode, [])
                        .Select(e => new PipelineStep(e, null, null)).ToList();
            }

            var engineConfigs = CompressionOf(await SettingsNode(db))["engineConfigs"] as JsonObject;
            var (outBody, runs) = CompressionPipeline.Run(body, steps, new EngineOptions(
                Model: req.ValueKind == JsonValueKind.Object && req.TryGetProperty("model", out var rm) ? rm.GetString() : null,
                SupportsVision: req.ValueKind == JsonValueKind.Object && req.TryGetProperty("supportsVision", out var sv) && sv.ValueKind is JsonValueKind.True or JsonValueKind.False ? sv.GetBoolean() : null,
                PrincipalId: req.ValueKind == JsonValueKind.Object && req.TryGetProperty("principalId", out var pid) ? pid.GetString() : null), engineConfigs);
            var before = TextOps.BodyTextChars(body);
            var after = TextOps.BodyTextChars(outBody);
            return Results.Json(new
            {
                body = outBody,
                compressed = after < before,
                stats = new
                {
                    originalTokens = (int)Math.Ceiling(before / 4.0),
                    compressedTokens = (int)Math.Ceiling(after / 4.0),
                    savingsPercent = before > 0 ? Math.Round((before - after) * 10000.0 / before) / 100.0 : 0,
                    techniquesUsed = runs.SelectMany(r => r.Techniques).Distinct().ToArray(),
                },
                engineBreakdown = runs.Select(r => new
                {
                    engine = r.Engine, r.BeforeChars, r.AfterChars, r.SavedChars, r.SavingsPercent, r.Skipped,
                }),
            }, JsonOpts);
        });

        g.MapPost("/compression/compare", async (HttpContext ctx) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var body = BodyToObject(req);
            if (body is null) return Results.BadRequest(new { error = "messages or text required" });
            var ids = req.ValueKind == JsonValueKind.Object && req.TryGetProperty("engineIds", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToArray()
                : CompressionRegistry.Engines.Where(en => en.Stable).Select(en => en.Id).ToArray();
            var before = TextOps.BodyTextChars(body);
            var rows = ids
                .Select(id => CompressionRegistry.Get(id))
                .Where(en => en is not null)
                .Select(en =>
                {
                    var r = en!.Apply((JsonObject)body.DeepClone(), new EngineOptions());
                    var after = TextOps.BodyTextChars(r.Body);
                    return (object)new
                    {
                        engineId = en.Id, name = en.Name, stable = en.Stable,
                        originalTokens = (int)Math.Ceiling(before / 4.0),
                        compressedTokens = (int)Math.Ceiling(after / 4.0),
                        savingsPercent = before > 0 ? Math.Round((before - after) * 10000.0 / before) / 100.0 : 0,
                        skipped = r.Stats?.Skipped,
                    };
                }).ToArray();
            return Results.Json(new { rows }, JsonOpts);
        });

        g.MapPost("/compression/retrieve", async (HttpContext ctx) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
            var hash = req.TryGetProperty("hash", out var h) ? h.GetString() : null;
            if (hash is null or { Length: < 6 or > 64 }) return Results.BadRequest(new { error = "hash required (6-64 chars)" });
            var block = CcrStore.Get(hash, req.TryGetProperty("principalId", out var p) ? p.GetString() : null);
            if (block is null) return Results.Json(new { found = false }, JsonOpts);
            var mode = req.TryGetProperty("mode", out var m) ? m.GetString() ?? "full" : "full";
            var n = req.TryGetProperty("n", out var nn) && nn.TryGetInt32(out var ni) ? ni : 20;
            object? result = mode switch
            {
                "head" => block[..Math.Min(n * 4 * 20, block.Length)],
                "tail" => block[Math.Max(0, block.Length - n * 4 * 20)..],
                "lines" => string.Join('\n', block.Split('\n').Skip(Math.Max(0, (req.TryGetProperty("start", out var st) ? st.GetInt32() : 1) - 1)).Take(n)),
                "grep" when req.TryGetProperty("pattern", out var pt) && pt.GetString() is { } pat
                    => string.Join('\n', block.Split('\n').Where(l => l.Contains(pat, StringComparison.OrdinalIgnoreCase)).Take(n)),
                "stats" => new { chars = block.Length, lines = block.Split('\n').Length, tokens = TextOps.EstimateTokens(block) },
                _ => block,
            };
            CcrStore.RecordRetrieval(hash, req.TryGetProperty("principalId", out var p2) ? p2.GetString() ?? "__anon__" : "__anon__");
            return Results.Json(new { found = true, block = result }, JsonOpts);
        });

        g.MapPost("/compression/ccr/clear", (HttpContext ctx) =>
        {
            var principal = ctx.Request.Query["principalId"].ToString();
            CcrStore.Clear(string.IsNullOrEmpty(principal) ? null : principal);
            return Results.Json(new { success = true }, JsonOpts);
        });

        // ── settings ───────────────────────────────────────────────────────
        g.MapGet("/settings/compression", async (LlmRouterDbContext db) =>
            Results.Json(new { compression = CompressionOf(await SettingsNode(db)) }, JsonOpts));

        g.MapPut("/settings/compression", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var data = await SettingsNode(db);
            var merged = CompressionOf(data).DeepClone() as JsonObject ?? [];
            var patch = req["compression"] as JsonObject ?? req;
            foreach (var kv in patch) merged[kv.Key] = kv.Value?.DeepClone();
            await SaveCompression(db, merged);
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/settings/compression/mcp-accessibility", async (LlmRouterDbContext db) =>
            Results.Json(CompressionOf(await SettingsNode(db))["mcpAccessibility"] ?? new JsonObject { ["enabled"] = false }, JsonOpts));

        g.MapPut("/settings/compression/mcp-accessibility", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var data = await SettingsNode(db);
            var merged = CompressionOf(data).DeepClone() as JsonObject ?? [];
            merged["mcpAccessibility"] = req.DeepClone();
            await SaveCompression(db, merged);
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/settings/compression/run-telemetry", async (LlmRouterDbContext db) =>
        {
            var runs = await db.CompressionRuns.OrderByDescending(r => r.Timestamp).Take(2000).ToListAsync();
            var totalBefore = runs.Sum(r => r.BeforeChars);
            var totalAfter = runs.Sum(r => r.AfterChars);
            return Results.Json(new
            {
                totalRuns = runs.Count,
                totalBeforeChars = totalBefore,
                totalAfterChars = totalAfter,
                totalSavedChars = Math.Max(0, totalBefore - totalAfter),
                savingsPercent = totalBefore > 0 ? Math.Round((totalBefore - totalAfter) * 10000.0 / totalBefore) / 100.0 : 0,
                byEngine = runs.GroupBy(r => r.EngineId).Select(gr => new
                {
                    engine = gr.Key, runs = gr.Count(),
                    saved = Math.Max(0, gr.Sum(r => r.BeforeChars - r.AfterChars)),
                }),
            }, JsonOpts);
        });

        // ── context combos ─────────────────────────────────────────────────
        g.MapGet("/context/combos", async (LlmRouterDbContext db) =>
            Results.Json(new { combos = (await db.CompressionCombos.OrderBy(c => c.Name).ToListAsync()).Select(ComboDto) }, JsonOpts));

        g.MapPost("/context/combos", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var name = req?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || name!.Length > 120)
                return Results.BadRequest(new { error = "name required (<=120 chars)" });
            var now = DateTime.UtcNow.ToString("o");
            var combo = new CompressionCombo
            {
                Id = req["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("n")[..12],
                Name = name, Description = req["description"]?.GetValue<string>(),
                Pipeline = req["pipeline"]?.ToJsonString() ?? "[]",
                LanguagePacks = req["languagePacks"]?.ToJsonString() ?? "[]",
                OutputMode = req["outputMode"]?.GetValue<bool>() ?? false,
                OutputModeIntensity = req["outputModeIntensity"]?.GetValue<string>(),
                IsDefault = req["isDefault"]?.GetValue<bool>() ?? false,
                CreatedAt = now, UpdatedAt = now,
            };
            db.CompressionCombos.Add(combo);
            await db.SaveChangesAsync();
            return Results.Json(ComboDto(combo), JsonOpts, statusCode: 201);
        });

        g.MapGet("/context/combos/{id}", async (string id, LlmRouterDbContext db) =>
            await db.CompressionCombos.FindAsync(id) is { } c
                ? Results.Json(ComboDto(c), JsonOpts)
                : Results.NotFound(new { error = "Compression combo not found" }));

        g.MapPut("/context/combos/{id}", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            var c = await db.CompressionCombos.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "Compression combo not found" });
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            if (req is null) return Results.BadRequest(new { error = "Invalid JSON body" });
            var pipeline = req["pipeline"];
            if (pipeline is JsonArray steps
                && CompressionPipeline.ParseSteps(steps).Count != steps.Count)
                return Results.BadRequest(new { error = "pipeline contains unknown engine" });
            if (req["name"]?.GetValue<string>() is { } n) c.Name = n;
            if (req.ContainsKey("description")) c.Description = req["description"]?.GetValue<string>();
            if (pipeline is not null) c.Pipeline = pipeline.ToJsonString();
            if (req["languagePacks"] is not null) c.LanguagePacks = req["languagePacks"]!.ToJsonString();
            if (req["outputMode"] is not null) c.OutputMode = req["outputMode"]!.GetValue<bool>();
            if (req.ContainsKey("outputModeIntensity")) c.OutputModeIntensity = req["outputModeIntensity"]?.GetValue<string>();
            if (req["isDefault"] is not null) c.IsDefault = req["isDefault"]!.GetValue<bool>();
            c.UpdatedAt = DateTime.UtcNow.ToString("o");
            await db.SaveChangesAsync();
            return Results.Json(ComboDto(c), JsonOpts);
        });

        g.MapDelete("/context/combos/{id}", async (string id, LlmRouterDbContext db) =>
        {
            var c = await db.CompressionCombos.FindAsync(id);
            if (c is null) return Results.NotFound(new { error = "Compression combo not found" });
            db.CompressionCombos.Remove(c);
            db.CompressionComboAssignments.RemoveRange(db.CompressionComboAssignments.Where(a => a.CompressionComboId == id));
            await db.SaveChangesAsync();
            return Results.Json(new { success = true }, JsonOpts);
        });

        g.MapGet("/context/combos/{id}/assignments", async (string id, LlmRouterDbContext db) =>
        {
            if (await db.CompressionCombos.FindAsync(id) is null)
                return Results.NotFound(new { error = "Compression combo not found" });
            var list = await db.CompressionComboAssignments.Where(a => a.CompressionComboId == id).ToListAsync();
            return Results.Json(new { assignments = list.Select(a => new { a.Id, compressionComboId = a.CompressionComboId, routingComboId = a.RoutingComboId, a.CreatedAt }) }, JsonOpts);
        });

        g.MapPut("/context/combos/{id}/assignments", async (string id, HttpContext ctx, LlmRouterDbContext db) =>
        {
            if (await db.CompressionCombos.FindAsync(id) is null)
                return Results.NotFound(new { error = "Compression combo not found" });
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var ids = req?["routingComboIds"] as JsonArray;
            if (ids is null) return Results.BadRequest(new { error = "routingComboIds required" });
            db.CompressionComboAssignments.RemoveRange(db.CompressionComboAssignments.Where(a => a.CompressionComboId == id));
            var now = DateTime.UtcNow.ToString("o");
            foreach (var rid in ids.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                db.CompressionComboAssignments.Add(new CompressionComboAssignment
                { Id = Guid.NewGuid().ToString("n")[..12], CompressionComboId = id, RoutingComboId = rid!, CreatedAt = now });
            await db.SaveChangesAsync();
            var list = await db.CompressionComboAssignments.Where(a => a.CompressionComboId == id).ToListAsync();
            return Results.Json(new { assignments = list.Select(a => new { a.Id, compressionComboId = a.CompressionComboId, routingComboId = a.RoutingComboId, a.CreatedAt }) }, JsonOpts);
        });

        // ── analytics ──────────────────────────────────────────────────────
        g.MapGet("/context/analytics", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var since = ctx.Request.Query["since"].ToString();
            var days = int.TryParse(System.Text.RegularExpressions.Regex.Match(since, @"^(\d+)d$").Groups[1].Value, out var d) ? d : 7;
            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");
            var runs = await db.CompressionRuns.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToListAsync();
            var before = runs.Sum(r => r.BeforeChars);
            var after = runs.Sum(r => r.AfterChars);
            return Results.Json(new
            {
                days,
                runs = runs.Count,
                tokensSaved = (int)Math.Ceiling(Math.Max(0, before - after) / 4.0),
                avgSavingsPercent = runs.Count > 0 ? Math.Round(runs.Average(r => r.BeforeChars > 0 ? Math.Max(0, r.BeforeChars - r.AfterChars) * 100.0 / r.BeforeChars : 0), 2) : 0,
                byEngine = runs.GroupBy(r => r.EngineId).Select(gr => new { engine = gr.Key, runs = gr.Count(), savedChars = Math.Max(0, gr.Sum(r => r.BeforeChars - r.AfterChars)) }),
            }, JsonOpts);
        });

        g.MapGet("/context/analytics/engine", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var engineId = ctx.Request.Query["engineId"].ToString();
            if (string.IsNullOrEmpty(engineId))
                return Results.BadRequest(new { error = "engineId query parameter is required" });
            var days = int.TryParse(ctx.Request.Query["days"], out var d) ? Math.Max(1, d) : 7;
            var cutoff = DateTime.UtcNow.AddDays(-days).ToString("o");
            var runs = await db.CompressionRuns
                .Where(r => r.EngineId == engineId)
                .ToListAsync();
            var inWindow = runs.Where(r => string.Compare(r.Timestamp, cutoff) >= 0).ToList();
            var before = inWindow.Sum(r => r.BeforeChars);
            var after = inWindow.Sum(r => r.AfterChars);
            return Results.Json(new
            {
                engineId,
                days,
                runs = inWindow.Count,
                tokensSaved = (int)Math.Ceiling(Math.Max(0, before - after) / 4.0),
                avgSavingsPercent = inWindow.Count > 0 ? Math.Round(inWindow.Average(r => r.BeforeChars > 0 ? Math.Max(0, r.BeforeChars - r.AfterChars) * 100.0 / r.BeforeChars : 0), 2) : 0,
            }, JsonOpts);
        });

        // ── caveman config (settings.compression.engineConfigs.caveman) ────
        g.MapGet("/context/caveman/config", async (LlmRouterDbContext db) =>
        {
            var comp = CompressionOf(await SettingsNode(db));
            var ec = comp["engineConfigs"] as JsonObject;
            return Results.Json(new
            {
                config = ec?["caveman"] ?? new JsonObject { ["enabled"] = true, ["intensity"] = "full" },
                languagePacks = comp["languagePacks"] ?? new JsonArray("en"),
            }, JsonOpts);
        });

        g.MapPut("/context/caveman/config", async (HttpContext ctx, LlmRouterDbContext db) =>
        {
            var req = await JsonSerializer.DeserializeAsync<JsonObject>(ctx.Request.Body);
            var data = await SettingsNode(db);
            var merged = CompressionOf(data).DeepClone() as JsonObject ?? [];
            var ec = merged["engineConfigs"] as JsonObject ?? new JsonObject();
            ec["caveman"] = (req?["config"] as JsonObject ?? req)?.DeepClone();
            merged["engineConfigs"] = ec;
            await SaveCompression(db, merged);
            return Results.Json(new { success = true }, JsonOpts);
        });
    }
}
