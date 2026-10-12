using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Services.TrafficInspector;

/// <summary>
/// Traffic Inspector facade — sessions + custom hosts (DB), capture-mode
/// state (http-proxy handle, system-proxy flag, tls-intercept via
/// settings.mitm), and the shared TrafficBuffer.
/// </summary>
public sealed class TrafficInspectorService : IAsyncDisposable
{
    private const int SessionPayloadMax = 1_048_576;
    private readonly IServiceScopeFactory _scopes;
    public TrafficBuffer Buffer { get; } = new();
    public InspectorHttpProxy HttpProxy { get; }
    public bool SystemProxyApplied { get; private set; }
    public string? SystemProxyGuardUntil { get; private set; }
    public int? SystemProxyPort { get; private set; }

    public TrafficInspectorService(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        HttpProxy = new InspectorHttpProxy(Buffer);
    }

    public async ValueTask DisposeAsync() => await HttpProxy.DisposeAsync();

    // ── sessions (port of lib/db/inspectorSessions.ts) ──────────────────────
    public async Task<object> CreateSession(string? name)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var row = new InspectorSession
        {
            Id = Guid.NewGuid().ToString(), Name = name,
            StartedAt = DateTime.UtcNow.ToString("o"),
        };
        db.InspectorSessions.Add(row);
        await db.SaveChangesAsync();
        return new { id = row.Id, started_at = row.StartedAt };
    }

    public async Task<List<InspectorSession>> ListSessions()
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.InspectorSessions.AsNoTracking()
            .OrderByDescending(x => x.StartedAt).ToListAsync();
    }

    public async Task<InspectorSession?> GetSession(string id)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.InspectorSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<List<string>> GetSessionRequests(string id)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.InspectorSessionRequests.AsNoTracking()
            .Where(r => r.SessionId == id).OrderBy(r => r.Seq)
            .Select(r => r.Payload).ToListAsync();
    }

    /// <summary>appendSessionRequest port: tx seq=max+1 + bump request_count.</summary>
    public async Task<int?> AppendSessionRequest(string sessionId, string payload)
    {
        if (payload.Length > SessionPayloadMax) return null;
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var session = await db.InspectorSessions.FindAsync(sessionId);
        if (session is null) return null;
        var next = (await db.InspectorSessionRequests
            .Where(r => r.SessionId == sessionId).MaxAsync(r => (int?)r.Seq) ?? 0) + 1;
        db.InspectorSessionRequests.Add(new InspectorSessionRequest
        { SessionId = sessionId, Seq = next, Payload = payload });
        session.RequestCount++;
        await db.SaveChangesAsync();
        return next;
    }

    /// <summary>PATCH action port: "stop" sets ended_at, "rename" sets name.</summary>
    public async Task<InspectorSession?> PatchSession(string id, string action, string? name)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var session = await db.InspectorSessions.FindAsync(id);
        if (session is null) return null;
        if (action == "stop") session.EndedAt = DateTime.UtcNow.ToString("o");
        else if (action == "rename") session.Name = name;
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>Delete session + cascade its request rows.</summary>
    public async Task<bool> DeleteSession(string id)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var session = await db.InspectorSessions.FindAsync(id);
        if (session is null) return false;
        db.InspectorSessionRequests.RemoveRange(
            db.InspectorSessionRequests.Where(r => r.SessionId == id));
        db.InspectorSessions.Remove(session);
        await db.SaveChangesAsync();
        return true;
    }

    // ── custom hosts (port of inspector_custom_hosts CRUD) ─────────────────
    public async Task<List<InspectorCustomHost>> ListHosts()
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.InspectorCustomHosts.AsNoTracking().ToListAsync();
    }

    public async Task AddHost(string host, string kind, string? label)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        if (await db.InspectorCustomHosts.FindAsync(host) is null)
        {
            db.InspectorCustomHosts.Add(new InspectorCustomHost
            {
                Host = host, Kind = kind, Label = label,
                CreatedAt = DateTime.UtcNow.ToString("o"),
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task<bool> PatchHost(string host, bool enabled)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var row = await db.InspectorCustomHosts.FindAsync(host);
        if (row is null) return false;
        row.Enabled = enabled;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task RemoveHost(string host)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        if (await db.InspectorCustomHosts.FindAsync(host) is { } row)
        {
            db.InspectorCustomHosts.Remove(row);
            await db.SaveChangesAsync();
        }
    }

    // ── capture modes ──────────────────────────────────────────────────────
    public async Task<object> HttpProxyAction(string action, int defaultPort)
    {
        if (action == "stop")
        {
            var port = HttpProxy.Running ? HttpProxy.Port : (int?)null;
            await HttpProxy.Stop();
            return new { ok = true, running = false, port = (int?)null };
        }
        if (HttpProxy.Running) return new { ok = true, running = true, port = HttpProxy.Port };
        try
        {
            await HttpProxy.Start(defaultPort);
            return new { ok = true, running = true, port = HttpProxy.Port };
        }
        catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            return new { error = new { message = $"Port {defaultPort} is already in use", type = "conflict", code = "EADDRINUSE", port = defaultPort } };
        }
    }

    /// <summary>system-proxy apply/revert — OS config not portable; report state only.</summary>
    public Task<object> SystemProxyAction(string action, int? port, int? guardMinutes)
    {
        if (action == "revert")
        {
            SystemProxyApplied = false;
            SystemProxyGuardUntil = null;
            SystemProxyPort = null;
            return Task.FromResult<object>(new { ok = true, applied = false });
        }
        return Task.FromResult<object>(new
        {
            error = "system proxy apply is not supported on this host — configure the OS proxy manually or use http-proxy mode",
        });
    }

    /// <summary>tls-intercept toggle → persists settings.mitm.tlsIntercept (read by MitmPolicy).</summary>
    public async Task<object> SetTlsIntercept(bool enabled)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        var row = await db.Settings.FindAsync(1) ?? db.Settings.Add(new SettingRow { Id = 1, Data = "{}" }).Entity;
        var doc = System.Text.Json.Nodes.JsonNode.Parse(row.Data ?? "{}")?.AsObject()
            ?? new System.Text.Json.Nodes.JsonObject();
        var mitm = doc["mitm"] as System.Text.Json.Nodes.JsonObject
            ?? (System.Text.Json.Nodes.JsonObject)(doc["mitm"] = new System.Text.Json.Nodes.JsonObject())!;
        mitm["tlsIntercept"] = enabled;
        row.Data = doc.ToJsonString();
        await db.SaveChangesAsync();
        return new { ok = true, enabled };
    }
}
