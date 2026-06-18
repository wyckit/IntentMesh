using IntentMesh.Core;

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
// WHAT IS STUBBED (clearly marked below):
//   • Real OpenAPI parsing — ToolSchema is a hand-authored record, not parsed
//     from a real OpenAPI spec YAML/JSON file. A TODO comment marks where the
//     real parser goes.
//   • Registration into im-action-contracts — ImportedContract is a plain
//     descriptor; it is NOT injected into a live SymbolicBundle or compiled
//     into a .tlmz. A TODO comment marks the registration step.
//   • Runtime enforcement of the imported contract — the contract descriptor
//     is produced but not yet wired into PolicyGate / IntentResolver as a
//     recognized action kind.
//
// HOW THIS BECOMES PRODUCTION:
//   1. Replace ToolSchema construction with a real OpenAPI/JSON-schema parser
//      (e.g., Microsoft.OpenApi).
//   2. Translate ImportedContract → a TLM ActionContract concept and append it
//      to the appropriate im-*.tlm source file.
//   3. Re-compile the bundle (tlm compile all) so the new kind is registered
//      and enforced by the pipeline.
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

    // ── STUB — real OpenAPI parsing (NOT IMPLEMENTED) ─────────────────────────
    /// <summary>
    /// [STUB — NOT IMPLEMENTED] Parses a real OpenAPI spec document and returns
    /// a list of <see cref="ToolSchema"/> records ready for <see cref="ToContract"/>.
    ///
    /// <para>
    /// <strong>Why this is stubbed:</strong> real OpenAPI parsing requires the
    /// <c>Microsoft.OpenApi</c> package (or equivalent) and a live YAML/JSON
    /// spec file — both out of scope for this in-process prototype.
    /// </para>
    ///
    /// <para>
    /// <strong>Production path:</strong>
    /// <list type="number">
    ///   <item>Add <c>Microsoft.OpenApi</c> NuGet package.</item>
    ///   <item>Parse the spec: <c>OpenApiDocument doc = new OpenApiStreamReader()
    ///         .Read(stream, out var diag);</c></item>
    ///   <item>Iterate <c>doc.Paths</c>, map each <c>OpenApiOperation</c> to a
    ///         <c>ToolSchema</c>, and call <see cref="ToContract"/> on each.</item>
    ///   <item>Feed the resulting contracts to <see cref="RegisterInBundle"/>.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <exception cref="NotImplementedException">
    /// Always — real OpenAPI parsing is not implemented in this prototype.
    /// </exception>
    public static IReadOnlyList<ToolSchema> ParseFromOpenApi(string openApiJson)
    {
        // TODO (Phase 5 → production): parse with Microsoft.OpenApi.
        // Map OpenApiOperation → ToolSchema for each path × method.
        throw new NotImplementedException(
            "Real OpenAPI YAML/JSON parsing is not implemented in this prototype. " +
            "Add Microsoft.OpenApi and implement the parser here. " +
            "See docs/INTEGRATIONS.md §OpenApiImporter.");
    }

    // ── STUB — bundle registration (NOT IMPLEMENTED) ─────────────────────────
    /// <summary>
    /// [STUB — NOT IMPLEMENTED] Registers an <see cref="ImportedContract"/> into
    /// a live <see cref="SymbolicBundle"/> so it becomes a recognized action kind
    /// that the IntentResolver and PolicyGate can enforce.
    ///
    /// <para>
    /// <strong>Why this is stubbed:</strong> <c>SymbolicBundle</c> is currently
    /// immutable (loaded from compiled <c>.tlmz</c> files). Registration would
    /// require either (a) a mutable bundle API, or (b) emitting a new
    /// <c>im-*.tlm</c> source and recompiling.
    /// </para>
    ///
    /// <para>
    /// <strong>Production path:</strong>
    /// <list type="number">
    ///   <item>Emit the contract as a TLM <c>ActionContract</c> concept into the
    ///         appropriate <c>im-*.tlm</c> source file.</item>
    ///   <item>Run <c>tlm compile all</c> to produce an updated <c>.tlmz</c>.</item>
    ///   <item>Reload the bundle: <c>SymbolicBundle.Load(compiledDir)</c>.</item>
    ///   <item>The new kind is now recognized by the Translation-Drift guard,
    ///         PolicyGate, and IntentResolver.</item>
    /// </list>
    /// Alternatively, expose a <c>SymbolicBundle.Register(ContractInfo)</c>
    /// method for hot-registration without recompilation.
    /// </para>
    /// </summary>
    /// <exception cref="NotImplementedException">
    /// Always — bundle registration is not implemented in this prototype.
    /// </exception>
    public static void RegisterInBundle(SymbolicBundle bundle, ImportedContract contract)
    {
        // TODO (Phase 5 → production): emit to TLM source + recompile, or add a
        // SymbolicBundle.Register(ContractInfo) hot-registration method.
        throw new NotImplementedException(
            "Registering an ImportedContract into a live SymbolicBundle is not " +
            "implemented in this prototype. See docs/INTEGRATIONS.md §OpenApiImporter.");
    }
}
