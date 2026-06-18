using System.Text.Json;
using IntentMesh.Tlm;
using IntentMesh.Tlm.Cli;

// ─────────────────────────────────────────────────────────────────────────────
// tlm — compiler/decompiler for the IntentMesh agentic TLM bundle (im-*).
// Uses the PassGen.Tlm library so .tlmz are byte-compatible with live RSRM / sage-rsrm.
//
//   tlm author                 author source/*.source.json from BundleAuthor
//   tlm compile   [name|all]   source/*.source.json -> compiled/*.tlmz
//   tlm decompile [name|all]   compiled/*.tlmz      -> decompiled/*.json
//   tlm validate  [name|all]   checksum + health over compiled/*.tlmz
//   tlm verify                 full lossless round-trip integrity over all
//   tlm list                   list the bundle's source TLMs
//   tlm stats     [name|all]   concept/relation/parameter counts
//
// --root <dir> overrides dataset-root autodetection (nearest `dataset/` up from CWD).
// ─────────────────────────────────────────────────────────────────────────────

var (command, target, root) = ParseArgs(args);
if (command is null) { PrintUsage(); return 1; }

var datasetRoot = ResolveRoot(root);
var sourceDir = Path.Combine(datasetRoot, "source");
var compiledDir = Path.Combine(datasetRoot, "compiled");
var decompiledDir = Path.Combine(datasetRoot, "decompiled");
Directory.CreateDirectory(sourceDir);
Directory.CreateDirectory(compiledDir);
Directory.CreateDirectory(decompiledDir);

var compiler = new TlmCompiler();
var validator = new TlmValidator();
var indented = new JsonSerializerOptions { WriteIndented = true };

Console.WriteLine($"dataset root: {datasetRoot}");

try
{
    return command switch
    {
        "author" => Author(),
        "compile" => Compile(target),
        "decompile" => Decompile(target),
        "validate" => Validate(target),
        "verify" => Verify(),
        "list" => List(),
        "stats" => Stats(target),
        _ => Unknown(command),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    return 1;
}

int Author()
{
    Console.WriteLine($"Authoring bundle sources -> {sourceDir}");
    BundleAuthor.Author(sourceDir);
    Console.WriteLine("Authored. Run `tlm compile all` next.");
    return 0;
}

int Compile(string? name)
{
    var sources = ResolveSources(name);
    if (sources.Count == 0) { Console.Error.WriteLine("No matching source files. Run `tlm author` first."); return 1; }
    int ok = 0, fail = 0;
    foreach (var src in sources)
    {
        var baseName = BaseName(src);
        try
        {
            var pkg = LoadSource(src);
            var artifact = compiler.Compile(pkg);
            var bytes = compiler.Serialize(artifact);
            File.WriteAllBytes(Path.Combine(compiledDir, $"{baseName}.tlmz"), bytes);
            Console.WriteLine($"  [OK]   {baseName,-22} {artifact.Concepts.Count,4}c {artifact.Relations.Count,4}r  {bytes.Length,6} B  {artifact.Manifest.Metadata.Checksum[..12]}");
            ok++;
        }
        catch (Exception ex) { Console.Error.WriteLine($"  [FAIL] {baseName}: {ex.Message}"); fail++; }
    }
    Console.WriteLine($"Compiled: {ok} ok, {fail} failed.");
    return fail == 0 ? 0 : 1;
}

int Decompile(string? name)
{
    var artifacts = ResolveCompiled(name);
    if (artifacts.Count == 0) { Console.Error.WriteLine("No matching .tlmz files. Run `tlm compile all` first."); return 1; }
    int ok = 0, fail = 0;
    foreach (var path in artifacts)
    {
        var baseName = Path.GetFileNameWithoutExtension(path);
        try
        {
            var pkg = compiler.Deserialize(File.ReadAllBytes(path));
            File.WriteAllText(Path.Combine(decompiledDir, $"{baseName}.decompiled.json"), JsonSerializer.Serialize(pkg, indented));
            Console.WriteLine($"  [OK]   {baseName}.tlmz -> {baseName}.decompiled.json ({pkg.Concepts.Count}c, {pkg.Relations.Count}r)");
            ok++;
        }
        catch (Exception ex) { Console.Error.WriteLine($"  [FAIL] {baseName}: {ex.Message}"); fail++; }
    }
    Console.WriteLine($"Decompiled: {ok} ok, {fail} failed.");
    return fail == 0 ? 0 : 1;
}

int Validate(string? name)
{
    var artifacts = ResolveCompiled(name);
    if (artifacts.Count == 0) { Console.Error.WriteLine("No matching .tlmz files."); return 1; }
    int valid = 0, invalid = 0;
    foreach (var path in artifacts)
    {
        var baseName = Path.GetFileNameWithoutExtension(path);
        var pkg = compiler.Deserialize(File.ReadAllBytes(path));
        var res = validator.Validate(pkg);
        if (res.IsValid)
        {
            Console.WriteLine($"  VALID    {baseName,-22} {pkg.Concepts.Count}c, {pkg.Relations.Count}r" + (res.Warnings.Count > 0 ? $"  ({res.Warnings.Count} warnings)" : ""));
            valid++;
        }
        else { Console.WriteLine($"  INVALID  {baseName}"); foreach (var e in res.Errors) Console.WriteLine($"             error: {e}"); invalid++; }
        foreach (var w in res.Warnings) Console.WriteLine($"             warn:  {w}");
    }
    Console.WriteLine($"Validated: {valid} valid, {invalid} invalid.");
    return invalid == 0 ? 0 : 1;
}

int Verify()
{
    var sources = ResolveSources(null);
    if (sources.Count == 0) { Console.Error.WriteLine("No source files."); return 1; }
    int pass = 0, failCount = 0;
    long totalConcepts = 0, totalRelations = 0, totalParams = 0;
    Console.WriteLine("Round-trip integrity (source -> compile -> decompile -> recompress == identity):");
    foreach (var src in sources)
    {
        var baseName = BaseName(src);
        try
        {
            var pkg = LoadSource(src);
            var artifact = compiler.Compile(pkg);
            var bytesA = compiler.Serialize(artifact);
            var pkgB = compiler.Deserialize(bytesA);
            var bytesA2 = compiler.Serialize(pkgB);
            bool bytesIdentical = bytesA.AsSpan().SequenceEqual(bytesA2);
            bool checksumValid = validator.Validate(pkgB).IsValid;
            bool checksumStable = pkgB.Manifest.Metadata.Checksum == TlmHasher.CalculateChecksum(pkgB);
            if (bytesIdentical && checksumValid && checksumStable)
            {
                Console.WriteLine($"  PASS  {baseName,-22} {bytesA.Length,6} B  checksum {artifact.Manifest.Metadata.Checksum[..12]}");
                totalConcepts += artifact.Concepts.Count; totalRelations += artifact.Relations.Count; totalParams += artifact.ParameterCount; pass++;
            }
            else { Console.WriteLine($"  FAIL  {baseName}: bytesIdentical={bytesIdentical} checksumValid={checksumValid} checksumStable={checksumStable}"); failCount++; }
        }
        catch (Exception ex) { Console.WriteLine($"  FAIL  {baseName}: {ex.Message}"); failCount++; }
    }
    Console.WriteLine();
    Console.WriteLine($"Round-trip: {pass} pass, {failCount} fail.");
    Console.WriteLine($"Bundle totals: {totalConcepts} concepts, {totalRelations} relations, {totalParams} parameters across {sources.Count} TLMs.");
    return failCount == 0 ? 0 : 1;
}

int List()
{
    var sources = ResolveSources(null);
    Console.WriteLine($"Source TLMs ({sources.Count}):");
    foreach (var src in sources)
    {
        var baseName = BaseName(src);
        var pkg = LoadSource(src);
        var m = pkg.Manifest.Metadata;
        var compiled = File.Exists(Path.Combine(compiledDir, $"{baseName}.tlmz"));
        Console.WriteLine($"  {(compiled ? "[OK]" : "[--]")} {m.TlmId,-22} v{m.Version}  {m.Role,-11} prio {m.Priority,-4} {pkg.Concepts.Count,4}c {pkg.Relations.Count,4}r" +
                          (pkg.Manifest.Imports.Count > 0 ? $"  imports: {string.Join(",", pkg.Manifest.Imports)}" : ""));
    }
    return 0;
}

int Stats(string? name)
{
    foreach (var src in ResolveSources(name))
    {
        var pkg = LoadSource(src);
        var cats = pkg.Concepts.GroupBy(c => c.Category).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}({g.Count()})");
        var rels = pkg.Relations.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}({g.Count()})");
        Console.WriteLine($"{pkg.Manifest.Metadata.TlmId}:");
        Console.WriteLine($"  concepts={pkg.Concepts.Count} relations={pkg.Relations.Count} params={pkg.ParameterCount}");
        Console.WriteLine($"  categories: {string.Join(", ", cats)}");
        Console.WriteLine($"  relation types: {string.Join(", ", rels)}");
        if (pkg.Policies.Count > 0 || pkg.Cues.Count > 0) Console.WriteLine($"  policies={pkg.Policies.Count} cues={pkg.Cues.Count}");
    }
    return 0;
}

int Unknown(string cmd) { Console.Error.WriteLine($"Unknown command: {cmd}"); PrintUsage(); return 1; }

TlmPackage LoadSource(string path)
    => JsonSerializer.Deserialize<TlmPackage>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Failed to parse {Path.GetFileName(path)}");

List<string> ResolveSources(string? name)
{
    if (string.IsNullOrEmpty(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
        return Directory.GetFiles(sourceDir, "*.source.json").OrderBy(f => f).ToList();
    var file = Path.Combine(sourceDir, name.EndsWith(".source.json") ? name : $"{name}.source.json");
    return File.Exists(file) ? new List<string> { file } : new List<string>();
}

List<string> ResolveCompiled(string? name)
{
    if (string.IsNullOrEmpty(name) || name.Equals("all", StringComparison.OrdinalIgnoreCase))
        return Directory.GetFiles(compiledDir, "*.tlmz").OrderBy(f => f).ToList();
    var file = Path.Combine(compiledDir, name.EndsWith(".tlmz") ? name : $"{name}.tlmz");
    return File.Exists(file) ? new List<string> { file } : new List<string>();
}

static string BaseName(string sourcePath) => Path.GetFileNameWithoutExtension(sourcePath).Replace(".source", "");

static (string? cmd, string? target, string? root) ParseArgs(string[] a)
{
    string? cmd = null, target = null, root = null;
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i] == "--root" && i + 1 < a.Length) { root = a[++i]; continue; }
        if (cmd is null) cmd = a[i].ToLowerInvariant();
        else if (target is null) target = a[i];
    }
    return (cmd, target, root);
}

static string ResolveRoot(string? explicitRoot)
{
    if (!string.IsNullOrEmpty(explicitRoot)) return Path.GetFullPath(explicitRoot);
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "dataset");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    return Path.GetFullPath("dataset");
}

static void PrintUsage() => Console.WriteLine("""
    tlm — IntentMesh agentic TLM bundle compiler (byte-compatible with RSRM / sage-rsrm)

    Usage:
      tlm author                 author source/*.source.json from the BundleAuthor
      tlm compile   [name|all]   compile source/*.source.json -> compiled/*.tlmz
      tlm decompile [name|all]   decompile compiled/*.tlmz    -> decompiled/*.json
      tlm validate  [name|all]   checksum + health validation
      tlm verify                 full lossless round-trip integrity check
      tlm list                   list source TLMs
      tlm stats     [name|all]   concept/relation/parameter breakdown

    Options:
      --root <dir>   dataset root (default: nearest `dataset/` walking up from CWD)
    """);
