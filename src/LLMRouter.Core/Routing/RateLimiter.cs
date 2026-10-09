using System.Collections.Concurrent;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Routing;

/// <summary>
/// SPEC-039: single-node sliding-window rate limiter. Each enabled rateLimits
/// row gets its own bucket holding request ticks and token ticks from the last
/// 60s. Consulted in the gateway before dispatch (apiKey/model scopes at
/// request level, provider scope per resolved target); tokens are credited
/// back when usage is logged.
/// </summary>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public readonly Queue<long> RequestTicks = new();
        public readonly Queue<(long Tick, int Count)> TokenTicks = new();
        public long TokenSum;
    }

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private static long NowSec => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private const int WindowSec = 60;

    public sealed record Violation(string Scope, string ScopeValue, string Kind, int Limit, int RetryAfterSec);
    public sealed record WindowStat(string RuleId, int Requests, long Tokens);

    /// <summary>
    /// Check every matching rule and, when all pass, consume one request on each.
    /// providerOnly=true evaluates only provider-scope rules (inside the
    /// per-target dispatch loop); false skips provider scope (pre-resolution,
    /// provider not yet known). A rejected request consumes nothing.
    /// </summary>
    public Violation? CheckAndConsume(IReadOnlyList<RateLimit> rules, string apiKey,
        string? providerId, string model, bool providerOnly = false)
    {
        var matching = rules
            .Where(r => providerOnly ? r.Scope == "provider" : r.Scope != "provider")
            .Where(r => Matches(r, apiKey, providerId, model))
            .ToList();
        foreach (var r in matching)
            if (Check(r) is { } v) return v;
        foreach (var r in matching) Consume(r);
        return null;
    }

    /// <summary>Dry-run of CheckAndConsume for quota preview: returns the first
    /// violation a request would hit without consuming anything.</summary>
    public Violation? Peek(IReadOnlyList<RateLimit> rules, string apiKey,
        string? providerId, string model, bool providerOnly = false)
    {
        var matching = rules
            .Where(r => providerOnly ? r.Scope == "provider" : r.Scope != "provider")
            .Where(r => Matches(r, apiKey, providerId, model));
        foreach (var r in matching)
            if (Check(r) is { } v) return v;
        return null;
    }

    /// <summary>Credit token usage to every fully-matching rule's window.</summary>
    public void RecordTokens(IReadOnlyList<RateLimit> rules, string apiKey,
        string? providerId, string model, int tokens)
    {
        if (tokens <= 0) return;
        foreach (var r in rules)
        {
            if (r.Tpm <= 0 || !Matches(r, apiKey, providerId, model)) continue;
            var b = _buckets.GetOrAdd(r.Id, _ => new Bucket());
            lock (b)
            {
                PruneTokens(b, NowSec);
                b.TokenTicks.Enqueue((NowSec, tokens));
                b.TokenSum += tokens;
            }
        }
    }

    /// <summary>Current window usage per rule (for /api/rate-limit/status and the UI).</summary>
    public IReadOnlyList<WindowStat> Snapshot(IReadOnlyList<RateLimit> rules)
    {
        var now = NowSec;
        var stats = new List<WindowStat>(rules.Count);
        foreach (var r in rules)
        {
            if (!_buckets.TryGetValue(r.Id, out var b)) { stats.Add(new WindowStat(r.Id, 0, 0)); continue; }
            lock (b)
            {
                Prune(b, now);
                stats.Add(new WindowStat(r.Id, b.RequestTicks.Count, b.TokenSum));
            }
        }
        return stats;
    }

    private Violation? Check(RateLimit r)
    {
        var b = _buckets.GetOrAdd(r.Id, _ => new Bucket());
        lock (b)
        {
            var now = NowSec;
            Prune(b, now);
            if (r.Rpm > 0 && b.RequestTicks.Count >= r.Rpm + Math.Max(0, r.Burst))
                return new Violation(r.Scope, r.ScopeValue, "rpm", r.Rpm, RetryAfter(b.RequestTicks, now));
            if (r.Tpm > 0 && b.TokenSum >= r.Tpm)
                return new Violation(r.Scope, r.ScopeValue, "tpm", r.Tpm, RetryAfterTokens(b.TokenTicks, now));
            return null;
        }
    }

    private void Consume(RateLimit r)
    {
        var b = _buckets.GetOrAdd(r.Id, _ => new Bucket());
        lock (b) { b.RequestTicks.Enqueue(NowSec); }
    }

    private static void Prune(Bucket b, long now)
    {
        while (b.RequestTicks.Count > 0 && b.RequestTicks.Peek() <= now - WindowSec)
            b.RequestTicks.Dequeue();
        PruneTokens(b, now);
    }

    private static void PruneTokens(Bucket b, long now)
    {
        while (b.TokenTicks.Count > 0 && b.TokenTicks.Peek().Tick <= now - WindowSec)
            b.TokenSum -= b.TokenTicks.Dequeue().Count;
    }

    private static int RetryAfter(Queue<long> ticks, long now) =>
        ticks.Count == 0 ? 1 : Math.Clamp((int)(ticks.Peek() + WindowSec - now), 1, WindowSec);

    private static int RetryAfterTokens(Queue<(long Tick, int Count)> ticks, long now) =>
        ticks.Count == 0 ? 1 : Math.Clamp((int)(ticks.Peek().Tick + WindowSec - now), 1, WindowSec);

    private static bool Matches(RateLimit r, string apiKey, string? providerId, string model) =>
        r.Scope switch
        {
            "apiKey" => MatchValue(r.ScopeValue, apiKey),
            "provider" => MatchValue(r.ScopeValue, providerId),
            "model" => MatchValue(r.ScopeValue, model),
            _ => false,
        };

    private static bool MatchValue(string rule, string? val) =>
        rule == "*"
        || (val is not null
            && (string.Equals(rule, val, StringComparison.OrdinalIgnoreCase)
                || (rule.EndsWith('*') && val.StartsWith(rule[..^1], StringComparison.OrdinalIgnoreCase))));
}
