using System.Text.Json;
using IntentMesh.Core;
using IntentMesh.Tlm;

namespace IntentMesh.Integrations;

// ──────────────────────────────────────────────────────────────────────────────
// OpenApiImporter — wraps an external tool / OpenAPI-style operation schema in
//                   a typed IntentMesh contract descriptor.
//
// WHAT IS REAL:
//   • The ToolSchema record models a minimal OpenAPI/tool-schema description
//     (name, method, summary, parameters, a risk/side-effect hint).
//   • ToContract() applies deterministic mapping rules to produce an
//     ImportedContract — the same shape as a ContractInfo in the bundle.
//   • The RequiresConfirmation heuristic (POST/DELETE/PATCH + side-effect hint)
//     is real and matches the bundle's existing risk conventions.
//   • The static SampleInvoiceSchema demonstrates a real "create_invoice" POST
//     operation flowing through the importer.
//
// NOW REAL (converted from the Phase 5 prototype stubs):
//   • ParseFromOpenApi() parses a real OpenAPI 3.x document — JSON or YAML (via the
//     dependency-free MiniYaml converter) — with the built-in System.Text.Json reader
//     (no Microsoft.OpenApi dependency). Paths × methods → ToolSchema; fields from
//     parameters + request-body properties; local $ref pointers (#/components/...) are
//     resolved for both parameters and request-body schemas.
//   • Semantic inference: SideEffect, risk, and capability are derived from the
//     operation's id/summary/tags keywords (e.g. "send"/"email" → email-send + email
//     capability; "delete"/"refund" → high risk), not just the HTTP method.
//   • RegisterToCompiledDir() compiles the imported contracts into a real
//     im-imported.tlmz using the TLM compiler; SymbolicBundle.Load() then recognizes
//     each Kind and the PolicyGate / Translation-Drift guard enforce it. Import →
//     usable typed contract, end-to-end.
//
// SUPPORTED YAML SUBSET (MiniYaml): block mappings/sequences, scalars, |/> block
//   scalars, inline [a,b]/{} flow. Not full YAML 1.2 (no anchors/aliases/tags); remote
//   ($ref to other files/URLs) is out of scope — local document refs only.
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A minimal representation of an external tool or OpenAPI operation schema —
/// the input to the importer.
///
/// <para>
/// In a production integration this record would be populated by parsing a real
/// OpenAPI YAML/JSON spec (see <see cref="OpenApiImporter.ParseFromOpenApi"/>
/// stub). Here it is constructed directly to illustrate the data model.
/// </para>
/// </summary>
/// <param name="Name">
/// The canonical tool/operation name (e.g., <c>create_invoice</c>).
/// </param>
/// <param name="Method">
/// HTTP method or equivalent (e.g., <c>POST</c>, <c>GET</c>, <c>DELETE</c>).
/// </param>
/// <param name="Summary">
/// Human-readable description of what the operation does.
/// </param>
/// <param name="Parameters">
/// Ordered list of parameter names the operation accepts (no type info in this
/// prototype — a real parser would include types and required flags).
/// </param>
/// <param name="RiskHint">
/// Caller-supplied risk level override: <c>low</c>, <c>medium</c>, or
/// <c>high</c>. When empty, the importer infers risk from <paramref name="Method"/>
/// and <paramref name="SideEffectHint"/>.
/// </param>
/// <param name="SideEffectHint">
/// Short description of the side effect, e.g., <c>financial-write</c>,
/// <c>email-send</c>, <c>file-delete</c>. Used to set
/// <see cref="ImportedContract.SideEffect"/> and derive
/// <see cref="ImportedContract.RequiresConfirmation"/>.
/// </param>
public sealed record ToolSchema(
    string Name,
    string Method,
    string Summary,
    IReadOnlyList<string> Parameters,
    string RiskHint = "",
    string SideEffectHint = "");

/// <summary>
/// An IntentMesh typed-contract descriptor produced by importing an external
/// tool schema. This is the prototype equivalent of a <c>ContractInfo</c> from
/// the TLM bundle — it describes the contract boundary an action must satisfy.
///
/// <para>
/// <strong>Not yet registered:</strong> in this prototype, <c>ImportedContract</c>
/// is a plain data record. It is not yet compiled into a <c>.tlmz</c> or
/// injected into a live <see cref="SymbolicBundle"/>. See
/// <see cref="OpenApiImporter.RegisterInBundle"/> stub for the next step.
/// </para>
/// </summary>
/// <param name="Kind">
/// The IntentMesh action kind id that would be registered (e.g.,
/// <c>act-create-invoice</c>). Derived from the tool name by the importer.
/// </param>
/// <param name="Risk">
/// Risk level: <c>low</c>, <c>medium</c>, or <c>high</c>.
/// </param>
/// <param name="SideEffect">
/// Side-effect category (e.g., <c>financial-write</c>, <c>none</c>).
/// </param>
/// <param name="Fields">
/// The parameter names exposed as typed contract fields.
/// </param>
/// <param name="RequiresConfirmation">
/// <c>true</c> when the action carries a non-trivial side effect (POST/DELETE/
/// PATCH with a non-none side-effect hint) and therefore requires user approval
/// before execution, matching IntentMesh's existing confirmation model.
/// </param>
/// <param name="Capability">
/// The capability the action requires (e.g., <c>email</c>, <c>billing</c>, <c>filesystem</c>),
/// inferred from the operation's semantics. Empty when no specific capability is implied. Mirrors
/// the bundle's capability scoping: the PolicyGate blocks the action unless this capability is
/// granted.
/// </param>
public sealed record ImportedContract(
    string Kind,
    string Risk,
    string SideEffect,
    string[] Fields,
    bool RequiresConfirmation,
    string Capability = "");

/// <summary>
/// Imports external tool / OpenAPI-style operation schemas and emits typed
/// IntentMesh contract descriptors (<see cref="ImportedContract"/>).
///
/// <para>
/// The core method is <see cref="ToContract"/>, which deterministically maps a
/// <see cref="ToolSchema"/> to an <see cref="ImportedContract"/>. This is
/// sufficient to prototype the integration layer; production use requires the
/// additional steps described in the class-level stub comments above.
/// </para>
/// </summary>
public static class OpenApiImporter
{
    // ── Sample schema (real data, hand-authored) ──────────────────────────────
    /// <summary>
    /// A sample tool schema for a "create_invoice" POST operation, demonstrating
    /// the importer with a realistic financial-write side effect.
    ///
    /// <para>
    /// In production this would be parsed from an OpenAPI spec — see
    /// <see cref="ParseFromOpenApi"/>. Here it is constructed directly so the
    /// prototype can run without external dependencies.
    /// </para>
    /// </summary>
    public static readonly ToolSchema SampleInvoiceSchema = new(
        Name: "create_invoice",
        Method: "POST",
        Summary: "Create a new invoice for a customer in the billing system.",
        Parameters: new[] { "customer_id", "amount_cents", "currency", "due_date", "line_items" },
        RiskHint: "medium",
        SideEffectHint: "financial-write");

    /// <summary>
    /// A sample tool schema for a "get_customer" GET operation, demonstrating
    /// the importer for a read-only, low-risk, no-confirmation operation.
    /// </summary>
    public static readonly ToolSchema SampleGetCustomerSchema = new(
        Name: "get_customer",
        Method: "GET",
        Summary: "Retrieve customer details by id — read-only, no side effects.",
        Parameters: new[] { "customer_id" },
        RiskHint: "low",
        SideEffectHint: "none");

    // ── Core mapping ──────────────────────────────────────────────────────────
    /// <summary>
    /// Converts a <see cref="ToolSchema"/> to an <see cref="ImportedContract"/>.
    ///
    /// <para>
    /// Mapping rules (deterministic, no LLM):
    /// <list type="bullet">
    ///   <item><strong>Kind</strong>: <c>act-{name}</c> (hyphenated, lower-case).</item>
    ///   <item><strong>Risk</strong>: caller's <c>RiskHint</c> if non-empty;
    ///         otherwise <c>high</c> for DELETE, <c>medium</c> for POST/PATCH,
    ///         <c>low</c> for GET/HEAD.</item>
    ///   <item><strong>SideEffect</strong>: caller's <c>SideEffectHint</c> if
    ///         non-empty; otherwise <c>none</c>.</item>
    ///   <item><strong>Fields</strong>: the <c>Parameters</c> list verbatim.</item>
    ///   <item><strong>RequiresConfirmation</strong>: <c>true</c> when the method
    ///         is POST/PUT/PATCH/DELETE AND the side effect is not <c>none</c>.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="schema">The source tool / OpenAPI operation schema.</param>
    /// <returns>A typed contract descriptor ready for review or registration.</returns>
    /// <param name="trusted">Set only for hand-authored/vetted schemas. When false (the default, e.g.
    /// an imported or generated OpenAPI spec) a caller hint cannot DOWNGRADE a mutating operation: a
    /// POST/PUT/PATCH/DELETE with <c>SideEffectHint="none"</c> is floored to its method-implied side
    /// effect so confirmation is not silently dropped. Trusted callers keep full control of the hints.</param>
    public static ImportedContract ToContract(ToolSchema schema, bool trusted = false)
    {
        // Derive the IntentMesh action kind id from the tool name.
        var kind = "act-" + schema.Name.ToLowerInvariant().Replace('_', '-');

        bool mutating = schema.Method.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";

        // Side effect: caller hint wins; otherwise infer from operation semantics (keywords in the
        // name/summary), falling back to the HTTP method.
        var sideEffect = !string.IsNullOrWhiteSpace(schema.SideEffectHint)
            ? schema.SideEffectHint.ToLowerInvariant()
            : InferSideEffect(schema.Name, schema.Summary, schema.Method);

        // SSRF-of-confirmation guard: an untrusted spec can't claim a SIDE-EFFECTING op is side-effect-free
        // — regardless of the HTTP verb. If the hint says "none" but semantics (name/summary) or the verb
        // infer a real side effect, the inference wins for an untrusted spec, so a side-effecting GET/HEAD
        // can't suppress confirmation via SideEffectHint:"none". A trusted spec keeps full control.
        if (!trusted && sideEffect == "none")
        {
            var inferred = InferSideEffect(schema.Name, schema.Summary, schema.Method);
            if (inferred != "none") sideEffect = inferred;
        }

        // Resolve risk: caller hint wins; otherwise infer from method AND the semantic side effect.
        var risk = !string.IsNullOrWhiteSpace(schema.RiskHint)
            ? schema.RiskHint.ToLowerInvariant()
            : InferRisk(schema.Method, sideEffect);

        // Capability the action requires, inferred from the side effect + operation semantics.
        var capability = InferCapability(sideEffect, schema.Name, schema.Summary);

        // Confirmation is required whenever the operation has a real side effect — keyed on the SIDE
        // EFFECT, not the HTTP verb. A side-effecting GET/HEAD (e.g. an operation named "sendInvite" or
        // "purgeCache" exposed over GET) must still be gated; tying confirmation to mutating verbs let
        // such operations bypass it. A genuinely safe read infers side-effect "none" and stays ungated;
        // a trusted spec can still mark a read explicitly safe via SideEffectHint="none".
        bool requiresConfirmation = sideEffect != "none";

        return new ImportedContract(
            Kind: kind,
            Risk: risk,
            SideEffect: sideEffect,
            Fields: schema.Parameters.ToArray(),
            RequiresConfirmation: requiresConfirmation,
            Capability: capability);
    }

    private static string InferRisk(string method, string sideEffect)
    {
        // A destructive or financial side effect escalates risk regardless of the verb.
        if (sideEffect is "delete" || sideEffect.Contains("financial") || sideEffect.Contains("destruct"))
            return "high";
        if (sideEffect is not "none")
            return "medium";
        return method.ToUpperInvariant() switch
        {
            "DELETE" => "high",
            "POST" or "PUT" or "PATCH" => "medium",
            _ => "low",
        };
    }

    /// <summary>Deterministic keyword-based side-effect inference from the operation id + summary,
    /// with the HTTP method as a fallback. No LLM — a fixed keyword table.</summary>
    private static string InferSideEffect(string name, string summary, string method)
    {
        var text = (name + " " + summary).ToLowerInvariant();
        if (ContainsAny(text, "delete", "remove", "purge", "drop", "destroy", "revoke")) return "delete";
        if (ContainsAny(text, "refund", "charge", "payment", "invoice", "billing", "transfer", "payout")) return "financial-write";
        if (ContainsAny(text, "email", "mail", "notify", "message", "send")) return "email-send";
        if (ContainsAny(text, "upload", "file", "document", "attachment", "blob", "storage")) return "file-write";
        if (ContainsAny(text, "calendar", "event", "meeting", "schedule", "appointment")) return "calendar-write";
        var m = method.ToUpperInvariant();
        return m is "POST" or "PUT" or "PATCH" ? "write" : m is "DELETE" ? "delete" : "none";
    }

    /// <summary>Maps a side effect (and the operation text) to the capability the PolicyGate scopes
    /// on. Empty when nothing specific is implied.</summary>
    private static string InferCapability(string sideEffect, string name, string summary)
    {
        var text = (name + " " + summary).ToLowerInvariant();
        if (sideEffect == "email-send" || ContainsAny(text, "email", "mail")) return "email";
        if (sideEffect == "financial-write" || ContainsAny(text, "invoice", "payment", "billing", "charge", "refund")) return "billing";
        if (sideEffect == "file-write" || ContainsAny(text, "file", "upload", "document", "storage")) return "filesystem";
        if (sideEffect == "calendar-write" || ContainsAny(text, "calendar", "meeting", "event")) return "calendar";
        if (ContainsAny(text, "query", "sql", "database", "report", "analytics")) return "data";
        return "";
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var n in needles) if (text.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    // ── REAL — OpenAPI 3.x parsing (System.Text.Json, no extra deps) ──────────
    /// <summary>
    /// Parses a real OpenAPI 3.x document — JSON or YAML — into <see cref="ToolSchema"/> records,
    /// one per path × HTTP method. Operation name = <c>operationId</c> (or method+path); fields =
    /// path-level + operation parameters plus the request-body schema properties; local
    /// <c>$ref</c> pointers (<c>#/components/...</c>) are resolved for parameters and the request
    /// body (including <c>allOf</c> composition). Tags are folded into the summary so
    /// <see cref="ToContract"/> can infer side effect, risk, and capability semantically. YAML is
    /// converted to JSON first by the dependency-free <see cref="MiniYaml"/> converter — no
    /// Microsoft.OpenApi dependency. Feed the results to <see cref="ToContract"/> then
    /// <see cref="RegisterToCompiledDir"/>.
    /// </summary>
    public static IReadOnlyList<ToolSchema> ParseFromOpenApi(string spec)
    {
        var schemas = new List<ToolSchema>();
        var json = LooksLikeJson(spec) ? spec : MiniYaml.ToJson(spec);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            return schemas;

        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;
            var pathLevelParams = ReadParameters(root, path.Value); // shared across the path's methods
            foreach (var op in path.Value.EnumerateObject())
            {
                var method = op.Name.ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD")) continue;
                var operation = op.Value;
                if (operation.ValueKind != JsonValueKind.Object) continue;

                string name = operation.TryGetProperty("operationId", out var oid) && oid.ValueKind == JsonValueKind.String
                    ? oid.GetString()!
                    : (op.Name + "_" + path.Name.Trim('/').Replace('/', '_').Replace("{", "").Replace("}", "")).Trim('_');

                string summary = Str(operation, "summary") ?? Str(operation, "description") ?? "";
                if (operation.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    summary = (summary + " " + string.Join(" ", tags.EnumerateArray()
                        .Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()))).Trim();

                // Fields = path params + operation params + request-body properties ($ref-resolved),
                // de-duplicated while preserving first-seen order.
                var fields = new List<string>(pathLevelParams);
                fields.AddRange(ReadParameters(root, operation));
                ReadBodyFields(root, operation, fields);

                schemas.Add(new ToolSchema(name, method, summary, Dedup(fields)));
            }
        }
        return schemas;
    }

    private static bool LooksLikeJson(string spec)
    {
        foreach (var c in spec)
        {
            if (char.IsWhiteSpace(c)) continue;
            return c == '{' || c == '[';
        }
        return false;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── $ref resolution + field extraction ────────────────────────────────────
    private static List<string> ReadParameters(JsonElement root, JsonElement container)
    {
        var names = new List<string>();
        if (container.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
            foreach (var p in ps.EnumerateArray())
            {
                var pr = Resolve(root, p);
                if (pr.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String)
                    names.Add(pn.GetString()!);
            }
        return names;
    }

    private static void ReadBodyFields(JsonElement root, JsonElement operation, List<string> fields)
    {
        if (!operation.TryGetProperty("requestBody", out var rb)) return;
        rb = Resolve(root, rb);
        if (!rb.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) return;
        foreach (var media in content.EnumerateObject())
            if (media.Value.TryGetProperty("schema", out var sch))
                CollectSchemaProps(root, sch, fields);
    }

    private static void CollectSchemaProps(JsonElement root, JsonElement schema, List<string> fields, int depth = 0)
    {
        if (depth > 16)
            throw new InvalidDataException("OpenAPI schema allOf/nesting exceeds 16 levels — rejected (fail-closed, would otherwise drop fields and under-scope the contract).");
        schema = Resolve(root, schema, depth);
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            foreach (var prop in props.EnumerateObject())
                fields.Add(prop.Name);
        if (schema.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
            foreach (var sub in allOf.EnumerateArray())
                CollectSchemaProps(root, sub, fields, depth + 1);
    }

    /// <summary>Follows a local <c>$ref</c> chain (<c>#/...</c>) to the referent. FAIL-CLOSED: a
    /// remote <c>$ref</c> (other file/URL) or an unresolvable local pointer throws rather than
    /// silently returning an unresolved node — silently dropping a referenced schema would
    /// under-scope the contract and could downgrade a dangerous operation's risk. Cycle-guarded by
    /// depth (a ref chain longer than 32 hops is treated as malformed).</summary>
    private static JsonElement Resolve(JsonElement root, JsonElement node, int depth = 0)
    {
        if (node.ValueKind != JsonValueKind.Object) return node;
        if (!node.TryGetProperty("$ref", out var r) || r.ValueKind != JsonValueKind.String) return node;

        if (depth > 32)
            throw new InvalidDataException("OpenAPI $ref chain exceeds 32 hops (possible cycle) — rejected.");
        var pointer = r.GetString()!;
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Remote/non-local $ref '{pointer}' is not supported — resolve it into a single document before import (fail-closed).");
        var target = ResolvePointer(root, pointer)
            ?? throw new InvalidDataException($"Unresolvable $ref '{pointer}' — the OpenAPI document is incomplete (fail-closed).");
        return Resolve(root, target, depth + 1);
    }

    private static JsonElement? ResolvePointer(JsonElement root, string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal)) return null; // local document refs only
        var cur = root;
        foreach (var seg in pointer[2..].Split('/'))
        {
            var key = seg.Replace("~1", "/").Replace("~0", "~");
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    private static string[] Dedup(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var item in items) if (seen.Add(item)) ordered.Add(item);
        return ordered.ToArray();
    }

    // ── REAL — register imported contracts into a loadable bundle ─────────────
    /// <summary>
    /// Compiles the imported contracts into a real <c>im-imported.tlmz</c> in <paramref name="compiledDir"/>
    /// using the same TLM format/compiler the rest of the bundle uses. After this,
    /// <c>SymbolicBundle.Load(compiledDir)</c> recognizes each <c>Kind</c> (IsRegistered = true) and
    /// the PolicyGate / Translation-Drift guard enforce it — import → usable typed contract,
    /// end-to-end. Returns the path written. (Write to a fresh dir or a copy of the bundle; writing
    /// into the canonical dataset would change the shipped contract set.)
    /// </summary>
    public static string RegisterToCompiledDir(string compiledDir, params ImportedContract[] contracts)
    {
        Directory.CreateDirectory(compiledDir);
        var concepts = new List<SymbolicConcept>
        {
            new() { Id = "imported-root", Label = "imported contracts", Category = "Registry",
                Description = "Action contracts imported from external tool / OpenAPI schemas." }
        };
        var relations = new List<SymbolicRelation>();
        foreach (var c in contracts)
        {
            concepts.Add(new SymbolicConcept
            {
                Id = c.Kind,
                Label = Pascal(c.Kind) + "Intent",
                Category = "ActionContract",
                Description = $"Imported typed contract for {c.Kind}.",
                Properties = new()
                {
                    ["Risk"] = c.Risk,
                    ["SideEffect"] = c.SideEffect,
                    ["RequiresConfirmation"] = c.RequiresConfirmation ? "true" : "false",
                    ["Fields"] = string.Join(",", c.Fields),
                    ["Postconditions"] = "",
                    ["Capability"] = c.Capability
                }
            });
            relations.Add(new SymbolicRelation { SourceId = "imported-root", TargetId = c.Kind, Type = "Registers" });
        }

        var pkg = new TlmPackage
        {
            Manifest = new TlmManifest
            {
                Metadata = new TlmMetadata
                {
                    TlmId = "im-imported",
                    IsMutable = false,
                    Role = TlmRole.Logic,
                    Priority = 125,
                    Version = "1.0.0",
                    Checksum = "",
                    HotSwapPolicy = HotSwapPolicy.Safe,
                    StabilityScore = 0.5
                },
                Imports = new(),
                Derives = new(),
                CreatedUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc),
                SchemaVersion = "1.0"
            },
            Concepts = concepts,
            Relations = relations
        };

        var compiler = new TlmCompiler();
        var bytes = compiler.Serialize(compiler.Compile(pkg));
        var outPath = Path.Combine(compiledDir, "im-imported.tlmz");
        File.WriteAllBytes(outPath, bytes);
        return outPath;
    }

    private static string Pascal(string kind)
    {
        var parts = kind.Replace("act-", "").Split('-', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
