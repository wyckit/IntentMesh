using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Tlm;

/// <summary>
/// Verbatim port of Rsrm.Core.Utilities.TlmHasher. The checksum is the SHA-256
/// of the compact JSON of an anonymous package projection with the stored
/// checksum cleared. Because the model classes are byte-faithful ports, this
/// produces the exact same hex digest RSRM computes — which is why artifacts
/// emitted by this standalone tool pass RSRM's load-time checksum validation.
/// </summary>
public static class TlmHasher
{
    public static string CalculateChecksum(TlmPackage package)
    {
        // Clone manifest to avoid side effects and clear checksum for calculation.
        var manifestCopy = new TlmManifest
        {
            Metadata = new TlmMetadata
            {
                TlmId = package.Manifest.Metadata.TlmId,
                Version = package.Manifest.Metadata.Version,
                Role = package.Manifest.Metadata.Role,
                IsMutable = package.Manifest.Metadata.IsMutable,
                Priority = package.Manifest.Metadata.Priority,
                HotSwapPolicy = package.Manifest.Metadata.HotSwapPolicy,
                Checksum = string.Empty,
                StabilityScore = package.Manifest.Metadata.StabilityScore
            },
            Imports = new List<string>(package.Manifest.Imports),
            Derives = new List<string>(package.Manifest.Derives),
            CreatedUtc = package.Manifest.CreatedUtc,
            SchemaVersion = package.Manifest.SchemaVersion
        };

        var packageCopy = new
        {
            Manifest = manifestCopy,
            Concepts = package.Concepts,
            Relations = package.Relations,
            Dimensions = package.Dimensions,
            Policies = package.Policies,
            Cues = package.Cues,
            FitSignals = package.FitSignals,
            Generators = package.Generators
        };

        var json = JsonSerializer.Serialize(packageCopy);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
