using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using LLMRouter.Core.Compression;
using LLMRouter.Core.Services;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>
/// SPEC-099: llmlingua sidecar — descriptor npm/porta/health, engine
/// fail-open quando o serviço está parado, dispatch pro sidecar quando
/// running, e script committed presente no repo.
/// </summary>
public class Spec099Tests
{
    [Fact]
    public void Llmlingua_descriptor_has_npm_package_port_and_health()
    {
        var d = ServiceManager.Find("llmlingua");
        d.ShouldNotBeNull();
        d!.Port.ShouldBe(20135);
        d.HealthPath.ShouldBe("/health");
        d.NpmPackage.ShouldBe("@atjsh/llmlingua-2");
        d.EntryScript.ShouldBe("llmlingua-sidecar.mjs");
    }

    [Fact]
    public void Engine_failopens_when_service_stopped()
    {
        var engine = new LlmlinguaEngine(new HttpClient(), () => Task.FromResult(false));
        var body = Body();
        var r = engine.Apply(body, new EngineOptions());
        r.Compressed.ShouldBeFalse();
        r.Stats?.Skipped.ShouldBe("backend_unavailable");
    }

    [Fact]
    public void Engine_calls_sidecar_when_running()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    compressed = "SHORT",
                    originalTokens = 100, compressedTokens = 2,
                }),
            };
        });
        var engine = new LlmlinguaEngine(new HttpClient(handler), () => Task.FromResult(true));
        var r = engine.Apply(Body(), new EngineOptions());
        calls.ShouldBe(1);
        r.Compressed.ShouldBeTrue();
        TextOps.ExtractText(TextOps.Messages(r.Body)![0]).ShouldBe("SHORT");
        r.Stats!.Techniques.ShouldContain("llmlingua");
    }

    [Fact]
    public void Engine_failopens_on_sidecar_error()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var engine = new LlmlinguaEngine(new HttpClient(handler), () => Task.FromResult(true));
        var r = engine.Apply(Body(), new EngineOptions());
        r.Compressed.ShouldBeFalse();
    }

    [Fact]
    public void Sidecar_script_is_committed_and_valid()
    {
        var path = Path.Combine(RepoRoot(), "scripts", "llmlingua-sidecar.mjs");
        File.Exists(path).ShouldBeTrue();
        var src = File.ReadAllText(path);
        src.ShouldContain("/compress");
        src.ShouldContain("/health");
        src.ShouldContain("20135");
        src.ShouldContain("@atjsh/llmlingua-2");
    }

    private static JsonObject Body() => new()
    {
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = new string('x', 600) },
        },
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (dir!.GetFiles("*.sln*").Length > 0) return dir.FullName;
        return AppContext.BaseDirectory;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
