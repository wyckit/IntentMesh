namespace IntentMesh.Tlm;

// Faithful port of Rsrm.Core.Models (TlmPackage.cs). Field order preserved for
// byte-identical serialization — see the header note in TlmManifest.cs.

public class SymbolicConcept
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Aliases { get; set; } = new();
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class SymbolicRelation
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Strength { get; set; } = 1.0;

    // v2 schema additions — defaults keep v1 records round-tripping cleanly.
    public int SchemaVersion { get; set; } = 1;
    public string SourceTag { get; set; } = string.Empty;

    // v3 schema additions (contrastive groundwork) — null on compiled-graph relations.
    public string? UtteranceText { get; set; }
    public int? TurnIndex { get; set; }

    // v4 schema addition (learned edge-decay) — null preserves legacy behavior.
    public double? Decay { get; set; }
}

public class SymbolicDimension
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double MinValue { get; set; } = 0.0;
    public double MaxValue { get; set; } = 1.0;
}

public class SymbolicPolicy
{
    public string Id { get; set; } = string.Empty;
    public string Rule { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}

public class SymbolicCue
{
    public string Id { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string Signal { get; set; } = string.Empty;
}

public class SymbolicFitSignal
{
    public string Id { get; set; } = string.Empty;
    public double Threshold { get; set; } = 0.8;
}

public class SymbolicGenerator
{
    public string Type { get; set; } = "Grid2D";
    public string Prefix { get; set; } = "Cell-";
    public string Category { get; set; } = "GridCell";
    public int SizeX { get; set; } = 1;
    public int SizeY { get; set; } = 1;
    public bool GenerateAdjacency { get; set; } = true;
    public int WinLength { get; set; } = 3;
    public string? Owner { get; set; }
}

public class TlmPackage
{
    public TlmManifest Manifest { get; set; } = new();
    public List<SymbolicConcept> Concepts { get; set; } = new();
    public List<SymbolicRelation> Relations { get; set; } = new();
    public List<SymbolicDimension> Dimensions { get; set; } = new();
    public List<SymbolicPolicy> Policies { get; set; } = new();
    public List<SymbolicCue> Cues { get; set; } = new();
    public List<SymbolicFitSignal> FitSignals { get; set; } = new();
    public List<SymbolicGenerator> Generators { get; set; } = new();

    /// <summary>
    /// Counts tunable parameters in this package — analogous to weights in a neural network.
    /// Edge strengths (1 per relation), dimensional ranges (2 per dimension),
    /// fit thresholds (1 per signal), policy rules (1 per policy),
    /// and concept properties (1 per key-value pair).
    /// </summary>
    public long ParameterCount =>
        Relations.Count
        + (Dimensions.Count * 2L)
        + FitSignals.Count
        + Policies.Count
        + Concepts.Sum(c => (long)c.Properties.Count);
}
