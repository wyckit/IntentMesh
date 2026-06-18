namespace IntentMesh.Tlm;

/// <summary>
/// Port of Rsrm.Compiler.Services.TlmValidator (validate + health-check paths).
/// Hot-swap-compatibility and import-cycle checks are omitted — they are runtime
/// concerns, not dataset-authoring concerns. Checksum, metadata, schema-version,
/// and the full health/dangling/orphan/sparsity battery are preserved so a TLM
/// that validates here validates identically inside RSRM.
/// </summary>
public sealed class TlmValidator
{
    /// <summary>The provenance property key RSRM uses for synthetic-fraction tagging.</summary>
    public const string SyntheticFractionKey = "_syntheticFraction";

    /// <summary>
    /// When RSRM_STRICT_DANGLING=1, dangling-relation warnings are promoted to
    /// errors (matches RSRM's CI gate). Off by default.
    /// </summary>
    public static bool StrictDanglingEnabled =>
        Environment.GetEnvironmentVariable("RSRM_STRICT_DANGLING") == "1";

    public ValidationResult Validate(TlmPackage package)
    {
        var result = new ValidationResult { IsValid = true };

        // 1. Checksum self-consistency.
        var expectedChecksum = TlmHasher.CalculateChecksum(package);
        if (package.Manifest.Metadata.Checksum != expectedChecksum)
        {
            result.IsValid = false;
            result.Errors.Add($"Checksum mismatch. Expected: {expectedChecksum}, Actual: {package.Manifest.Metadata.Checksum}");
        }

        // 2. Metadata.
        if (string.IsNullOrWhiteSpace(package.Manifest.Metadata.TlmId))
        {
            result.IsValid = false;
            result.Errors.Add("TlmId is required.");
        }

        // 2b. SchemaVersion bounds check.
        var schemaVersionStr = package.Manifest.SchemaVersion;
        if (string.IsNullOrWhiteSpace(schemaVersionStr))
        {
            result.Warnings.Add($"SchemaVersion is empty; treating as legacy artifact (implicit '{TlmManifest.CurrentSchemaVersion}').");
        }
        else if (!Version.TryParse(schemaVersionStr, out var schemaVer))
        {
            result.IsValid = false;
            result.Errors.Add($"SchemaVersion '{schemaVersionStr}' is not a valid version string.");
        }
        else
        {
            var currentVer = Version.Parse(TlmManifest.CurrentSchemaVersion);
            if (schemaVer <= new Version(0, 0))
            {
                result.IsValid = false;
                result.Errors.Add($"SchemaVersion '{schemaVersionStr}' is 0 or unset and is not valid.");
            }
            else if (schemaVer > currentVer)
            {
                result.IsValid = false;
                result.Errors.Add($"SchemaVersion '{schemaVersionStr}' exceeds the current supported version '{TlmManifest.CurrentSchemaVersion}'.");
            }
        }

        // 3. Health check (non-blocking warnings + metrics).
        var health = HealthCheck(package);
        result.Warnings.AddRange(health.Warnings);
        result.HealthMetrics = health.HealthMetrics;
        if (!health.IsValid) result.IsValid = false; // strict-dangling escalation

        return result;
    }

    public ValidationResult HealthCheck(TlmPackage package)
    {
        var result = new ValidationResult { IsValid = true };
        var tlmId = package.Manifest.Metadata.TlmId;

        int whitespaceGarbageCount = 0, emptyLabelCount = 0, longLabelCount = 0, duplicateIdCount = 0;
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var concept in package.Concepts)
        {
            if (!seenIds.Add(concept.Id))
            {
                duplicateIdCount++;
                if (duplicateIdCount <= 3) result.Warnings.Add($"[{tlmId}] Duplicate concept ID: '{concept.Id}'");
            }

            if (string.IsNullOrWhiteSpace(concept.Label)) { emptyLabelCount++; continue; }

            var label = concept.Label;
            int wsCount = label.Count(char.IsWhiteSpace);
            int contentLen = label.Length - wsCount;
            if (label.Length > 20 && contentLen > 0 && (double)wsCount / label.Length > 0.5)
            {
                whitespaceGarbageCount++;
                if (whitespaceGarbageCount <= 3)
                    result.Warnings.Add($"[{tlmId}] Whitespace-heavy label ({wsCount}/{label.Length} chars ws): '{label[..Math.Min(60, label.Length)].Trim()}...'");
            }

            if (label.Length > 120)
            {
                longLabelCount++;
                if (longLabelCount <= 3)
                    result.Warnings.Add($"[{tlmId}] Label too long ({label.Length} chars): '{label[..60].Trim()}...'");
            }
        }

        if (whitespaceGarbageCount > 3) result.Warnings.Add($"[{tlmId}] {whitespaceGarbageCount} total concepts with whitespace-heavy labels");
        if (emptyLabelCount > 0) result.Warnings.Add($"[{tlmId}] {emptyLabelCount} concepts with empty labels");
        if (longLabelCount > 3) result.Warnings.Add($"[{tlmId}] {longLabelCount} total concepts with labels > 120 chars");
        if (duplicateIdCount > 3) result.Warnings.Add($"[{tlmId}] {duplicateIdCount} total duplicate concept IDs");

        // Concept density.
        if (package.Concepts.Count > 10 && package.Relations.Count > 0)
        {
            double ratio = (double)package.Relations.Count / package.Concepts.Count;
            if (ratio < 0.5)
                result.Warnings.Add($"[{tlmId}] Sparse graph: {package.Relations.Count} relations / {package.Concepts.Count} concepts (ratio {ratio:F2})");
        }
        else if (package.Concepts.Count > 10 && package.Relations.Count == 0)
        {
            result.Warnings.Add($"[{tlmId}] No relations — {package.Concepts.Count} disconnected concepts");
        }

        // Orphan detection.
        if (package.Concepts.Count > 5 && package.Relations.Count > 0)
        {
            var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in package.Relations) { referencedIds.Add(rel.SourceId); referencedIds.Add(rel.TargetId); }
            int orphanCount = package.Concepts.Count(c => !referencedIds.Contains(c.Id));
            if (orphanCount > 0 && (double)orphanCount / package.Concepts.Count > 0.3)
                result.Warnings.Add($"[{tlmId}] {orphanCount}/{package.Concepts.Count} concepts are orphaned (not in any relation)");
        }

        // Dangling relations.
        var allConceptIds = new HashSet<string>(package.Concepts.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        int danglingCount = 0;
        var danglingSamples = new List<string>();
        foreach (var rel in package.Relations)
        {
            if (!allConceptIds.Contains(rel.SourceId) || !allConceptIds.Contains(rel.TargetId))
            {
                danglingCount++;
                if (danglingSamples.Count < 5) danglingSamples.Add($"{rel.SourceId}--[{rel.Type}]-->{rel.TargetId}");
            }
        }
        if (danglingCount > 0)
        {
            var msg = $"[{tlmId}] {danglingCount} relations reference non-existent concept IDs (e.g. {string.Join("; ", danglingSamples)})";
            if (StrictDanglingEnabled) { result.IsValid = false; result.Errors.Add(msg); }
            else result.Warnings.Add(msg);
        }

        if (package.Concepts.Count == 0) result.Warnings.Add($"[{tlmId}] Empty package — no concepts");
        else if (package.Concepts.Count < 3) result.Warnings.Add($"[{tlmId}] Very small package — only {package.Concepts.Count} concepts");

        // Synthetic-fraction provenance check.
        double totalSynthetic = 0; int provenanceTagged = 0;
        foreach (var concept in package.Concepts)
        {
            if (concept.Properties.TryGetValue(SyntheticFractionKey, out var raw) &&
                double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var frac))
            { totalSynthetic += frac; provenanceTagged++; }
        }
        double meanSyntheticFraction = provenanceTagged > 0 ? totalSynthetic / provenanceTagged : 0;
        if (meanSyntheticFraction > 0.5)
            result.Warnings.Add($"[{tlmId}] High synthetic fraction: {meanSyntheticFraction:F2} across {provenanceTagged} tagged concepts — risk of synthetic-data collapse");

        result.HealthMetrics = new TlmHealthMetrics
        {
            TotalConcepts = package.Concepts.Count,
            TotalRelations = package.Relations.Count,
            OrphanRate = ComputeOrphanRate(package),
            DuplicateConceptRate = package.Concepts.Count > 0 ? (double)duplicateIdCount / package.Concepts.Count : 0,
            RelationSparsity = ComputeRelationSparsity(package),
            SyntheticFraction = meanSyntheticFraction
        };

        return result;
    }

    private static double ComputeOrphanRate(TlmPackage package)
    {
        if (package.Concepts.Count == 0 || package.Relations.Count == 0) return 0;
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in package.Relations) { referencedIds.Add(rel.SourceId); referencedIds.Add(rel.TargetId); }
        int orphanCount = package.Concepts.Count(c => !referencedIds.Contains(c.Id));
        return (double)orphanCount / package.Concepts.Count;
    }

    private static double ComputeRelationSparsity(TlmPackage package)
    {
        if (package.Concepts.Count == 0) return 0;
        double density = Math.Min(1.0, (double)package.Relations.Count / package.Concepts.Count);
        return 1.0 - density;
    }
}
