using System.Net;
using System.Net.Http.Json;
using LLMRouter.Core.Skills;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace LLMRouter.Tests;

/// <summary>SPEC-058: detect/collect/run de skills + executions.</summary>
public class Spec058SkillsTests : WebAppTestBase
{
    private static async Task<string> MakeSkillDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"skillfx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "SKILL.md"),
            "---\nname: fx-skill\ndescription: fixture\ncommand: echo hello-fx\n---\nbody\n");
        return dir;
    }

    [Fact]
    public async Task Detect_encontra_skill_fora_do_store()
    {
        var dir = await MakeSkillDir();
        await LoginAsync();
        var r = await Client.PostAsJsonAsync("/api/skills/collect/detect", new { roots = new[] { dir } });
        r.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await r.Content.ReadAsStringAsync()).ShouldContain("fx-skill");
    }

    [Fact]
    public void Collect_importa_para_o_store()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}");
        var src = MakeSkillDir().Result;
        var (ok, detail) = SkillsExtras.Collect(src, dest);
        ok.ShouldBeTrue(detail);
        File.Exists(Path.Combine(detail, "SKILL.md")).ShouldBeTrue();
        SkillsExtras.Collect(src, dest).ok.ShouldBeFalse("already in store");
    }

    [Fact]
    public async Task Run_executa_command_e_grava_execution()
    {
        var store = Path.Combine(Path.GetTempPath(), $"store-{Guid.NewGuid():N}");
        var src = await MakeSkillDir();
        SkillsExtras.Collect(src, store);
        var id = Path.GetFileName(src.TrimEnd('/'));

        await using var db = Db();
        var (ok, detail) = await SkillsExtras.RunAsync(db, store, id);
        ok.ShouldBeTrue(detail);
        var exec = await db.SkillExecutions.FirstAsync();
        exec.Skill.ShouldBe(id);
        exec.ExitCode.ShouldBe(0);
        exec.Stdout.ShouldContain("hello-fx");
    }
}
