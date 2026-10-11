using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Extras;

/// <summary>
/// SPEC-098: webhooks/deliveries live in tables now (was the
/// `kv("webhooks","list")` and `kv("webhookDeliveries", id)` JSON blobs).
/// The blob is split into rows once per scope and marked
/// `kv._migrated:{scope}` — the migration runs lazily on first access.
/// Contracts preserved: the list accessor still returns the same item
/// shapes ({id,url,events,...}) the blob carried.
/// </summary>
public static class WebhookStore
{
    public const int MaxDeliveriesPerWebhook = 100;

    private static readonly JsonSerializerOptions O = new(JsonSerializerDefaults.Web);

    /// <summary>All registered webhooks (item JSON, same shape as the old blob).</summary>
    public static async Task<List<JsonElement>> ListAsync(LlmRouterDbContext db)
    {
        await MigrateAsync(db);
        return await db.Webhooks.AsNoTracking()
            .Select(w => w.Data).ToListAsync() is { } rows
            ? rows.Select(r => JsonDocument.Parse(r).RootElement.Clone()).ToList()
            : [];
    }

    /// <summary>Insert or replace a webhook item (id taken from item["id"]).</summary>
    public static async Task UpsertAsync(LlmRouterDbContext db, JsonElement item)
    {
        await MigrateAsync(db);
        var id = item.GetProperty("id").GetString()!;
        var row = await db.Webhooks.FindAsync(id)
            ?? db.Webhooks.Add(new Webhook
            {
                Id = id,
                CreatedAt = DateTime.UtcNow.ToString("O"),
            }).Entity;
        row.Data = item.GetRawText();
    }

    public static async Task RemoveAsync(LlmRouterDbContext db, string id)
    {
        await MigrateAsync(db);
        if (await db.Webhooks.FindAsync(id) is { } row) db.Webhooks.Remove(row);
    }

    /// <summary>Deliveries for a webhook (item JSON, oldest → newest).</summary>
    public static async Task<JsonElement[]> DeliveriesAsync(LlmRouterDbContext db, string webhookId)
    {
        await MigrateDeliveriesAsync(db);
        var rows = await db.WebhookDeliveries.AsNoTracking()
            .Where(d => d.WebhookId == webhookId)
            .OrderBy(d => d.Id).Select(d => d.Data).ToListAsync();
        return rows.Select(r => JsonDocument.Parse(r).RootElement.Clone()).ToArray();
    }

    /// <summary>Append a delivery row; keeps only the newest <see cref="MaxDeliveriesPerWebhook"/> per webhook.</summary>
    public static async Task AddDeliveryAsync(LlmRouterDbContext db, string webhookId, object item)
    {
        await MigrateDeliveriesAsync(db);
        db.WebhookDeliveries.Add(new WebhookDelivery
        {
            WebhookId = webhookId,
            Data = JsonSerializer.Serialize(item, O),
            At = DateTime.UtcNow.ToString("O"),
        });
        await db.SaveChangesAsync();
        var prune = await db.WebhookDeliveries
            .Where(d => d.WebhookId == webhookId)
            .OrderByDescending(d => d.Id).Skip(MaxDeliveriesPerWebhook)
            .Select(d => d.Id).ToListAsync();
        if (prune.Count > 0)
            db.WebhookDeliveries.RemoveRange(
                db.WebhookDeliveries.Where(d => prune.Contains(d.Id)));
    }

    /// <summary>kv("webhooks","list") → webhooks rows, once.</summary>
    public static async Task MigrateAsync(LlmRouterDbContext db)
    {
        if (await db.Kv.FindAsync("_migrated", "webhooks") is not null) return;
        var raw = (await db.Kv.FindAsync("webhooks", "list"))?.Value;
        if (raw is not null && !await db.Webhooks.AnyAsync())
        {
            try
            {
                foreach (var item in JsonDocument.Parse(raw).RootElement.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var i) || i.GetString() is not { Length: > 0 } id)
                        continue;
                    db.Webhooks.Add(new Webhook
                    {
                        Id = id, Data = item.GetRawText(),
                        CreatedAt = DateTime.UtcNow.ToString("O"),
                    });
                }
            }
            catch (JsonException) { /* malformed blob — nothing to migrate */ }
        }
        db.Kv.Add(new KvEntry { Scope = "_migrated", Key = "webhooks", Value = "1" });
        await db.SaveChangesAsync();
    }

    /// <summary>kv("webhookDeliveries",*) rows → webhookDeliveries rows, once.</summary>
    public static async Task MigrateDeliveriesAsync(LlmRouterDbContext db)
    {
        if (await db.Kv.FindAsync("_migrated", "webhookDeliveries") is not null) return;
        var blobs = await db.Kv.Where(k => k.Scope == "webhookDeliveries").ToListAsync();
        if (blobs.Count > 0 && !await db.WebhookDeliveries.AnyAsync())
        {
            foreach (var blob in blobs)
            {
                try
                {
                    foreach (var item in JsonDocument.Parse(blob.Value).RootElement.EnumerateArray())
                        db.WebhookDeliveries.Add(new WebhookDelivery
                        {
                            WebhookId = blob.Key, Data = item.GetRawText(),
                            At = DateTime.UtcNow.ToString("O"),
                        });
                }
                catch (JsonException) { /* malformed blob — skip */ }
            }
        }
        db.Kv.Add(new KvEntry { Scope = "_migrated", Key = "webhookDeliveries", Value = "1" });
        await db.SaveChangesAsync();
    }
}
