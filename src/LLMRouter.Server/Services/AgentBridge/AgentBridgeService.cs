using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Server.Services.AgentBridge;

/// <summary>
/// Orchestrates the AgentBridge MITM server: lifecycle, per-agent state,
/// bypass patterns, model mappings, detected-model tracking — port of
/// mitm/manager.ts + lib/db/agentBridge{State,Bypass,Mappings}.ts.
/// </summary>
public sealed class AgentBridgeService
{
    public MitmCa Ca { get; }
    public MitmProxy Proxy { get; }
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AgentBridgeService> _log;
    private string? _lastError;
    private DateTimeOffset? _startedAt;

    public AgentBridgeService(IServiceScopeFactory scopes, ILogger<AgentBridgeService> log,
        IHostEnvironment env, IConfiguration cfg)
    {
        _scopes = scopes;
        _log = log;
        var dataDir = cfg["Mitm:DataDir"];
        Ca = new MitmCa(dataDir);
        var forward = Environment.GetEnvironmentVariable("LLMR_INTERNAL_URL")
            ?? cfg["Mitm:ForwardBase"] ?? "http://127.0.0.1:8080";
        Proxy = new MitmProxy(Ca, forward, BypassFor, MappingsFor, InternalKey, log);
    }

    private async Task<IReadOnlyList<string>> BypassFor(string host)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.AgentBridgeBypass.AsNoTracking().Select(b => b.Pattern).ToListAsync();
    }

    private async Task<IReadOnlyDictionary<string, string>> MappingsFor(string agentId)
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.AgentBridgeMappings.AsNoTracking()
            .Where(m => m.AgentId == agentId)
            .ToDictionaryAsync(m => m.SourceModel, m => m.TargetModel);
    }

    /// <summary>pickApiKeyForInternalUse port: first active key's raw value.</summary>
    private async Task<string?> InternalKey()
    {
        await using var s = _scopes.CreateAsyncScope();
        var db = s.ServiceProvider.GetRequiredService<LlmRouterDbContext>();
        return await db.ApiKeys.AsNoTracking()
            .Where(k => k.IsActive).OrderBy(k => k.CreatedAt)
            .Select(k => k.Key).FirstOrDefaultAsync();
    }

    public async Task<object> ServerAction(string action, int? port, string? sudoPassword)
    {
        switch (action)
        {
            case "start":
            {
                if (Proxy.Running) return Status();
                try
                {
                    Ca.Ensure();
                    await Proxy.Start(port ?? 8888);
                    _startedAt = DateTimeOffset.UtcNow;
                    _lastError = null;
                }
                catch (Exception e) { _lastError = e.Message; }
                return Status();
            }
            case "stop":
                await Proxy.Stop();
                _startedAt = null;
                return Status();
            case "restart":
                await Proxy.Stop();
                await Proxy.Start(port ?? (Proxy.Port > 0 ? Proxy.Port : 8888));
                _startedAt = DateTimeOffset.UtcNow;
                return Status();
            case "status":
            default:
                return Status();
        }
    }

    public object Status() => new
    {
        running = Proxy.Running,
        port = Proxy.Port,
        pid = Proxy.Running ? Environment.ProcessId : (int?)null,
        uptimeSec = _startedAt is { } t ? (long)(DateTimeOffset.UtcNow - t).TotalSeconds : 0,
        totalConnections = Proxy.TotalConnections,
        intercepted = Proxy.Intercepted,
        passthrough = Proxy.Passthrough,
        activeConnections = Proxy.ActiveConnections,
        lastError = _lastError,
        certExists = Ca.CertExists,
        certTrusted = Ca.IsTrusted(),
    };

    public static object AgentFor(MitmTarget t, AgentBridgeState? state) => new
    {
        id = t.Id,
        name = t.Name,
        hosts = t.Hosts,
        viability = t.Viability,
        detectedModels = t.DetectedModels,
        detectCommand = t.DetectCommand,
        state = new
        {
            enabled = state?.Enabled ?? false,
            dns_enabled = state?.DnsEnabled ?? false,
            cert_installed = state?.CertInstalled ?? false,
            last_seen = state?.LastSeenAt,
        },
    };
}
