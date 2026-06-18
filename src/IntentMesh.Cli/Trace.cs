using IntentMesh.Core;

namespace IntentMesh.Cli;

/// <summary>Renders a RunResult as the five-panel pipeline trace (PassGen --trace, generalized).</summary>
public static class Trace
{
    public static void Render(RunResult r, string title)
    {
        Line('=');
        Console.WriteLine($"  INTENTMESH TRACE — {title}");
        Console.WriteLine("  Don't execute language. Execute verified intent.");
        Line('=');

        Console.WriteLine("\n  +-- [1] PROMPT  (what the human said)");
        Console.WriteLine($"  |     \"{r.Prompt}\"");
        Console.WriteLine("  v");

        Console.WriteLine("  +-- [2] INTENT MESH  (language -> typed, trust-tagged intent nodes)");
        foreach (var n in r.Nodes)
        {
            var trust = n.TrustSource == "User" ? "user " : "ZERO-TRUST";
            Console.WriteLine($"  |     [{n.Id}] {Mark(n.Status),-16} {trust,-10} {n.Type}");
            Console.WriteLine($"  |          {n.Label}");
            if (n.Fields.Count > 0)
                Console.WriteLine($"  |          fields: {string.Join(", ", n.Fields.Select(f => $"{f.Field}={f.Value}"))}");
        }
        Console.WriteLine("  v");

        Console.WriteLine("  +-- [3] POLICY GATE  (authority — allow / confirm / block)");
        foreach (var p in r.Policy)
        {
            Console.WriteLine($"  |     [{p.NodeId}] {p.Decision.ToUpper(),-8} risk={p.Risk,-6} {p.Label}");
            Console.WriteLine($"  |          reason: {p.Reason}");
            Console.WriteLine($"  |          rules:  {string.Join(", ", p.TriggeredRules)}");
        }
        Console.WriteLine("  v");

        Console.WriteLine("  +-- [4] EXECUTION  (deterministic tools; only validated typed intent runs)");
        foreach (var n in r.Nodes)
        {
            var e = r.Execution.FirstOrDefault(x => x.NodeId == n.Id);
            if (n.Status == "Blocked")
                Console.WriteLine($"  |     [{n.Id}] [ BLOCKED ] {n.Label} — {n.BlockedReason}");
            else if (e is null)
                Console.WriteLine($"  |     [{n.Id}] [ -- ] {n.Label}");
            else
                Console.WriteLine($"  |     [{n.Id}] {(e.Halted ? "[ HALTED ]" : "[ ran ]   ")} {e.Summary}");
        }
        Console.WriteLine("  v");

        Console.WriteLine("  +-- [5] VERIFICATION  (postconditions checked after execution)");
        foreach (var v in r.Verification)
            Console.WriteLine($"        {(v.Pass ? "pass" : "FAIL")}  {v.Id,-34} {v.Expected}");
        var allPass = r.Verification.All(v => v.Pass);
        Console.WriteLine();
        Console.WriteLine($"  VERDICT: {(allPass ? "MATCHES APPROVED INTENT" : "VERIFICATION FAILED")}  " +
                          $"(allowed={r.Summary.Allowed} confirm={r.Summary.NeedsConfirmation} blocked={r.Summary.Blocked} " +
                          $"executed={r.Summary.Executed} verified={r.Summary.Verified})");

        Console.WriteLine("\n  AUDIT TRAIL");
        foreach (var a in r.Audit)
            Console.WriteLine($"     {a.Seq,3}. [{a.Phase,-7}] {a.NodeId,-4} {a.Message}");
    }

    private static string Mark(string status) => status switch
    {
        "Blocked" => "BLOCKED",
        "NeedsConfirmation" => "needs-confirm",
        "Verified" => "verified",
        "Executed" => "executed",
        "Allowed" => "allowed",
        _ => status.ToLowerInvariant()
    };

    private static void Line(char c) => Console.WriteLine(new string(c, 78));
}
