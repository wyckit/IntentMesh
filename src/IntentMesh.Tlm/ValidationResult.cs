namespace IntentMesh.Tlm;

// Faithful port of Rsrm.Core.Models.ValidationResult / TlmHealthMetrics.

public class ValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public TlmHealthMetrics? HealthMetrics { get; set; }
}

/// <summary>
/// Quantified ontology-health signals produced by TlmValidator.HealthCheck.
/// Rates are in [0.0, 1.0]. Rate is zero when the denominator is zero.
/// </summary>
public class TlmHealthMetrics
{
    public double OrphanRate { get; set; }
    public double DuplicateConceptRate { get; set; }
    public double RelationSparsity { get; set; }
    public double SyntheticFraction { get; set; }
    public int TotalConcepts { get; set; }
    public int TotalRelations { get; set; }
}
