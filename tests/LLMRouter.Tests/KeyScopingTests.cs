using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-149: allowedEndpoints/allowedIpRanges gate, rotate grace, anomalies.</summary>
public class KeyScopingTests : WebAppTestBase
{
    private async Task<string> SeedKey(string? endpoints, string? cidrs)
    {
        await using var db = Db();
        var k = new ApiKey
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Key = $"sk-test{Guid.NewGuid():N}",
            Name = "scoped", IsActive = true, CreatedAt = "x",
            AllowedEndpoints = endpoints, AllowedIpRanges = cidrs,
        };
        db.ApiKeys.Add(k);
        await db.SaveChangesAsync();
        return k.Key;
    }

    private HttpRequestMessage ChatReq(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        { Content = JsonContent.Create(new { model = "m", messages = new[] { new { role = "user", content = "hi" } } }) };
        req.Headers.Authorization = new("Bearer", key);
        return req;
    }

    // client sem cookie de dashboard — o cookie bypassa o gate como "dashboard"
    private HttpClient Anon() => Factory.CreateClient();

    [Fact]
    public async Task EndpointScope_DeniesChat()
    {
        await LoginAsync();
        var key = await SeedKey("[\"embeddings\"]", null);
        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadAsStringAsync();
        body.ShouldContain("key_scope");
    }

    [Fact]
    public async Task EndpointScope_WildcardAllows()
    {
        await LoginAsync();
        var key = await SeedKey("[\"*\"]", null);
        var res = await Anon().SendAsync(ChatReq(key));
        res.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden); // passa o gate, falha depois (sem provider)
    }

    [Theory]
    [InlineData("10.1.2.3", "10.0.0.0/8", true)]
    [InlineData("192.168.1.5", "10.0.0.0/8", false)]
    [InlineData("192.168.1.5", "192.168.1.5", true)]
    [InlineData("192.168.1.5", "192.168.1.5/32", true)]
    [InlineData("::1", "::1/128", true)]
    [InlineData("10.1.2.3", "bad", false)]
    public void Cidr_Matching(string ip, string cidr, bool expected)
        => LLMRouter.Server.Endpoints.MgmtBatchEndpoints.CidrMatch(ip, cidr).ShouldBe(expected);

    [Fact]
    public async Task Rotate_GracesOldKey()
    {
        await LoginAsync();
        var key = await SeedKey(null, null);
        string id;
        await using (var db = Db())
        {
            id = (await db.ApiKeys.FirstAsync(k => k.Key == key)).Id;
        }
        var res = await Client.PostAsJsonAsync($"/api/keys/{id}/rotate", new { });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var newKey = body.GetProperty("key").GetProperty("key").GetString()!;
        newKey.ShouldStartWith("sk-");
        newKey.ShouldNotBe(key);
        // old key still authed within grace
        await using (var db = Db())
        {
            var old = await db.ApiKeys.FindAsync(id);
            old!.RetiresAt.ShouldNotBeNull();
            old.IsActive.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task NewIp_WritesAnomaly()
    {
        await LoginAsync();
        var key = await SeedKey(null, null);
        await Anon().SendAsync(ChatReq(key)); // qualquer /v1 → gate roda, escreve new_ip
        var res = await Client.GetFromJsonAsync<JsonElement>("/api/keys/anomalies?kind=new_ip");
        var anomalies = res.GetProperty("anomalies").EnumerateArray().ToList();
        anomalies.ShouldNotBeEmpty();
        anomalies[0].GetProperty("kind").GetString().ShouldBe("new_ip");
        var id = anomalies[0].GetProperty("id").GetInt64();
        var rv = await Client.PostAsync($"/api/keys/anomalies/{id}/reviewed", null);
        rv.EnsureSuccessStatusCode();
    }
}
