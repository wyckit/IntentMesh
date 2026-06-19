# Build your first IntentMesh adapter

An adapter is the only thing that actually touches the world. It is **boring and deterministic on
purpose**: it does not reason, it does not interpret language, and it accepts **only a typed
contract** — never raw text. Adding one is four small steps, all of which grow the symbolic layer
as *data*.

## The shape of an adapter

```csharp
public interface IToolAdapter
{
    bool Handles(string kind);
    ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws, bool approved);
}
```

`Execute` honors the policy decision: a `Confirm` decision performs only the safe, non-committing
path unless `approved` is true. It returns an `ExecutionResult` (what ran, what was halted, the
effects, and any zero-trust nodes proposed from untrusted data it read).

## Step 1 — declare the typed contract (TLM data)

In `BundleAuthor` (`im-action-contracts`), register the action with its risk, side-effect class,
confirmation requirement, fields, and the postconditions it must guarantee:

```csharp
("act-send-sms", "SendSmsIntent", "high", "external-comm", "true", "to,body",
    "pc-no-attacker-recipient", "Send an SMS. External side effect; requires confirmation."),
```

Then add a capability in `im-tools`, e.g. `("tool-sms", "sms adapter", "act-send-sms", "external-comm", "sms", "…")`.
Re-author + `tlm verify` (7/7). **No engine code changed yet** — the contract and capability are data.

## Step 2 — add the typed action

```csharp
public sealed record SendSmsAction(string To, string Body) : TypedAction("act-send-sms")
{ public override IReadOnlyList<(string,string)> Fields => new[] { ("to", To), ("body", Body) }; }
```

The resolver may only emit registered kinds (the Translation-Drift bound), so the proposer can
never invent this contract on the fly.

## Step 3 — write the adapter

```csharp
public sealed class SmsAdapter : IToolAdapter
{
    public bool Handles(string kind) => kind == "act-send-sms";

    public ExecutionResult Execute(IntentNode node, PolicyDecision decision, Workspace ws, bool approved)
    {
        var a = (SendSmsAction)node.Action;
        if (!approved)                                   // external-comm → Confirm; halt until approved
            return ToolHost.Halt(node.Id, $"SMS to {a.To} requires confirmation — NOT sent.", "0 sent");
        // real send goes here, gated by the 'sms' capability grant + this approval
        ws.SentEmails.Add(a.To);                          // (sandbox) record the effect
        return ToolHost.Ok(node.Id, $"Sent SMS to {a.To} (approved).", "1 message sent");
    }
}
```

Register it in `ToolHost`'s adapter list. The Policy Gate already governs it: capability scoping
blocks it unless `sms` is granted; the side-effect class makes sending require confirmation; a
zero-trust node proposing it is blocked outright.

## Step 4 — add a postcondition + a test

In `PostconditionVerifier`, assert the contract boundary (e.g. the SMS recipient equals the
approved contract recipient — the same pattern as `pc-recipient-contract-match`). Add an xUnit test
that the injected/untrusted case is blocked and the legit case verifies.

## What you get for free

- **Authority**: the action only runs from a validated, authorized typed contract.
- **Capability scoping**: it stays dark until its capability is granted (the real-adapter gate).
- **Zero-trust**: untrusted content can never drive it.
- **Audit**: every decision is in the signed bundle.

## The one rule that bites: enforce invariants in *every* adapter

An action-level invariant (e.g. *draft-before-send*: a send must reference an existing draft by its
exact `draftRef`) must be enforced in **every** adapter that handles the kind — not just the built-in
one. `GmailSendAdapter` and the in-memory `EmailAdapter` both re-check `draftRef`; a real send never
trusts raw intent fields. If you add a second adapter for a kind, port the invariant — the Step 4
postcondition is what catches an adapter that forgot.

## Where to start

- **[Minimal host template](../templates/IntentMesh.Host.Template/)** — wire an agent through the gate in ~60 lines.
- **[SDK.md](SDK.md)** — the `Run → Save → Replay → Explain` facade. **[EXTENSION-POINTS.md](EXTENSION-POINTS.md)** — every seam.
- **[INTEGRATIONS.md](INTEGRATIONS.md)** — the MCP proxy and real-OAuth adapter example.
