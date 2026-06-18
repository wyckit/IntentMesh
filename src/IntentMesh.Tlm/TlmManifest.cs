namespace IntentMesh.Tlm;

// ─────────────────────────────────────────────────────────────────────────────
// Faithful port of Rsrm.Core.Models (TlmManifest.cs). Property *declaration
// order* and types are preserved EXACTLY so that System.Text.Json produces
// byte-identical output to the real RSRM build. That byte-identity is what
// makes the SHA-256 checksum match, which in turn lets these standalone .tlmz
// artifacts load and validate inside the live RSRM runtime unchanged.
// ─────────────────────────────────────────────────────────────────────────────

public enum TlmRole
{
    Foundation,
    Logic,
    Interface,
    Learning,
    Analysis,
    Policy,
    Overlay
}

public enum HotSwapPolicy
{
    None,
    Safe,
    Transactional
}

public class TlmMetadata
{
    public string TlmId { get; set; } = string.Empty;
    public bool IsMutable { get; set; }
    public TlmRole Role { get; set; }
    public int Priority { get; set; }
    public string Version { get; set; } = "0.1.0";
    public string Checksum { get; set; } = string.Empty;
    public HotSwapPolicy HotSwapPolicy { get; set; } = HotSwapPolicy.Safe;
    public double StabilityScore { get; set; }
}

public class TlmManifest
{
    /// <summary>
    /// The schema version string produced by this build of the compiler.
    /// Used by <see cref="TlmValidator"/> to bounds-check incoming manifests
    /// (SchemaVersion must be parseable, &gt; 0, and ≤ current).
    /// </summary>
    public const string CurrentSchemaVersion = "1.0";

    public TlmMetadata Metadata { get; set; } = new();
    public List<string> Imports { get; set; } = new();
    public List<string> Derives { get; set; } = new();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string SchemaVersion { get; set; } = "1.0";
}
