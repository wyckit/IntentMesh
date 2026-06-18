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
//   • ParseFromOpenApi() parses a real OpenAPI 3.x JSON document with the built-in
//     System.Text.Json reader (no Microsoft.OpenApi dependency) — paths × methods →
//     ToolSchema, fields from parameters + request-body properties.
//   • RegisterToCompiledDir() compiles the imported contracts into a real
//     im-imported.tlmz using the TLM compiler; SymbolicBundle.Load() then recognizes
//     each Kind and the PolicyGate / Translation-Drift guard enforce it. Import →
//     usable typed contract, end-to-end.
//
// STILL FOR PRODUCTION (out of scope here):
//   • YAML specs (this reads JSON; convert YAML→JSON first) and $ref resolution.
//   • Auto-deriving SideEffect/capability from semantic hints rather than a method
//     heuristic; richer field types/required flags.
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
public sealed record ImportedContract(
    string Kind,
    string Risk,
    string SideEffect,
    string[] Fields,
    bool RequiresConfirmation);

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
    public static ImportedContract ToContract(ToolSchema schema)
    {
        // Derive the IntentMesh action kind id from the tool name.
        var kind = "act-" + schema.Name.ToLowerInvariant().Replace('_', '-');

        // Resolve risk: caller hint wins; otherwise infer from HTTP method.
        var risk = !string.IsNullOrWhiteSpace(schema.RiskHint)
            ? schema.RiskHint.ToLowerInvariant()
            : InferRisk(schema.Method);

        // Side effect: use caller hint, default to "none".
        var sideEffect = !string.IsNullOrWhiteSpace(schema.SideEffectHint)
            ? schema.SideEffectHint.ToLowerInvariant()
            : "none";

        // Confirmation required when: mutating method + non-trivial side effect.
        bool mutating = schema.Method.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";
        bool requiresConfirmation = mutating && sideEffect != "none";

        return new ImportedContract(
            Kind: kind,
            Risk: risk,
            SideEffect: sideEffect,
            Fields: schema.Parameters.ToArray(),
            RequiresConfirmation: requiresConfirmation);
    }

    private static string InferRisk(string method) =>
        method.ToUpperInvariant() switch
        {
            "DELETE" => "high",
            "POST" or "PUT" or "PATCH" => "medium",
            _ => "low",
        };

    // ── REAL — OpenAPI 3.x parsing (System.Text.Json, no extra deps) ──────────
    /// <summary>
    /// Parses a real OpenAPI 3.x JSON document into <see cref="ToolSchema"/> records — one per
    /// path × HTTP method. Operation name = <c>operationId</c> (or method+path); fields = the
    /// operation's parameters plus the request-body schema properties; risk is inferred from the
    /// method (overridable). Handles the common subset with the built-in JSON reader, no
    /// Microsoft.OpenApi dependency. Feed the results to <see cref="ToContract"/> then
    /// <see cref="RegisterToCompiledDir"/>.
    /// </summary>
    public static IReadOnlyList<ToolSchema> ParseFromOpenApi(string openApiJson)
    {
        var schemas = new List<ToolSchema>();
        using var doc = JsonDocument.Parse(openApiJson);
        if (!doc.RootElement.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
            return schemas;

        foreach (var path in paths.EnumerateObject())
        {
            if (path.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var op in path.Value.EnumerateObject())
            {
                var method = op.Name.ToUpperInvariant();
                if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD")) continue;
                var operation = op.Value;

                string name = operation.TryGetProperty("operationId", out var oid) && oid.ValueKind == JsonValueKind.String
                    ? oid.GetString()!
                    : (op.Name + "_" + path.Name.Trim('/').Replace('/', '_').Replace("{", "").Replace("}", "")).Trim('_');

                string summary = Str(operation, "summary") ?? Str(operation, "description") ?? "";

                var fields = new List<string>();
                if (operation.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
                    foreach (var p in ps.EnumerateArray())
                        if (p.TryGetProperty("name", out var pn) && pn.ValueKind == JsonValueKind.String)
                            fields.Add(pn.GetString()!);

                if (operation.TryGetProperty("requestBody", out var rb) &&
                    rb.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
                    foreach (var media in content.EnumerateObject())
                        if (media.Value.TryGetProperty("schema", out var sch) &&
                            sch.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
                            foreach (var prop in props.EnumerateObject())
                                fields.Add(prop.Name);

                schemas.Add(new ToolSchema(name, method, summary, fields));
            }
        }
        return schemas;
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

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
                    ["Risk"] = c.Risk, ["SideEffect"] = c.SideEffect,
                    ["RequiresConfirmation"] = c.RequiresConfirmation ? "true" : "false",
                    ["Fields"] = string.Join(",", c.Fields), ["Postconditions"] = ""
                }
            });
            relations.Add(new SymbolicRelation { SourceId = "imported-root", TargetId = c.Kind, Type = "Registers" });
        }

        var pkg = new TlmPackage
        {
            Manifest = new TlmManifest
            {
                Metadata = new TlmMetadata { TlmId = "im-imported", IsMutable = false, Role = TlmRole.Logic,
                    Priority = 125, Version = "1.0.0", Checksum = "", HotSwapPolicy = HotSwapPolicy.Safe, StabilityScore = 0.5 },
                Imports = new(), Derives = new(),
                CreatedUtc = new DateTime(2026, 6, 18, 0, 0, 0, DateTimeKind.Utc), SchemaVersion = "1.0"
            },
            Concepts = concepts, Relations = relations
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
