using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Config;

public static class ConfigRevisionStore
{
    public static async Task<ConfigRevision> RecordAsync(
        LlmRouterDbContext db, string area, string actor, string patch, string previous,
        CancellationToken ct = default)
    {
        var row = new ConfigRevision
        {
            At = DateTime.UtcNow.ToString("O"),
            Area = area,
            Actor = actor,
            Patch = patch,
            Prev = previous,
        };
        db.ConfigRevisions.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public static async Task<List<ConfigRevision>> ListAsync(
        LlmRouterDbContext db, string? area, CancellationToken ct = default) =>
        await db.ConfigRevisions
            .Where(x => area == null || x.Area == area)
            .OrderByDescending(x => x.Id)
            .Take(200)
            .AsNoTracking()
            .ToListAsync(ct);

    public static JsonElement Diff(ConfigRevision row)
    {
        using var prev = JsonDocument.Parse(row.Prev);
        using var patch = JsonDocument.Parse(row.Patch);
        return JsonSerializer.SerializeToElement(new
        {
            id = row.Id,
            area = row.Area,
            actor = row.Actor,
            previous = prev.RootElement.Clone(),
            patch = patch.RootElement.Clone(),
        });
    }
}
