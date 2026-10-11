using System.Text.Json;
using LLMRouter.Core.Data;

namespace LLMRouter.Core.Gateway;

/// <summary>
/// SPEC-111: gates aplicados a virtual keys (userKeys) antes do dispatch —
/// allowlist de modelos e teto de gasto por período. A autenticação em si fica
/// em <c>GatewayEndpoints.AuthenticatedKey</c>; aqui só a política.
/// </summary>
public static class VirtualKeyGate
{
    /// <summary>
    /// true quando modelsAllow está vazio ("[]") OU algum glob casa o model
    /// ("provider/model"; "*" casa tudo; "prefix*" casa prefixo).
    /// </summary>
    public static bool ModelAllowed(UserKey uk, string model)
    {
        if (string.IsNullOrWhiteSpace(uk.ModelsAllow)) return true;
        try
        {
            var globs = JsonSerializer.Deserialize<string[]>(uk.ModelsAllow);
            if (globs is null || globs.Length == 0) return true;
            foreach (var g in globs)
                if (MatchGlob(g, model)) return true;
            return false;
        }
        catch { return false; }
    }

    /// <summary>Início do período corrente ("yyyy-MM-dd" UTC) p/ o budgetPeriod.</summary>
    public static string PeriodStart(string budgetPeriod, DateTime now)
    {
        var d = now.Date;
        return budgetPeriod switch
        {
            "daily" => d.ToString("yyyy-MM-dd"),
            "weekly" => d.AddDays(-(((int)d.DayOfWeek) + 6) % 7).ToString("yyyy-MM-dd"), // semana começa segunda
            "monthly" => new DateTime(d.Year, d.Month, 1).ToString("yyyy-MM-dd"),
            _ => "all",
        };
    }

    /// <summary>
    /// Gasto considerando o reset lazy: se o período gravado difere do
    /// corrente, o spend efetivo é zero (a próxima escrita zera a coluna).
    /// </summary>
    public static double EffectiveSpend(UserKey uk, DateTime now) =>
        uk.BudgetPeriod == "none"
            ? uk.SpendUsd
            : uk.SpendPeriodStart == PeriodStart(uk.BudgetPeriod, now) ? uk.SpendUsd : 0;

    /// <summary>true quando há teto (budgetUsd>0) e o spend efetivo o atingiu.</summary>
    public static bool BudgetExceeded(UserKey uk, DateTime now) =>
        uk.BudgetUsd > 0 && EffectiveSpend(uk, now) >= uk.BudgetUsd;

    /// <summary>
    /// Aplica o reset lazy e credita o custo — chamado pelo escritor serial
    /// (UsageWriter) ou pelo caminho sync; nunca em paralelo.
    /// </summary>
    public static void ApplySpend(UserKey uk, double costUsd, DateTime now)
    {
        var ps = PeriodStart(uk.BudgetPeriod, now);
        if (uk.BudgetPeriod != "none" && uk.SpendPeriodStart != ps)
        {
            uk.SpendUsd = 0;
            uk.SpendPeriodStart = ps;
        }
        else if (uk.SpendPeriodStart == "")
            uk.SpendPeriodStart = ps;
        uk.SpendUsd += costUsd;
    }

    private static bool MatchGlob(string rule, string val) =>
        rule == "*"
        || string.Equals(rule, val, StringComparison.OrdinalIgnoreCase)
        || (rule.EndsWith('*') && val.StartsWith(rule[..^1], StringComparison.OrdinalIgnoreCase));
}
