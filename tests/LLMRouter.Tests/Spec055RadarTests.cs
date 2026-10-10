using System.Net;
using LLMRouter.Core.Data;
using LLMRouter.Core.Radar;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-055: radar items, combos sugeridos, sync graceful, endpoints.</summary>
public class Spec055RadarTests : WebAppTestBase
{
    [Fact]
    public async Task Combos_sugeridos_a_partir_do_catalogo()
    {
        await using var db = Db();
        db.RadarItems.AddRange(
            new RadarItem { Kind = "offer", ItemKey = "o1", Title = "free-model-a", Data = """{"provider":"p1","name":"free-model-a"}""", At = "x" },
            new RadarItem { Kind = "offer", ItemKey = "o2", Title = "free-model-b", Data = """{"provider":"p1","name":"free-model-b"}""", At = "x" });
        await db.SaveChangesAsync();

        var sugg = await RadarStore.SuggestCombosAsync(db);
        sugg.ShouldNotBeEmpty();
        sugg[0].ToString().ShouldContain("free-p1");
    }

    [Fact]
    public async Task Sync_sem_sources_e_graceful()
    {
        await using var db = Db();
        var (added, error) = await RadarStore.SyncKindAsync(db, new HttpClient(), "catalog", []);
        added.ShouldBe(0);
        error.ShouldBeNull();
    }

    [Fact]
    public async Task Endpoints_status_e_listas()
    {
        await LoginAsync();
        await using var db = Db();
        db.RadarItems.Add(new RadarItem { Kind = "catalog", ItemKey = "c1", Title = "m1", Data = "{}", At = "x" });
        await db.SaveChangesAsync();

        (await Client.GetAsync("/api/radar/catalogs")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/radar/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/radar/settings")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/api/radar/combos")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
