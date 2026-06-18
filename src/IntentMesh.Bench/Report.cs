using System.Text;

namespace IntentMesh.Bench;

public static class Report
{
    private static string Mark(bool b) => b ? "✅" : "❌";

    private static (int inj, int legit, int audit, int verif) Totals(IEnumerable<Score> s)
    {
        var l = s.ToList();
        return (l.Count(x => x.InjectionBlocked), l.Count(x => x.LegitDone), l.Count(x => x.AuditProduced), l.Count(x => x.Verified));
    }

    public static string Markdown(List<Row> rows)
    {
        int n = rows.Count;
        var mesh = Totals(rows.Select(r => r.Mesh));
        var van = Totals(rows.Select(r => r.Vanilla));
        var mcp = Totals(rows.Select(r => r.Mcp));

        var sb = new StringBuilder();
        sb.AppendLine("# IntentBench — Agentic Intent Safety Benchmark");
        sb.AppendLine();
        sb.AppendLine("> Don't execute language. Execute verified intent.");
        sb.AppendLine();
        sb.AppendLine($"{n} scenarios across five attack vectors. IntentMesh runs the real pipeline; the");
        sb.AppendLine("baselines are deterministic **models** of each architecture class (not live LLMs):");
        sb.AppendLine("a **Vanilla agent** (prompt → LLM → tool, no boundary) and an **MCP-gated agent**");
        sb.AppendLine("(tool-name allowlist, no intent/authority/recipient reasoning).");
        sb.AppendLine();
        sb.AppendLine("## Scoreboard");
        sb.AppendLine();
        sb.AppendLine($"| Criterion (out of {n}) | Vanilla LLM | MCP / tool-gated | **IntentMesh** |");
        sb.AppendLine("|---|---|---|---|");
        sb.AppendLine($"| Injection blocked | {van.inj} | {mcp.inj} | **{mesh.inj}** |");
        sb.AppendLine($"| Legit task completed | {van.legit} | {mcp.legit} | **{mesh.legit}** |");
        sb.AppendLine($"| Audit produced | {van.audit} | {mcp.audit} | **{mesh.audit}** |");
        sb.AppendLine($"| Postcondition verified | {van.verif} | {mcp.verif} | **{mesh.verif}** |");
        sb.AppendLine();
        sb.AppendLine("## By attack vector — injection blocked");
        sb.AppendLine();
        sb.AppendLine("| Vector | Vanilla | MCP-gated | IntentMesh |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var g in rows.GroupBy(r => r.Scenario.Vector))
        {
            int c = g.Count();
            sb.AppendLine($"| {g.Key} ({c}) | {g.Count(r => r.Vanilla.InjectionBlocked)}/{c} | {g.Count(r => r.Mcp.InjectionBlocked)}/{c} | {g.Count(r => r.Mesh.InjectionBlocked)}/{c} |");
        }
        sb.AppendLine();
        sb.AppendLine("The structural insight: MCP/tool-gating only blocks the obvious raw-shell case; every");
        sb.AppendLine("attack that uses a *legitimate* tool with malicious arguments (email, query, file)");
        sb.AppendLine("sails through, because the payload looks like a valid tool call. IntentMesh quarantines");
        sb.AppendLine("it as a zero-authority source **before** it becomes a tool call.");
        sb.AppendLine();
        sb.AppendLine("## Per-scenario (IntentMesh)");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Vector | Injection blocked | Legit done | Audit | Verified |");
        sb.AppendLine("|---|---|:--:|:--:|:--:|:--:|");
        foreach (var r in rows)
            sb.AppendLine($"| `{r.Scenario.Id}` | {r.Scenario.Vector} | {Mark(r.Mesh.InjectionBlocked)} | {Mark(r.Mesh.LegitDone)} | {Mark(r.Mesh.AuditProduced)} | {Mark(r.Mesh.Verified)} |");
        sb.AppendLine();
        sb.AppendLine("_Baselines are deterministic architecture-class models, included to show the structural");
        sb.AppendLine("difference, not to benchmark any specific product._");
        return sb.ToString();
    }

    public static string ConsoleSummary(List<Row> rows)
    {
        var mesh = Totals(rows.Select(r => r.Mesh));
        var van = Totals(rows.Select(r => r.Vanilla));
        var mcp = Totals(rows.Select(r => r.Mcp));
        int n = rows.Count;
        var sb = new StringBuilder();
        sb.AppendLine($"IntentBench — {n} scenarios");
        sb.AppendLine($"  {"criterion",-26}{"vanilla",10}{"mcp-gated",12}{"intentmesh",12}");
        sb.AppendLine($"  {"injection blocked",-26}{van.inj,10}{mcp.inj,12}{mesh.inj,12}");
        sb.AppendLine($"  {"legit task completed",-26}{van.legit,10}{mcp.legit,12}{mesh.legit,12}");
        sb.AppendLine($"  {"audit produced",-26}{van.audit,10}{mcp.audit,12}{mesh.audit,12}");
        sb.AppendLine($"  {"postcondition verified",-26}{van.verif,10}{mcp.verif,12}{mesh.verif,12}");
        return sb.ToString();
    }

    public static string Html(List<Row> rows)
    {
        int n = rows.Count;
        var mesh = Totals(rows.Select(r => r.Mesh));
        var van = Totals(rows.Select(r => r.Vanilla));
        var mcp = Totals(rows.Select(r => r.Mcp));
        string Cell(int v) => $"<td class=\"num {(v == n ? "full" : v == 0 ? "zero" : "part")}\">{v}/{n}</td>";
        var sb = new StringBuilder();
        sb.Append("""
        <!DOCTYPE html><html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>IntentBench — Agentic Intent Safety Benchmark</title><style>
        body{margin:0;background:#0b0f14;color:#e6edf3;font-family:system-ui,Segoe UI,Roboto,sans-serif;padding:32px}
        h1{font-size:22px}.slogan{color:#4cc2ff;font-family:ui-monospace,Consolas,monospace;font-size:13px}
        p{color:#8b9bb0;max-width:760px;line-height:1.5}
        table{border-collapse:collapse;margin:18px 0;background:#121823;border:1px solid #1f2a38;border-radius:10px;overflow:hidden}
        th,td{padding:10px 16px;border-bottom:1px solid #1f2a38;text-align:left}th{background:#0e151f;font-size:13px}
        td.num{text-align:center;font-weight:700;font-family:ui-monospace,Consolas,monospace}
        .full{color:#34d399}.zero{color:#f87171}.part{color:#fbbf24}.mesh{color:#34d399}
        </style></head><body>
        <h1>IntentBench <span class="slogan">— Don't execute language. Execute verified intent.</span></h1>
        """);
        sb.Append($"<p>{n} scenarios across five attack vectors. IntentMesh runs the real pipeline; baselines are deterministic models of each architecture class (Vanilla = no boundary; MCP-gated = tool-name allowlist, no intent/authority reasoning).</p>");
        sb.Append("<table><tr><th>Criterion</th><th>Vanilla LLM</th><th>MCP / tool-gated</th><th class=mesh>IntentMesh</th></tr>");
        sb.Append($"<tr><td>Injection blocked</td>{Cell(van.inj)}{Cell(mcp.inj)}{Cell(mesh.inj)}</tr>");
        sb.Append($"<tr><td>Legit task completed</td>{Cell(van.legit)}{Cell(mcp.legit)}{Cell(mesh.legit)}</tr>");
        sb.Append($"<tr><td>Audit produced</td>{Cell(van.audit)}{Cell(mcp.audit)}{Cell(mesh.audit)}</tr>");
        sb.Append($"<tr><td>Postcondition verified</td>{Cell(van.verif)}{Cell(mcp.verif)}{Cell(mesh.verif)}</tr>");
        sb.Append("</table>");
        sb.Append("<table><tr><th>Vector</th><th>Vanilla</th><th>MCP-gated</th><th class=mesh>IntentMesh</th></tr>");
        foreach (var g in rows.GroupBy(r => r.Scenario.Vector))
        {
            int c = g.Count();
            sb.Append($"<tr><td>{g.Key}</td><td class=num>{g.Count(r => r.Vanilla.InjectionBlocked)}/{c}</td><td class=num>{g.Count(r => r.Mcp.InjectionBlocked)}/{c}</td><td class=\"num full\">{g.Count(r => r.Mesh.InjectionBlocked)}/{c}</td></tr>");
        }
        sb.Append("</table><p>Tool-gating blocks only the obvious raw-shell case; attacks using a legitimate tool with malicious arguments sail through. IntentMesh quarantines them as zero-authority before they become a tool call.</p>");
        sb.Append("</body></html>");
        return sb.ToString();
    }
}
