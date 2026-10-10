using System.Text.Json;
using LLMRouter.Core.Data;
using LLMRouter.Core.Gateway;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Media;

/// <summary>
/// SPEC-063: media-kind-aware provider resolution for /v1 media endpoints.
/// Model-based resolution first (same pipeline as chat), filtered to connections
/// opted into the media kind; falls back to every active connection declaring
/// the kind when the request has no resolvable model (e.g. /v1/voices).
/// </summary>
public static class MediaRouter
{
    /// <summary>
    /// Resolve targets for a media request. <paramref name="kind"/> is the media
    /// kind derived from the path (see <see cref="MediaKinds.KindForPath"/>).
    /// Returns (targets, unsupported) — unsupported=true when the kind is known
    /// but no active connection declares it and no resolvable target exists.
    /// </summary>
    public static async Task<(List<ResolvedTarget> Targets, bool Unsupported)> ResolveAsync(
        LlmRouterDbContext db,
        GatewayEngine engine,
        ProviderRegistry registry,
        string? kind,
        string model,
        JsonElement? body,
        CancellationToken ct)
    {
        var targets = model.Length > 0
            ? await engine.ResolveAsync(model, body, ct)
            : [];
        if (kind is not null)
            targets = MediaKinds.Filter(targets, kind, t => t.Connection);
        if (targets.Count > 0 || kind is null)
            return (targets, false);

        // fallback: connections opted into this kind, regardless of model
        var conns = await db.ProviderConnections.Where(c => c.IsActive).ToListAsync(ct);
        foreach (var c in conns.Where(c => MediaKinds.KindsOf(c).Contains(kind))
                     .OrderBy(c => c.Priority))
        {
            var p = registry.GetProvider(c.Provider) ?? await NodeResolver.ResolveAsync(db, c.Provider, ct);
            if (p is null || !Resilience.ProviderBreaker.CanExecute(p.Id, p.AuthType)) continue;
            var upstreamModel = model.Length > 0 ? model : (p.Models?.FirstOrDefault()?.Id ?? "");
            targets.Add(new ResolvedTarget(p, c, upstreamModel, null));
        }
        return (targets, targets.Count == 0);
    }
}
