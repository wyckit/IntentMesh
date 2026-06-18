using IntentMesh.Core;

namespace IntentMesh.Integrations;

// ──────────────────────────────────────────────────────────────────────────────
// OAuthAdapterExample — a CLEAN EXAMPLE of how a real OAuth-backed adapter
//                       plugs into the IntentMesh IToolAdapter seam.
//
// WHAT IS REAL:
//   • GmailSendAdapter implements IToolAdapter — the same interface every
//     built-in adapter (EmailAdapter, CalendarAdapter, etc.) uses.
//   • Handles() declares exactly the action kinds this adapter covers.
//   • RequiredCapability declares the bundle capability the PolicyGate checks
//     before reaching Execute() (pol-capability-not-granted blocks if "email"
//     is not in the granted set).
//   • The adapter honours the `approved` parameter: if the node was not
//     explicitly approved it halts without transmitting.
//   • The structural seam (OAuth token acquisition → API call → result) is
//     fully present and documented; only the network call is stubbed.
//
// WHAT IS STUBBED (clearly marked below):
//   • Real OAuth token flow — AcquireTokenAsync throws NotImplementedException.
//   • Real Gmail API call — the send method is a no-op with a clear comment.
//   • Both stubs are inlined next to the code that would use them so the reader
//     sees exactly where the real implementation goes.
//
// HOW THIS BECOMES PRODUCTION:
//   1. Implement AcquireTokenAsync using MSAL or Google.Apis.Auth:
//      - Register the app in Google Cloud Console.
//      - Exchange an authorization code for access + refresh tokens.
//      - Cache tokens securely; refresh on expiry.
//   2. Replace the no-op send stub with a real Gmail API call:
//      - Build a GmailService with the acquired credential.
//      - Create a MimeMessage, base64-encode it, and call
//        service.Users.Messages.Send(msg, "me").ExecuteAsync().
//   3. Register GmailSendAdapter in IntentMeshRuntime by passing it via a
//      custom ToolHost (or extending ToolHost to accept additional adapters).
// ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The result of the OAuth token acquisition step.
///
/// <para>
/// In production this record is populated by <see cref="GmailSendAdapter.AcquireTokenAsync"/>.
/// In this prototype it is never constructed because the token flow is stubbed.
/// </para>
/// </summary>
public sealed record OAuthToken(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// A clean example of how a real OAuth-backed adapter plugs into the IntentMesh
/// <see cref="IToolAdapter"/> seam.
///
/// <para>
/// This adapter handles <see cref="Kinds.SendEmail"/> actions by (a) checking
/// the <c>email</c> capability grant, (b) requiring explicit user approval, and
/// (c) — in production — acquiring an OAuth token and calling the Gmail API.
/// </para>
///
/// <para>
/// <strong>Security invariant preserved by the IntentMesh pipeline:</strong>
/// <list type="bullet">
///   <item>The PolicyGate blocks this adapter's actions when the <c>email</c>
///         capability is not granted (<c>pol-capability-not-granted</c>).</item>
///   <item>The adapter itself also checks <paramref name="approved"/> and
///         transmits nothing unless the user explicitly approved the node.</item>
///   <item>The stub send is a genuine no-op — no bytes leave the process.</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>What is stubbed:</strong> <see cref="AcquireTokenAsync"/> (OAuth
/// token flow) and the Gmail API call inside <see cref="Execute"/> are both
/// stubs. The stubs throw or no-op with clear comments; no real network I/O
/// occurs.
/// </para>
/// </summary>
public sealed class GmailSendAdapter : IToolAdapter
{
    /// <summary>
    /// The IntentMesh capability this adapter requires. The PolicyGate checks
    /// this against the runtime's granted capability set before reaching
    /// <see cref="Execute"/>. If <c>email</c> is not granted, the node is
    /// blocked with <c>pol-capability-not-granted</c> — the adapter is never
    /// invoked.
    /// </summary>
    public const string RequiredCapability = "email";

    /// <summary>
    /// Declares that this adapter handles <see cref="Kinds.SendEmail"/> actions.
    /// The ToolHost calls this to route a typed node to the right adapter.
    /// </summary>
    public bool Handles(string kind) => kind == Kinds.SendEmail;

    /// <summary>
    /// Executes a <see cref="SendEmailAction"/>, honoring the IntentMesh
    /// approval model.
    ///
    /// <para>
    /// <strong>Approval gate (real logic):</strong> if <paramref name="approved"/>
    /// is <c>false</c> the adapter halts immediately — zero bytes are transmitted.
    /// This mirrors the built-in <c>EmailAdapter.Send()</c> pattern.
    /// </para>
    ///
    /// <para>
    /// <strong>OAuth + Gmail send (STUBBED):</strong> the block that would
    /// acquire a token and call the Gmail API is clearly marked as a stub.
    /// </para>
    /// </summary>
    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws, bool approved)
    {
        if (node.Action is not SendEmailAction send)
            return ToolHost.Ok(node.Id, "GmailSendAdapter: no-op (unexpected action type)");

        // ── Approval gate (REAL — do not remove) ─────────────────────────────
        // A send_email node requires both (a) the 'email' capability in the
        // granted set and (b) explicit user approval. The capability check is
        // enforced upstream by the PolicyGate; the approval check is here.
        if (!approved)
        {
            return ToolHost.Halt(
                node.Id,
                $"GmailSendAdapter: send to {send.Recipient} requires explicit user approval — " +
                "NOT transmitted. Approve the node to proceed.",
                "0 messages transmitted");
        }

        // ── OAuth token acquisition (STUBBED) ─────────────────────────────────
        // In production: call AcquireTokenAsync() here and pass the token to
        // the Gmail API call below.
        //
        // OAuthToken token = await AcquireTokenAsync();   ← STUBBED (see below)
        //
        // We skip it in the prototype; no network call is made.

        // ── Gmail API send (STUBBED — genuine no-op) ──────────────────────────
        // real OAuth-authenticated Gmail send goes here;
        // gated by the 'email' capability grant + user approval.
        //
        // Production code (not executed in this prototype):
        //
        //   var credential = GoogleCredential.FromAccessToken(token.AccessToken);
        //   var service = new GmailService(new BaseClientService.Initializer
        //       { HttpClientInitializer = credential });
        //   var mime = BuildMimeMessage(send.Recipient, send.DraftRef, send.BodySourceRefs);
        //   await service.Users.Messages.Send(mime, "me").ExecuteAsync();
        //
        // This stub records the send in the sandboxed workspace (matching the
        // built-in EmailAdapter pattern) so tests can assert the side-effect.
        ws.SentEmails.Add(send.Recipient);

        return ToolHost.Ok(
            node.Id,
            $"GmailSendAdapter: [STUB] would send to {send.Recipient} via Gmail API " +
            "(no-op in prototype; real OAuth-authenticated send is not implemented).",
            "0 real messages transmitted — prototype stub only",
            $"recipient = {send.Recipient}");
    }

    // ── STUB — OAuth token flow (NOT IMPLEMENTED) ─────────────────────────────
    /// <summary>
    /// [STUB — NOT IMPLEMENTED] Acquires a valid OAuth 2.0 access token for the
    /// Gmail API scope (<c>https://mail.google.com/</c>).
    ///
    /// <para>
    /// <strong>Why this is stubbed:</strong> real OAuth requires a Google Cloud
    /// Console app registration, a client ID/secret, a browser redirect (or
    /// device-flow), and a token store — all out of scope for this in-process
    /// prototype.
    /// </para>
    ///
    /// <para>
    /// <strong>Production path (Google / MSAL):</strong>
    /// <list type="number">
    ///   <item>Register the app in Google Cloud Console; download
    ///         <c>client_secrets.json</c>.</item>
    ///   <item>Use <c>Google.Apis.Auth</c>:
    ///         <c>GoogleWebAuthorizationBroker.AuthorizeAsync(...)</c> for the
    ///         first login (stores refresh token in a <c>FileDataStore</c>).</item>
    ///   <item>On subsequent calls, the library silently refreshes the token from
    ///         the store — no user interaction needed.</item>
    ///   <item>Return an <see cref="OAuthToken"/> wrapping the access token and
    ///         its expiry so the caller can pass it to the Gmail API.</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <exception cref="NotImplementedException">
    /// Always — real OAuth token flow is not implemented in this prototype.
    /// </exception>
    public static Task<OAuthToken> AcquireTokenAsync()
    {
        // TODO (Phase 5 → production): implement with Google.Apis.Auth or MSAL.
        // Store the refresh token securely (user secrets / key vault).
        // See docs/INTEGRATIONS.md §OAuthAdapterExample.
        throw new NotImplementedException(
            "Real OAuth 2.0 token acquisition is not implemented in this prototype. " +
            "Implement with Google.Apis.Auth (or MSAL) and a secure token store. " +
            "See docs/INTEGRATIONS.md §OAuthAdapterExample.");
    }
}

/// <summary>
/// A custom <see cref="ToolHost"/>-style registry that demonstrates how to
/// inject a real OAuth adapter alongside the built-in adapters.
///
/// <para>
/// In production the IntentMeshRuntime would be extended (or ToolHost made
/// injectable) to accept a list of <see cref="IToolAdapter"/> instances. This
/// shows the intended wiring without modifying Core.
/// </para>
/// </summary>
public static class OAuthAdapterWiringExample
{
    /// <summary>
    /// Illustrates the intended production wiring: create a runtime that uses
    /// the <see cref="GmailSendAdapter"/> for <c>act-send-email</c> nodes.
    ///
    /// <para>
    /// <strong>Current limitation:</strong> <c>ToolHost</c> in Core uses a
    /// hard-coded adapter list. In a production SDK the list would be
    /// injectable. This method documents the intent and is used by tests to
    /// verify the adapter's <c>Handles</c> contract.
    /// </para>
    /// </summary>
    public static GmailSendAdapter CreateAdapter() => new();
}
