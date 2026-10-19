using System.Text.Json;
using LLMRouter.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LLMRouter.Core.Guardrails;

/// <summary>SPEC-147: safetyEvents writer + query helpers.</summary>
public static class SafetyEvents
{
    /// <summary>Grava um evento de segurança (chamador decide SaveChanges).</summary>
    public static void Record(LlmRouterDbContext db, string guardrail, string kind,
        string action, string severity, object? detail = null,
        string? keyId = null, string? model = null)
    {
        db.SafetyEvents.Add(new SafetyEvent
        {
            At = DateTime.UtcNow.ToString("O"),
            Guardrail = guardrail.Length > 200 ? guardrail[..200] : guardrail,
            Kind = kind,
            Action = action,
            Severity = severity,
            KeyId = keyId,
            Model = model,
            Detail = JsonSerializer.Serialize(detail ?? new { }),
        });
    }

    /// <summary>Variante async que já persiste — uso fora do pipeline (dry-run, testes).</summary>
    public static async Task RecordAsync(LlmRouterDbContext db, string guardrail, string kind,
        string action, string severity, object? detail = null,
        string? keyId = null, string? model = null, CancellationToken ct = default)
    {
        Record(db, guardrail, kind, action, severity, detail, keyId, model);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Agregações p/ /api/safety/stats.</summary>
    public static async Task<object> StatsAsync(LlmRouterDbContext db, CancellationToken ct = default)
    {
        var rows = await db.SafetyEvents.ToListAsync(ct);
        return new
        {
            total = rows.Count,
            unreviewed = rows.Count(r => !r.Reviewed),
            byAction = rows.GroupBy(r => r.Action).ToDictionary(g => g.Key, g => g.Count()),
            byKind = rows.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.Count()),
            byGuardrail = rows.GroupBy(r => r.Guardrail).OrderByDescending(g => g.Count())
                .Take(20).ToDictionary(g => g.Key, g => g.Count()),
            byKey = rows.Where(r => r.KeyId != null).GroupBy(r => r.KeyId!)
                .ToDictionary(g => g.Key, g => g.Count()),
        };
    }
}
