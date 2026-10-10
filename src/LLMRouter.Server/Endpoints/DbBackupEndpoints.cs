using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Endpoints;

/// <summary>SPEC-037: full DB export/import — sqlite file + portable JSON.</summary>
public static class DbBackupEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static string ResolveDbPath(IConfiguration cfg) =>
        cfg["Db:Path"]
        ?? Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LLMRouter", "llmrouter.db");

    private static readonly HashSet<string> SkipImportTables = new(StringComparer.OrdinalIgnoreCase)
        { "__EFMigrationsHistory", "sqlite_sequence" };

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/db-backups/export", async (IConfiguration cfg) =>
        {
            var dbPath = ResolveDbPath(cfg);
            var tmp = Path.Combine(Path.GetTempPath(), $"llmrouter-backup-{Guid.NewGuid():N}.db");
            try
            {
                await using var conn = new SqliteConnection($"Data Source={dbPath}");
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM INTO @p0";
                cmd.Parameters.AddWithValue("@p0", tmp);
                await cmd.ExecuteNonQueryAsync();
                var bytes = await File.ReadAllBytesAsync(tmp);
                var name = $"llmrouter-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db";
                return Results.File(bytes, "application/octet-stream", name);
            }
            finally
            {
                // best-effort: failure is non-fatal
                try { File.Delete(tmp); } catch { }
            }
        });

        g.MapGet("/db-backups/export-all", async (IConfiguration cfg) =>
        {
            var dbPath = ResolveDbPath(cfg);
            var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                var names = new List<string>();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync()) names.Add(rd.GetString(0));
                }
                foreach (var name in names)
                {
                    var rows = new List<Dictionary<string, object?>>();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM {SqliteIdent.Quote(name)}";
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < rd.FieldCount; i++)
                            row[rd.GetName(i)] = rd.GetValue(i) is DBNull ? null : rd.GetValue(i);
                        rows.Add(row);
                    }
                    tables[name] = rows;
                }
            }
            return Results.Json(new { exportedAt = DateTime.UtcNow.ToString("o"), tables }, JsonOpts);
        });

        g.MapPost("/db-backups/import", async (HttpContext ctx, IConfiguration cfg, LlmRouterDbContext db) =>
        {
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid JSON backup" }); }
            if (!doc.RootElement.TryGetProperty("tables", out var tablesEl) || tablesEl.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "backup must have a 'tables' object" });

            var dbPath = ResolveDbPath(cfg);
            var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int importedTables = 0, importedRows = 0;
            var errors = new List<string>();

            await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                await conn.OpenAsync();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync()) valid.Add(rd.GetString(0));
                }

                await using var tx = await conn.BeginTransactionAsync();
                foreach (var tbl in tablesEl.EnumerateObject())
                {
                    var name = tbl.Name;
                    if (!valid.Contains(name) || SkipImportTables.Contains(name)) continue;
                    if (tbl.Value.ValueKind != JsonValueKind.Array) continue;
                    try
                    {
                        await using var del = conn.CreateCommand();
                        del.Transaction = (SqliteTransaction)tx;
                        del.CommandText = $"DELETE FROM {SqliteIdent.Quote(name)}";
                        await del.ExecuteNonQueryAsync();

                        foreach (var rowEl in tbl.Value.EnumerateArray())
                        {
                            if (rowEl.ValueKind != JsonValueKind.Object) continue;
                            var cols = new List<string>();
                            var vals = new List<string>();
                            var prms = new List<SqliteParameter>();
                            var i = 0;
                            foreach (var prop in rowEl.EnumerateObject())
                            {
                                if (!SqliteIdent.TryQuote(prop.Name, out var colName)) continue;
                                cols.Add(colName);
                                vals.Add($"@p{i}");
                                prms.Add(new SqliteParameter($"@p{i}", ToDbValue(prop.Value)));
                                i++;
                            }
                            await using var ins = conn.CreateCommand();
                            ins.Transaction = (SqliteTransaction)tx;
                            ins.CommandText = $"INSERT INTO {SqliteIdent.Quote(name)} ({string.Join(",", cols)}) VALUES ({string.Join(",", vals)})";
                            ins.Parameters.AddRange(prms.ToArray());
                            await ins.ExecuteNonQueryAsync();
                            importedRows++;
                        }
                        importedTables++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{name}: {ex.Message}");
                        await tx.RollbackAsync();
                        return Results.Json(new { error = "import failed, rolled back", details = errors }, statusCode: 400);
                    }
                }
                await tx.CommitAsync();
            }

            // EF's pooled connections may cache the file; force a fresh read.
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            await LLMRouter.Core.Extras.Extras.AuditAsync(db, "db.import", $"tables={importedTables} rows={importedRows}");
            return Results.Json(new { importedTables, importedRows, errors }, JsonOpts);
        });
    }

    private static object? ToDbValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        _ => v.ToString(),
    };
}
