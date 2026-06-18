using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace IntentMesh.Tlm;

/// <summary>
/// Self-contained TLM compiler/decompiler. Faithful port of
/// Rsrm.Compiler.Services.TlmCompiler: expands declarative generators, stamps
/// the checksum, validates, and writes the canonical envelope + Brotli + compact
/// JSON layout. Deserialize is the decompile path. Output is byte-identical to
/// the real RSRM compiler for the same source package.
/// </summary>
public sealed class TlmCompiler
{
    // WriteIndented=false matches RSRM: halves on-disk bytes pre-Brotli and keeps
    // the serialized form symmetric with what TlmHasher hashes.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly TlmValidator _validator;

    public TlmCompiler() : this(new TlmValidator()) { }

    public TlmCompiler(TlmValidator validator) => _validator = validator;

    /// <summary>
    /// Compile from a source <see cref="TlmPackage"/>, flowing every manifest
    /// metadata field from source → artifact, expanding generators, stamping the
    /// checksum, and validating. This is the sole compile entry point.
    /// </summary>
    public TlmPackage Compile(TlmPackage source)
    {
        var m = source.Manifest.Metadata;

        var finalConcepts = new List<SymbolicConcept>(source.Concepts);
        var finalRelations = new List<SymbolicRelation>(source.Relations);

        foreach (var gen in source.Generators)
            ExpandGenerator(gen, finalConcepts, finalRelations);

        var package = new TlmPackage
        {
            Manifest = new TlmManifest
            {
                Metadata = new TlmMetadata
                {
                    TlmId = m.TlmId,
                    Version = m.Version,
                    Role = m.Role,
                    IsMutable = m.IsMutable,
                    Priority = m.Priority,
                    HotSwapPolicy = m.HotSwapPolicy,
                    StabilityScore = m.StabilityScore
                },
                Imports = source.Manifest.Imports,
                Derives = source.Manifest.Derives,
                // Preserve the source-declared timestamp so compiles are reproducible
                // (byte-identical across runs). Falls back to the manifest default.
                CreatedUtc = source.Manifest.CreatedUtc,
                SchemaVersion = source.Manifest.SchemaVersion
            },
            Concepts = finalConcepts,
            Relations = finalRelations,
            Dimensions = source.Dimensions,
            Policies = source.Policies,
            Cues = source.Cues,
            FitSignals = source.FitSignals,
            Generators = source.Generators
        };

        Finalize(package);

        var validation = _validator.Validate(package);
        foreach (var warning in validation.Warnings)
            Console.Error.WriteLine($"[compile warn] {warning}");
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"TLM '{m.TlmId}' failed post-compile validation: {string.Join("; ", validation.Errors)}");

        return package;
    }

    public void Finalize(TlmPackage package)
        => package.Manifest.Metadata.Checksum = TlmHasher.CalculateChecksum(package);

    public byte[] Serialize(TlmPackage package)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
        using var memoryStream = new MemoryStream();

        Span<byte> headerBuf = stackalloc byte[TlmzEnvelope.HeaderLength];
        TlmzEnvelope.WriteCurrent(headerBuf);
        memoryStream.Write(headerBuf);

        using (var brotli = new BrotliStream(memoryStream, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(jsonBytes, 0, jsonBytes.Length);

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Decompress + deserialize a <c>.tlmz</c> back to a <see cref="TlmPackage"/>.
    /// Sniffs the envelope magic; falls through to legacy raw-Brotli decode when
    /// absent. This is the decompile path.
    /// </summary>
    public TlmPackage Deserialize(byte[] tlmzBytes)
    {
        var envelope = TlmzEnvelope.Peek(tlmzBytes);
        var bodyOffset = envelope.HasValue ? TlmzEnvelope.HeaderLength : 0;
        var bodyLength = tlmzBytes.Length - bodyOffset;

        using var compressed = new MemoryStream(tlmzBytes, bodyOffset, bodyLength, writable: false);
        using var brotli = new BrotliStream(compressed, CompressionMode.Decompress);
        using var decompressed = new MemoryStream();
        brotli.CopyTo(decompressed);
        var json = Encoding.UTF8.GetString(decompressed.ToArray());
        return JsonSerializer.Deserialize<TlmPackage>(json)
            ?? throw new InvalidDataException("Deserialized TlmPackage was null.");
    }

    // ── Declarative generator expansion (Grid2D / GridWinConditions) ──────────
    private static void ExpandGenerator(SymbolicGenerator gen,
        List<SymbolicConcept> concepts, List<SymbolicRelation> relations)
    {
        if (gen.Type == "Grid2D")
        {
            for (int y = 1; y <= gen.SizeY; y++)
                for (int x = 1; x <= gen.SizeX; x++)
                {
                    string id = $"{gen.Prefix}{x}-{y}";
                    var props = new Dictionary<string, string> { { "X", x.ToString() }, { "Y", y.ToString() } };
                    if (!string.IsNullOrEmpty(gen.Owner)) props["Owner"] = gen.Owner;
                    concepts.Add(new SymbolicConcept
                    {
                        Id = id,
                        Label = $"{x},{y}",
                        Category = gen.Category,
                        Description = $"Grid Cell at {x},{y}",
                        Properties = props
                    });

                    if (gen.GenerateAdjacency)
                    {
                        if (x > 1) relations.Add(new SymbolicRelation { SourceId = id, TargetId = $"{gen.Prefix}{x - 1}-{y}", Type = "AdjacentTo", Strength = 1.0 });
                        if (x < gen.SizeX) relations.Add(new SymbolicRelation { SourceId = id, TargetId = $"{gen.Prefix}{x + 1}-{y}", Type = "AdjacentTo", Strength = 1.0 });
                        if (y > 1) relations.Add(new SymbolicRelation { SourceId = id, TargetId = $"{gen.Prefix}{x}-{y - 1}", Type = "AdjacentTo", Strength = 1.0 });
                        if (y < gen.SizeY) relations.Add(new SymbolicRelation { SourceId = id, TargetId = $"{gen.Prefix}{x}-{y + 1}", Type = "AdjacentTo", Strength = 1.0 });
                    }
                }
        }
        else if (gen.Type == "GridWinConditions")
        {
            int winIndex = 1;
            for (int y = 1; y <= gen.SizeY; y++)
                for (int x = 1; x <= gen.SizeX - gen.WinLength + 1; x++)
                {
                    string winId = $"Win-H{winIndex++}";
                    concepts.Add(new SymbolicConcept { Id = winId, Label = winId, Category = "WinCondition", Description = $"Horiz at {x},{y}" });
                    for (int i = 0; i < gen.WinLength; i++)
                        relations.Add(new SymbolicRelation { SourceId = winId, TargetId = $"{gen.Prefix}{x + i}-{y}", Type = "Requires", Strength = 1.0 });
                }
            for (int x = 1; x <= gen.SizeX; x++)
                for (int y = 1; y <= gen.SizeY - gen.WinLength + 1; y++)
                {
                    string winId = $"Win-V{winIndex++}";
                    concepts.Add(new SymbolicConcept { Id = winId, Label = winId, Category = "WinCondition", Description = $"Vert at {x},{y}" });
                    for (int i = 0; i < gen.WinLength; i++)
                        relations.Add(new SymbolicRelation { SourceId = winId, TargetId = $"{gen.Prefix}{x}-{y + i}", Type = "Requires", Strength = 1.0 });
                }
            for (int x = 1; x <= gen.SizeX - gen.WinLength + 1; x++)
                for (int y = 1; y <= gen.SizeY - gen.WinLength + 1; y++)
                {
                    string winId = $"Win-D{winIndex++}";
                    concepts.Add(new SymbolicConcept { Id = winId, Label = winId, Category = "WinCondition", Description = $"DiagDR at {x},{y}" });
                    for (int i = 0; i < gen.WinLength; i++)
                        relations.Add(new SymbolicRelation { SourceId = winId, TargetId = $"{gen.Prefix}{x + i}-{y + i}", Type = "Requires", Strength = 1.0 });
                }
            for (int x = gen.WinLength; x <= gen.SizeX; x++)
                for (int y = 1; y <= gen.SizeY - gen.WinLength + 1; y++)
                {
                    string winId = $"Win-D{winIndex++}";
                    concepts.Add(new SymbolicConcept { Id = winId, Label = winId, Category = "WinCondition", Description = $"DiagDL at {x},{y}" });
                    for (int i = 0; i < gen.WinLength; i++)
                        relations.Add(new SymbolicRelation { SourceId = winId, TargetId = $"{gen.Prefix}{x - i}-{y + i}", Type = "Requires", Strength = 1.0 });
                }
        }
    }
}
