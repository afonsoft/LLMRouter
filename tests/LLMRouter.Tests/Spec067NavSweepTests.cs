using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-067: todo link da sidebar resolve p/ rota @page não-placeholder.</summary>
public partial class Spec067NavSweepTests
{
    internal static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, ".specs"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Toda_rota_da_sidebar_tem_pagina()
    {
        var root = RepoRoot();
        var client = Path.Combine(root, "src", "LLMRouter.Client");
        var routes = new HashSet<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(client, "Pages"), "*.razor", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(client, "*.razor", SearchOption.TopDirectoryOnly)))
            foreach (Match m in RouteRe().Matches(File.ReadAllText(f)))
            {
                var route = m.Groups[1].Value;
                // rotas parametrizadas viram prefixo p/ match
                routes.Add(Regex.Replace(route, "/\\{[^}]+\\}", "/[]"));
            }

        var sidebar = File.ReadAllText(Path.Combine(client, "Layout", "SidebarData.cs"));
        var missing = new List<string>();
        foreach (Match m in UrlRe().Matches(sidebar))
        {
            var url = m.Groups[1].Value;
            if (!url.StartsWith("/dashboard")) continue;
            if (routes.Contains(url) ||
                routes.Any(r => r.Contains("/[]") && url.StartsWith(r[..r.IndexOf("/[]")], StringComparison.Ordinal)))
                continue;
            missing.Add(url);
        }
        missing.ShouldBeEmpty("sidebar links sem página: " + string.Join(", ", missing));
    }

    [GeneratedRegex("@page\\s+\"([^\"]+)\"")]
    private static partial Regex RouteRe();

    [GeneratedRegex("\"(/[a-z0-9/{}-]+)\"")]
    private static partial Regex UrlRe();
}

public sealed class Spec067RouteDupTests : WebAppTestBase
{
    [Fact]
    public void NenhumaRotaDeveSerDuplicada()
    {
        var dir = Path.Combine(Spec067NavSweepTests.RepoRoot(), "src", "LLMRouter.Client", "Pages");
        var dups = Directory.GetFiles(dir, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "@page\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value))
            .GroupBy(r => r).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Xunit.Assert.True(dups.Count == 0, "rotas duplicadas: " + string.Join(", ", dups));
    }
}
