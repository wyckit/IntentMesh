using IntentMesh.Core;
using Xunit;

namespace IntentMesh.Tests;

/// <summary>
/// Tests the emergent-skill lifecycle: skills load from im-skills, the proposer observes when a
/// run exercises a skill's composition, and — the governance invariant — observation never grants
/// authority: no executable node is injected and the skill stays in its declared lifecycle state.
/// </summary>
public sealed class SkillTests
{
    private static IntentMeshRuntime Runtime() => IntentMeshRuntime.Load();
    private const string Prompt1 = "Plan my Friday, move anything flexible, book an hour for the gym, and draft Sarah the meeting notes.";
    private const string Prompt2 = "Clean up my downloads and delete anything that looks like junk.";

    [Fact]
    public void Skills_and_lifecycle_load_from_the_tlm()
    {
        var b = Runtime().Bundle;
        var skill = Assert.Single(b.Skills, s => s.Id == "skill-daily-planning-followup");
        Assert.Equal("proposed", skill.Status);
        Assert.Equal(5, skill.Composition.Count);
        Assert.Contains(Kinds.DraftEmail, skill.Composition);

        Assert.Equal(7, b.Lifecycle.Count);
        Assert.Equal("observed", b.Lifecycle[0].Label);
        Assert.Equal("removed", b.Lifecycle[^1].Label);
    }

    [Fact]
    public void Skill_is_observed_when_the_run_exercises_its_full_composition()
    {
        var r = Runtime().Run(Prompt1, Workspace.CreateDemo());
        var item = Assert.Single(r.Skills.Items, s => s.Id == "skill-daily-planning-followup");
        Assert.True(item.MatchedThisRun);
        Assert.Equal("proposed", item.Status);   // observation does NOT promote it
        Assert.Equal(7, r.Skills.Lifecycle.Count);
    }

    [Fact]
    public void Skill_is_not_observed_when_the_run_does_not_match()
    {
        var r = Runtime().Run(Prompt2, Workspace.CreateDemo());
        var item = Assert.Single(r.Skills.Items, s => s.Id == "skill-daily-planning-followup");
        Assert.False(item.MatchedThisRun);   // downloads cleanup ≠ daily planning
    }

    [Fact]
    public void Observing_a_skill_injects_no_executable_nodes()
    {
        // The Friday plan has exactly its five user action nodes — the matched skill adds none.
        var r = Runtime().Run(Prompt1, Workspace.CreateDemo());
        Assert.Equal(5, r.Nodes.Count(n => n.TrustSource == "User"));
        Assert.DoesNotContain(r.Nodes, n => n.Type.StartsWith("skill"));
    }
}
