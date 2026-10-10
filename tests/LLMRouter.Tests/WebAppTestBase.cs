using System.Net.Http.Json;
using LLMRouter.Core.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// Base para testes WebApplicationFactory com sqlite isolado por classe:
/// cria factory com Db:Path único, login admin e atalho p/ DbContext scoped.
/// </summary>
public abstract class WebAppTestBase : IAsyncLifetime
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"llmr-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    /// <summary>Factory do app de teste.</summary>
    protected WebApplicationFactory<Program> Factory => _factory;
    /// <summary>HttpClient anônimo do app de teste.</summary>
    protected HttpClient Client => _client;

    /// <summary>Permite subclasses customizarem a config do host antes do build.</summary>
    protected virtual void ConfigureHost(WebHostBuilderContext ctx, IConfigurationBuilder cfg) { }

    /// <inheritdoc/>
    public virtual Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((ctx, c) =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["Db:Path"] = _dbPath });
                ConfigureHost(ctx, c);
            }));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>DbContext novo (scoped) — use e descarte com await using.</summary>
    protected LlmRouterDbContext Db() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<LlmRouterDbContext>();

    /// <summary>Login dashboard (cookie) — primeiro run aceita qualquer senha.</summary>
    protected async Task LoginAsync()
    {
        var r = await _client.PostAsJsonAsync("/api/auth/login", new { password = "test1234" });
        r.EnsureSuccessStatusCode();
    }
}
