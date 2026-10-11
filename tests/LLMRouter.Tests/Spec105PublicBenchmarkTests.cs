using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace LLMRouter.Tests;

/// <summary>SPEC-105: perf harness knobs + RESULT_JSON + docs/perf.md row.</summary>
public class Spec105PublicBenchmarkTests
{
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "LLMRouter.slnx")))
            d = d.Parent;
        return d!.FullName;
    }

    private static async Task<(int code, string stdout)> PyAsync(string args, string? workdir = null)
    {
        var psi = new ProcessStartInfo("python3", args)
        {
            WorkingDirectory = workdir ?? RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var outp = await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, outp);
    }

    [Fact]
    public async Task Load_py_accepts_rate_concurrency_scenario_flags()
    {
        var (code, outp) = await PyAsync("scripts/perf/load.py --help");
        code.ShouldBe(0);
        outp.ShouldContain("--rate");
        outp.ShouldContain("--concurrency");
        outp.ShouldContain("--scenario");
        outp.ShouldContain("ratelimited");
        outp.ShouldContain("cached");
    }

    [Fact]
    public async Task Result_json_line_is_emitted_and_parseable()
    {
        var keys = Path.Combine(Path.GetTempPath(), $"llmr-perf-keys-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(keys, "k1\n");
        try
        {
            // dead gateway: every request errors, but RESULT_JSON still emits
            Environment.SetEnvironmentVariable("GATEWAY_URL", "http://127.0.0.1:9");
            Environment.SetEnvironmentVariable("MIN_RPS", "99999");
            var (_, outp) = await PyAsync($"scripts/perf/load.py {keys} 1");
            var line = outp.Split('\n').FirstOrDefault(l => l.StartsWith("RESULT_JSON="));
            line.ShouldNotBeNull();
            var r = JsonDocument.Parse(line![("RESULT_JSON=".Length)..]).RootElement;
            r.TryGetProperty("rps", out _).ShouldBeTrue();
            r.TryGetProperty("p50", out _).ShouldBeTrue();
            r.GetProperty("errors").GetInt32().ShouldBeGreaterThan(0);
            r.GetProperty("scenario").GetString().ShouldBe("combo");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GATEWAY_URL", null);
            Environment.SetEnvironmentVariable("MIN_RPS", null);
            File.Delete(keys);
        }
    }

    [Fact]
    public void Run_sh_appends_markdown_row_to_docs_perf_results()
    {
        var run = File.ReadAllText(Path.Combine(RepoRoot(), "scripts/perf/run.sh"));
        run.ShouldContain("docs/perf.md");
        run.ShouldContain("results:end");
        var doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs/perf.md"));
        doc.ShouldContain("<!-- results:start -->");
        doc.ShouldContain("<!-- results:end -->");
        doc.ShouldContain("| date | env | commit | scenario | rps |");
    }
}
