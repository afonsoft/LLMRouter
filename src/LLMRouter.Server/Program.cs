using LLMRouter.Core.Data;
using LLMRouter.Core.Registry;
using LLMRouter.Core.Routing;
using LLMRouter.Core.Gateway;
using LLMRouter.Server.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddProvider(LLMRouter.Server.Services.ConsoleLogBuffer.Instance);

var dbPath = Environment.GetEnvironmentVariable("LLMROUTER_DB_PATH")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LLMRouter", "llmrouter.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<LlmRouterDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<ComboPlanner>();
builder.Services.AddSingleton<ModelResolver>();
builder.Services.AddScoped<GatewayEngine>();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("upstream").ConfigureHttpClient(c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddAuthentication("cookie")
    .AddCookie("cookie", o =>
    {
        o.Cookie.Name = "llmrouter_auth";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<LlmRouterDbContext>().EnsureCreated();

app.UseAuthentication();
app.UseAuthorization();
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapAuthEndpoints();
app.MapGatewayEndpoints();
app.MapManagementEndpoints();

app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
