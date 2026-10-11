using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Extras;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-098: webhooks + deliveries partidos do kv blob para tabelas.
/// Verifica migração idempotente (uma vez, com flag), shape de CRUD,
/// delivery row por teste, retenção de 100 e reindex a partir da tabela.
/// </summary>
public class Spec098Tests : WebAppTestBase
{
    [Fact]
    public async Task Webhook_blob_migrates_once_with_flag()
    {
        await using (var db = Db())
        {
            db.Kv.Add(new KvEntry
            {
                Scope = "webhooks", Key = "list",
                Value = """[{"id":"w1","url":"https://a.dev/hook","events":["*"]},{"id":"w2","url":"https://b.dev/hook","events":["combo.update"]}]""",
            });
            await db.SaveChangesAsync();
        }

        await using (var db = Db())
        {
            var first = await WebhookStore.ListAsync(db);
            first.Count.ShouldBe(2);
            (await db.Kv.FindAsync("_migrated", "webhooks")).ShouldNotBeNull();

            // Second pass must not duplicate.
            var second = await WebhookStore.ListAsync(db);
            second.Count.ShouldBe(2);
            (await db.Webhooks.CountAsync()).ShouldBe(2);
        }
    }

    [Fact]
    public async Task Webhook_crud_roundtrip_keeps_shape()
    {
        await LoginAsync();
        var create = await Client.PostAsJsonAsync("/api/webhooks",
            new { url = "https://h.dev/x", events = new[] { "usage.update" } });
        create.EnsureSuccessStatusCode();

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/webhooks");
        var wh = list.GetProperty("webhooks").EnumerateArray().Single();
        var id = wh.GetProperty("id").GetString()!;
        wh.GetProperty("url").GetString().ShouldBe("https://h.dev/x");

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/webhooks/{id}");
        detail.GetProperty("events")[0].GetString().ShouldBe("usage.update");

        var upd = await Client.PutAsJsonAsync($"/api/webhooks/{id}", new { enabled = false });
        upd.EnsureSuccessStatusCode();
        var merged = (await Client.GetFromJsonAsync<JsonElement>($"/api/webhooks/{id}"));
        merged.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        var del = await Client.DeleteAsync($"/api/webhooks/{id}");
        del.EnsureSuccessStatusCode();
        var gone = await Client.GetAsync($"/api/webhooks/{id}");
        gone.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Webhook_test_endpoint_writes_delivery_row()
    {
        await LoginAsync();
        await using (var db = Db())
        {
            await WebhookStore.UpsertAsync(db, JsonDocument.Parse(
                """{"id":"wtest","url":"https://t.dev","events":["*"]}""").RootElement);
            await db.SaveChangesAsync();
        }

        var r = await Client.PostAsync("/api/webhooks/wtest/test", null);
        r.EnsureSuccessStatusCode();
        var delivery = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("delivery");
        delivery.GetProperty("eventType").GetString().ShouldBe("webhook.test");

        var deliveries = await Client.GetFromJsonAsync<JsonElement>("/api/webhooks/wtest/deliveries");
        deliveries.GetProperty("deliveries").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Deliveries_retention_caps_at_100()
    {
        await using var db = Db();
        for (var i = 0; i < 105; i++)
            await WebhookStore.AddDeliveryAsync(db, "wcap", new { id = $"d{i}", status = "ok" });
        await db.SaveChangesAsync();

        var items = await WebhookStore.DeliveriesAsync(db, "wcap");
        items.Length.ShouldBe(100);
        items[0].GetProperty("id").GetString().ShouldBe("d5"); // oldest 5 pruned
    }

    [Fact]
    public async Task KvIndex_reindex_webhooks_reads_table()
    {
        await using var db = Db();
        foreach (var i in new[] { "wa", "wb" })
        {
            await WebhookStore.UpsertAsync(db, JsonDocument.Parse(
                $$"""{"id":"{{i}}","url":"https://x.dev","events":["*"]}""").RootElement);
        }
        await db.SaveChangesAsync();

        var n = await KvIndex.ReindexAsync(db, "webhooks");
        n.ShouldBe(2);
        await db.SaveChangesAsync();
        (await db.Kv.FindAsync("webhooks", "items/wa")).ShouldNotBeNull();
        (await db.Kv.FindAsync("webhooks", "items/wb")).ShouldNotBeNull();
    }
}
