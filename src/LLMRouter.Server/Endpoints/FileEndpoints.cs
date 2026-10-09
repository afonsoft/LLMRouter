using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>
/// SPEC-040: OpenAI-compatible Files API (/v1/files*) + dashboard /api/files,
/// and /v1/batches consuming input_file_id JSONL. Files live on disk under
/// {dbDir}/files/{id}; the files table tracks metadata.
/// </summary>
public static class FileEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const long MaxUploadBytes = 50L * 1024 * 1024;

    public static string Dir(IConfiguration cfg) =>
        Path.Combine(Path.GetDirectoryName(DbBackupEndpoints.ResolveDbPath(cfg))! , "files");

    public static string PathFor(IConfiguration cfg, string id) => Path.Combine(Dir(cfg), id);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/files", ListAsync);
        api.MapPost("/files", UploadAsync);
        api.MapGet("/files/{id}", GetAsync);
        api.MapGet("/files/{id}/content", ContentAsync);
        api.MapDelete("/files/{id}", DeleteAsync);

        foreach (var prefix in new[] { "/v1", "/api/v1" })
        {
            var g = app.MapGroup(prefix);
            g.MapGet("/files", V1(ListAsync));
            g.MapPost("/files", V1(UploadAsync));
            g.MapGet("/files/{id}", V1(GetAsync));
            g.MapGet("/files/{id}/content", V1(ContentAsync));
            g.MapDelete("/files/{id}", V1(DeleteAsync));
            g.MapPost("/batches", CreateBatchAsync);
            g.MapGet("/batches/{id}", GetBatchAsync);
            g.MapPost("/batches/{id}/cancel", CancelBatchAsync);
        }
    }

    // ---- shared handler implementations (dashboard /api group) ----

    private static async Task<IResult> ListAsync(HttpContext ctx, LlmRouterDbContext db, IConfiguration cfg)
    {
        var files = await db.Files.OrderByDescending(f => f.CreatedAt).ToListAsync();
        return Results.Json(new { @object = "list", data = files.Select(Shape) }, JsonOpts);
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, LlmRouterDbContext db, IConfiguration cfg)
    {
        if (!ctx.Request.HasFormContentType)
            return Results.Json(new { error = new { message = "multipart form with a 'file' field required", type = "invalid_request" } }, JsonOpts, statusCode: 400);
        var form = await ctx.Request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null)
            return Results.Json(new { error = new { message = "file required", type = "invalid_request" } }, JsonOpts, statusCode: 400);
        if (file.Length > MaxUploadBytes)
            return Results.Json(new { error = new { message = "file exceeds 50MB limit", type = "invalid_request" } }, JsonOpts, statusCode: 413);

        var id = "file-" + Guid.NewGuid().ToString("N")[..16];
        Directory.CreateDirectory(Dir(cfg));
        await using (var fs = File.Create(PathFor(cfg, id)))
            await file.CopyToAsync(fs);

        var entry = new FileEntry
        {
            Id = id,
            Filename = Path.GetFileName(file.FileName),
            Bytes = file.Length,
            Purpose = form["purpose"].FirstOrDefault() ?? "assistants",
            Mime = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        db.Files.Add(entry);
        await db.SaveChangesAsync();
        return Results.Json(Shape(entry), JsonOpts);
    }

    private static async Task<IResult> GetAsync(HttpContext ctx, LlmRouterDbContext db, IConfiguration cfg, string id)
    {
        var e = await db.Files.FindAsync(id);
        return e is null
            ? Results.Json(new { error = new { message = "file not found", type = "invalid_request" } }, JsonOpts, statusCode: 404)
            : Results.Json(Shape(e), JsonOpts);
    }

    private static async Task<IResult> ContentAsync(HttpContext ctx, LlmRouterDbContext db, IConfiguration cfg, string id)
    {
        var e = await db.Files.FindAsync(id);
        var path = e is null ? null : PathFor(cfg, id);
        return e is null || path is null || !File.Exists(path)
            ? Results.Json(new { error = new { message = "file not found", type = "invalid_request" } }, JsonOpts, statusCode: 404)
            : Results.File(path, e.Mime, e.Filename);
    }

    private static async Task<IResult> DeleteAsync(HttpContext ctx, LlmRouterDbContext db, IConfiguration cfg, string id)
    {
        var e = await db.Files.FindAsync(id);
        if (e is null)
            return Results.Json(new { error = new { message = "file not found", type = "invalid_request" } }, JsonOpts, statusCode: 404);
        db.Files.Remove(e);
        await db.SaveChangesAsync();
        try { File.Delete(PathFor(cfg, id)); } catch { }
        return Results.Json(new { id, @object = "file", deleted = true }, JsonOpts);
    }

    // ---- /v1/batches (OpenAI shape, input_file_id JSONL) ----

    private static async Task CreateBatchAsync(HttpContext ctx, LlmRouterDbContext db,
        IConfiguration cfg, IServiceScopeFactory scopeFactory)
    {
        if (!await V1Authed(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        var b = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body);
        var inputFileId = b.TryGetProperty("input_file_id", out var f) ? f.GetString() : null;
        var endpoint = b.TryGetProperty("endpoint", out var ep) ? ep.GetString() ?? "/v1/chat/completions" : "/v1/chat/completions";
        var meta = b.TryGetProperty("metadata", out var md) ? md.Clone() : (JsonElement?)null;

        var file = inputFileId is null ? null : await db.Files.FindAsync(inputFileId);
        var path = file is null ? null : PathFor(cfg, file.Id);
        if (file is null || path is null || !File.Exists(path))
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "input_file_id missing or unreadable", type = "invalid_request" } });
            return;
        }

        var lines = File.ReadLines(path).Where(l => l.Trim().Length > 0).ToList();
        var id = "batch_" + Guid.NewGuid().ToString("N")[..16];
        var auth = ctx.Request.Headers.Authorization.ToString();
        var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";

        db.Kv.Add(new KvEntry { Scope = "v1batches", Key = id, Value = JsonSerializer.Serialize(new
        {
            id, @object = "batch", input_file_id = inputFileId, endpoint,
            completion_window = b.TryGetProperty("completion_window", out var cw) ? cw.GetString() ?? "24h" : "24h",
            status = "validating", created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            request_counts = new { total = lines.Count, completed = 0, failed = 0 },
            output_file_id = (string?)null, error_file_id = (string?)null, metadata = meta,
        })});
        await db.SaveChangesAsync();

        _ = Task.Run(async () =>
        {
            var output = new List<string>();
            var errors = new List<string>();
            int done = 0, failed = 0;
            try
            {
                await using var sc = scopeFactory.CreateAsyncScope();
                var db2 = sc.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
                var http = sc.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("batches");
                http.Timeout = TimeSpan.FromMinutes(10);
                http.BaseAddress = new Uri(baseUrl);
                await SaveBatch(db2, id, "in_progress", lines.Count, 0, 0, null, null, null, null);

                var i = 0;
                foreach (var line in lines)
                {
                    // cancellation: dashboard/ set status "cancelling"
                    var cur = await db2.Kv.FindAsync("v1batches", id);
                    if (cur is not null && cur.Value.Contains("\"cancel", StringComparison.OrdinalIgnoreCase)) break;
                    i++;
                    string? customId = null, method = "POST", url = endpoint;
                    JsonElement? reqBody = null;
                    try
                    {
                        var j = JsonDocument.Parse(line).RootElement;
                        if (j.TryGetProperty("custom_id", out var c)) customId = c.GetString();
                        if (j.TryGetProperty("method", out var m)) method = m.GetString() ?? "POST";
                        if (j.TryGetProperty("url", out var u)) url = u.GetString() ?? endpoint;
                        if (j.TryGetProperty("body", out var bb)) reqBody = bb.Clone();
                    }
                    catch { reqBody = JsonDocument.Parse(line).RootElement; }

                    try
                    {
                        var req = new HttpRequestMessage(new HttpMethod(method), url.StartsWith('/') ? url : "/" + url);
                        if (auth != "") req.Headers.TryAddWithoutValidation("Authorization", auth);
                        if (reqBody is { } rb)
                            req.Content = new StringContent(rb.GetRawText(), System.Text.Encoding.UTF8, "application/json");
                        var resp = await http.SendAsync(req);
                        var respBody = await resp.Content.ReadAsStringAsync();
                        done++;
                        output.Add(JsonSerializer.Serialize(new
                        {
                            id = $"batch_req_{i}", custom_id = customId,
                            response = new { status_code = (int)resp.StatusCode, body = TryJson(respBody) },
                        }));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        errors.Add(JsonSerializer.Serialize(new
                        {
                            id = $"batch_req_{i}", custom_id = customId,
                            response = (object?)null, error = new { message = ex.Message },
                        }));
                    }
                    await SaveBatch(db2, id, "in_progress", lines.Count, done, failed, null, null, null, null);
                }

                string? outputId = null, errorId = null;
                if (output.Count > 0)
                    outputId = await WriteOutputFile(db2, cfg, output, $"{id}_output.jsonl");
                if (errors.Count > 0)
                    errorId = await WriteOutputFile(db2, cfg, errors, $"{id}_errors.jsonl");
                var cancelled = (await db2.Kv.FindAsync("v1batches", id))?.Value.Contains("\"cancel", StringComparison.OrdinalIgnoreCase) == true;
                await SaveBatch(db2, id, cancelled ? "cancelled" : "completed", lines.Count, done, failed, outputId, errorId, null, null);
            }
            catch { /* worker died; kv row keeps last persisted state */ }
        });

        var row = await db.Kv.FindAsync("v1batches", id);
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(row!.Value);
    }

    private static async Task GetBatchAsync(HttpContext ctx, LlmRouterDbContext db, string id)
    {
        if (!await V1Authed(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        var row = await db.Kv.FindAsync("v1batches", id);
        if (row is null) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(row.Value);
    }

    private static async Task CancelBatchAsync(HttpContext ctx, LlmRouterDbContext db, string id)
    {
        if (!await V1Authed(ctx, db)) { ctx.Response.StatusCode = 401; return; }
        var row = await db.Kv.FindAsync("v1batches", id);
        if (row is null) { ctx.Response.StatusCode = 404; return; }
        var j = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Value) ?? [];
        j["status"] = JsonSerializer.SerializeToElement("cancelling");
        row.Value = JsonSerializer.Serialize(j);
        await db.SaveChangesAsync();
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(row.Value);
    }

    private static async Task SaveBatch(LlmRouterDbContext db, string id, string status,
        int total, int done, int failed, string? outputId, string? errorId,
        string? inputFileId, JsonElement? meta)
    {
        var row = await db.Kv.FindAsync("v1batches", id);
        if (row is null) return;
        var j = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row.Value) ?? [];
        j["status"] = JsonSerializer.SerializeToElement(status);
        j["request_counts"] = JsonSerializer.SerializeToElement(new { total, completed = done, failed });
        if (outputId is not null) j["output_file_id"] = JsonSerializer.SerializeToElement(outputId);
        if (errorId is not null) j["error_file_id"] = JsonSerializer.SerializeToElement(errorId);
        row.Value = JsonSerializer.Serialize(j);
        await db.SaveChangesAsync();
    }

    private static async Task<string> WriteOutputFile(LlmRouterDbContext db, IConfiguration cfg,
        List<string> lines, string name)
    {
        var id = "file-" + Guid.NewGuid().ToString("N")[..16];
        Directory.CreateDirectory(Dir(cfg));
        await File.WriteAllLinesAsync(PathFor(cfg, id), lines);
        var entry = new FileEntry
        {
            Id = id, Filename = name, Bytes = new FileInfo(PathFor(cfg, id)).Length,
            Purpose = "batch_output", Mime = "application/jsonl",
            CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        db.Files.Add(entry);
        await db.SaveChangesAsync();
        return id;
    }

    private static object TryJson(string s)
    {
        try { return JsonDocument.Parse(s).RootElement.Clone(); }
        catch { return s; }
    }

    // ---- helpers ----

    private static object Shape(FileEntry e) => new
    {
        id = e.Id, @object = "file", bytes = e.Bytes,
        created_at = DateTime.TryParse(e.CreatedAt, out var t)
            ? new DateTimeOffset(t, TimeSpan.Zero).ToUnixTimeSeconds() : 0,
        filename = e.Filename, purpose = e.Purpose,
    };

    private static Func<HttpContext, LlmRouterDbContext, IConfiguration, Task<IResult>> V1(
        Func<HttpContext, LlmRouterDbContext, IConfiguration, Task<IResult>> impl) =>
        async (ctx, db, cfg) => await V1Authed(ctx, db)
            ? await impl(ctx, db, cfg)
            : Results.Json(new { error = new { message = "Invalid or missing API key.", type = "invalid_api_key" } }, JsonOpts, statusCode: 401);

    private static Func<HttpContext, LlmRouterDbContext, IConfiguration, string, Task<IResult>> V1(
        Func<HttpContext, LlmRouterDbContext, IConfiguration, string, Task<IResult>> impl) =>
        async (ctx, db, cfg, id) => await V1Authed(ctx, db)
            ? await impl(ctx, db, cfg, id)
            : Results.Json(new { error = new { message = "Invalid or missing API key.", type = "invalid_api_key" } }, JsonOpts, statusCode: 401);

    private static async Task<bool> V1Authed(HttpContext ctx, LlmRouterDbContext db)
    {
        if (ctx.User.Identity?.IsAuthenticated == true) return true;
        var key = ctx.Request.Headers.Authorization.FirstOrDefault() is { } a && a.StartsWith("Bearer ")
            ? a[7..].Trim()
            : ctx.Request.Headers["x-api-key"].FirstOrDefault() ?? ctx.Request.Query["key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key)) return false;
        return await db.ApiKeys.AnyAsync(k => k.Key == key && k.IsActive);
    }
}
