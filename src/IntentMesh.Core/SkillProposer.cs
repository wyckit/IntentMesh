namespace IntentMesh.Core;

/// <summary>What the proposer observed about one skill for a given run.</summary>
public sealed record SkillObservation(SkillInfo Skill, bool MatchedThisRun, string Note);

/// <summary>
/// Emergent-skill observer. It watches the resolved plan and notes when a run exercises a known
/// skill's composition — but it is INSPECTION-ONLY. It never injects nodes, never executes, and
/// never promotes a skill. "Emergence may propose; governance grants authority." A proposed skill
/// stays proposed until a human advances it through the lifecycle (im-skills).
/// </summary>
public sealed class SkillProposer
{
    private readonly IReadOnlyList<SkillInfo> _skills;
    public SkillProposer(IReadOnlyList<SkillInfo> skills) => _skills = skills;

    public IReadOnlyList<SkillObservation> Observe(IReadOnlyList<IntentNode> resolved)
    {
        var planKinds = resolved
            .Where(n => n.TrustSource == TrustSource.User)
            .Select(n => n.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var obs = new List<SkillObservation>();
        foreach (var s in _skills)
        {
            bool matched = s.Composition.Count > 0 && s.Composition.All(planKinds.Contains);
            var note = matched
                ? $"This run exercised the full {s.Label} pattern — a candidate to propose ({s.Status})."
                : $"Not exercised by this run ({s.Composition.Count(planKinds.Contains)}/{s.Composition.Count} steps present).";
            obs.Add(new SkillObservation(s, matched, note));
        }
        return obs;
    }
}
