using System.Globalization;
using System.Text.Json;

namespace IntentMesh.Integrations;

/// <summary>
/// A tiny, dependency-free YAML→JSON converter for the block-style subset that OpenAPI specs use:
/// nested mappings, sequences (of scalars or inline maps), quoted/plain scalars, integers, floats,
/// booleans, null, <c>#</c> comments, simple <c>|</c>/<c>&gt;</c> block scalars, and inline
/// <c>[a, b]</c> / <c>{}</c> flow collections. It produces a JSON string so the existing
/// System.Text.Json OpenAPI reader can consume YAML and JSON through one path. Not a full YAML 1.2
/// implementation (no anchors/aliases, tags, or multi-document streams) — enough for tool/OpenAPI
/// schemas, deterministically.
/// </summary>
internal static class MiniYaml
{
    // Resource bounds — a hostile spec must fail with a catchable exception, never OOM or trigger an
    // uncatchable StackOverflowException.
    private const int MaxInputBytes = 5 * 1024 * 1024;   // 5 MiB
    private const int MaxLines = 200_000;
    private const int MaxDepth = 200;

    public static string ToJson(string yaml)
    {
        if (yaml.Length > MaxInputBytes)
            throw new InvalidDataException($"YAML input exceeds the {MaxInputBytes / (1024 * 1024)} MiB limit — rejected.");
        var lines = Tokenize(yaml);
        int i = 0;
        object? root = lines.Count == 0 ? new Dictionary<string, object?>() : ParseNode(lines, ref i, lines[0].Indent, 0);
        return JsonSerializer.Serialize(root);
    }

    private readonly record struct Line(int Indent, string Text);

    // ── Tokenize: strip comments/blank lines, record indent + content ──────────
    private static List<Line> Tokenize(string yaml)
    {
        var result = new List<Line>();
        foreach (var raw in yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var stripped = StripComment(raw);
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (stripped.TrimStart() == "---" || stripped.TrimStart() == "...") continue; // doc markers
            int indent = 0;
            while (indent < stripped.Length && stripped[indent] == ' ') indent++;
            // A tab in the indentation is invalid YAML and would be silently mis-parsed — reject it.
            if (indent < stripped.Length && stripped[indent] == '\t')
                throw new InvalidDataException("Tab character in YAML indentation is not allowed — rejected (fail-closed).");
            result.Add(new Line(indent, stripped[indent..].TrimEnd()));
            if (result.Count > MaxLines)
                throw new InvalidDataException($"YAML input exceeds the {MaxLines}-line limit — rejected.");
        }
        return result;
    }

    /// <summary>Remove a trailing <c>#</c> comment that sits outside quotes (YAML needs a space or
    /// line-start before <c>#</c>).</summary>
    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || line[i - 1] == ' ' || line[i - 1] == '\t')) return line[..i];
        }
        return line;
    }

    // ── Recursive descent ──────────────────────────────────────────────────────
    private static object? ParseNode(List<Line> lines, ref int i, int indent, int depth)
    {
        if (depth > MaxDepth)
            throw new InvalidDataException($"YAML nesting exceeds the {MaxDepth}-level limit — rejected (fail-closed).");
        return lines[i].Text.StartsWith('-') && (lines[i].Text.Length == 1 || lines[i].Text[1] == ' ')
            ? ParseSequence(lines, ref i, indent, depth)
            : ParseMapping(lines, ref i, indent, depth);
    }

    private static Dictionary<string, object?> ParseMapping(List<Line> lines, ref int i, int indent, int depth)
    {
        var map = new Dictionary<string, object?>();
        while (i < lines.Count && lines[i].Indent == indent && !IsSequenceItem(lines[i].Text))
        {
            var (key, rest) = SplitKey(lines[i].Text);
            i++;
            if (rest.Length == 0)
            {
                // Nested block: a deeper mapping/sequence, or a sequence at the SAME indent.
                if (i < lines.Count && lines[i].Indent > indent)
                    map[key] = ParseNode(lines, ref i, lines[i].Indent, depth + 1);
                else if (i < lines.Count && lines[i].Indent == indent && IsSequenceItem(lines[i].Text))
                    map[key] = ParseSequence(lines, ref i, indent, depth + 1);
                else
                    map[key] = null;
            }
            else if (rest is "|" or ">" or "|-" or ">-" or "|+" or ">+")
            {
                map[key] = ParseBlockScalar(lines, ref i, indent, fold: rest[0] == '>');
            }
            else
            {
                map[key] = ParseScalar(rest);
            }
        }
        return map;
    }

    private static List<object?> ParseSequence(List<Line> lines, ref int i, int indent, int depth)
    {
        var list = new List<object?>();
        while (i < lines.Count && lines[i].Indent == indent && IsSequenceItem(lines[i].Text))
        {
            var full = lines[i].Text;            // e.g. "- name: id"  or  "- application/json"  or  "-"
            var content = full.Length == 1 ? "" : full[2..].TrimStart();
            int contentCol = indent + (full.Length - content.Length); // column where content begins

            if (content.Length == 0)
            {
                // Element body is on the following deeper lines.
                i++;
                list.Add(i < lines.Count && lines[i].Indent > indent ? ParseNode(lines, ref i, lines[i].Indent, depth + 1) : null);
            }
            else if (LooksLikeMapEntry(content))
            {
                // Inline map start: rewrite this line as a mapping line at the content column, then
                // parse a mapping that also absorbs the aligned continuation lines.
                lines[i] = new Line(contentCol, content);
                list.Add(ParseMapping(lines, ref i, contentCol, depth + 1));
            }
            else
            {
                i++;
                list.Add(ParseScalar(content));
            }
        }
        return list;
    }

    private static string ParseBlockScalar(List<Line> lines, ref int i, int parentIndent, bool fold)
    {
        var parts = new List<string>();
        while (i < lines.Count && lines[i].Indent > parentIndent)
        {
            parts.Add(lines[i].Text);
            i++;
        }
        return string.Join(fold ? " " : "\n", parts);
    }

    // ── Scalars & helpers ──────────────────────────────────────────────────────
    private static bool IsSequenceItem(string text) => text == "-" || text.StartsWith("- ");

    private static bool LooksLikeMapEntry(string content)
    {
        // A mapping entry has a "key:" with the colon outside quotes and the key free of spaces.
        char quote = '\0';
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == ':' && (i + 1 == content.Length || content[i + 1] == ' '))
                return !content[..i].Contains(' ');
        }
        return false;
    }

    private static (string key, string rest) SplitKey(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
                return (Unquote(text[..i].Trim()), text[(i + 1)..].Trim());
        }
        return (Unquote(text.Trim()), "");
    }

    private static object? ParseScalar(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s is "null" or "~" or "Null" or "NULL") return null;
        if (s is "true" or "True" or "TRUE") return true;
        if (s is "false" or "False" or "FALSE") return false;

        if (s[0] is '"' or '\'') return Unquote(s);

        // Inline flow collections.
        if (s.StartsWith('[') && s.EndsWith(']')) return ParseFlowSeq(s[1..^1]);
        if (s.StartsWith('{') && s.EndsWith('}')) return ParseFlowMap(s[1..^1]);

        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return s;
    }

    private static List<object?> ParseFlowSeq(string body)
    {
        var list = new List<object?>();
        body = body.Trim();
        if (body.Length == 0) return list;
        foreach (var part in SplitTopLevel(body)) list.Add(ParseScalar(part));
        return list;
    }

    private static Dictionary<string, object?> ParseFlowMap(string body)
    {
        var map = new Dictionary<string, object?>();
        body = body.Trim();
        if (body.Length == 0) return map;
        foreach (var part in SplitTopLevel(body))
        {
            var (k, rest) = SplitKey(part.Trim());
            map[k] = rest.Length == 0 ? null : ParseScalar(rest);
        }
        return map;
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var items = new List<string>();
        char quote = '\0';
        int depth = 0, start = 0;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            else if (c == ',' && depth == 0) { items.Add(body[start..i]); start = i + 1; }
        }
        items.Add(body[start..]);
        return items;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1].Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\\\", "\\");
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'')
            return s[1..^1].Replace("''", "'");
        return s;
    }
}
