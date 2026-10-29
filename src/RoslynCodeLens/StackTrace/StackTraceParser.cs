using System.Globalization;
using System.Text.RegularExpressions;

namespace RoslynCodeLens.StackTrace;

/// <summary>One recognized line of a pasted stack trace, before demangling/resolution.</summary>
public sealed record ParsedTraceLine(
    string Raw,
    bool IsExceptionHeader,
    string TypeFullName,      // exception type (normalized) for headers; declaring type (runtime-mangled form) for frames
    string MethodName,        // empty for headers; mangled method segment for frames; [T] args stripped
    string? Parameters,       // raw parameter list text, null for headers
    string? File,
    int? Line,
    bool IsDemystified,
    bool DemystifiedAsync,
    bool IsFrameLikeUnparsed = false,          // 'at '-anchored line no grammar recognized; only Raw is meaningful
    bool DemystifiedLambda = false,            // Demystifier '+(...) => { }' suffix
    string? DemystifiedLocalFunction = null);  // Demystifier '+Name(...)' suffix

/// <summary>
/// Result of parsing a pasted stack trace. <see cref="Lines"/> is ONE ordered list covering
/// every recognized or frame-like line in original order — frame-like-but-unparsed lines are
/// discriminated via <see cref="ParsedTraceLine.IsFrameLikeUnparsed"/> so downstream consumers
/// preserve trace positions trivially. <see cref="FrameLikeUnparsed"/> is a convenience view.
/// </summary>
public sealed record StackTraceParseResult(IReadOnlyList<ParsedTraceLine> Lines)
{
    /// <summary>Frame-like lines that failed all grammars (also present in Lines, in order).</summary>
    public IReadOnlyList<string> FrameLikeUnparsed
        => Lines.Where(l => l.IsFrameLikeUnparsed).Select(l => l.Raw).ToList();
}

public static partial class StackTraceParser
{
    // "at " frame anchor anywhere in the line (log prefixes come before it).
    [GeneratedRegex(@"(?:^|\s)at\s+(?<rest>.+)$")]
    private static partial Regex AtAnchor();

    // Exception header: [---> ]Fully.Qualified.TypeName[`N[...]]: message   (type must contain
    // a dot, no spaces — keeps "ERROR: something" log lines out; [ ] admit generic instantiation).
    [GeneratedRegex(@"^(?:--->\s+)?(?<type>[A-Za-z_][A-Za-z0-9_.+`\[\]]*\.[A-Za-z0-9_.+`\[\]]+)\s*:\s+.+$")]
    private static partial Regex ExceptionHeader();

    // " in <file>:line N" suffix of a runtime frame.
    [GeneratedRegex(@"\s+in\s+(?<file>.+?):line\s+(?<line>\d+)\s*$")]
    private static partial Regex FileLineSuffix();

    // Native-AOT offset suffix: " + 0x39" after the parameter list (#303).
    [GeneratedRegex(@"\s*\+\s*0x[0-9a-fA-F]+\s*$")]
    private static partial Regex OffsetSuffix();

    // Demystifier lambda suffix tail: "(...) => { }".
    [GeneratedRegex(@"=>\s*\{\s*\}\s*$")]
    private static partial Regex LambdaArrowSuffix();

    public static StackTraceParseResult Parse(string text)
    {
        var results = new List<ParsedTraceLine>();
        var recognized = 0;
        var nonEmptyLines = 0;
        var lastNonEmpty = "";
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            nonEmptyLines++;
            lastNonEmpty = line;
            if (line.StartsWith("---", StringComparison.Ordinal) &&
                line.Contains("stack trace", StringComparison.OrdinalIgnoreCase))
                continue; // "--- End of ... stack trace ---" separators

            var at = AtAnchor().Match(line);
            if (at.Success && TryParseFrame(line, at.Groups["rest"].Value, out var frame))
            {
                results.Add(frame);
                recognized++;
                continue;
            }

            if (TryParseHeaders(line, results))
            {
                recognized++;
                continue;
            }

            // Frame-like but unparsed: keep it (in order) instead of silently dropping (#303).
            if (line.StartsWith("at ", StringComparison.Ordinal))
            {
                results.Add(new ParsedTraceLine(
                    line, IsExceptionHeader: false, TypeFullName: "", MethodName: "",
                    Parameters: null, File: null, Line: null,
                    IsDemystified: false, DemystifiedAsync: false, IsFrameLikeUnparsed: true));
            }
            // else: noise — dropped
        }

        // Bare-input fallback: a single line like "Demo.Svc.<Run>d__3.MoveNext" pasted without
        // the 'at ' prefix (and possibly without parens) is retried as a frame body.
        if (recognized == 0 && nonEmptyLines == 1)
        {
            var body = lastNonEmpty.StartsWith("at ", StringComparison.Ordinal)
                ? lastNonEmpty[3..].TrimStart()
                : lastNonEmpty;
            if (!body.Contains('(')) body += "()";
            if (TryParseFrame(lastNonEmpty, body, out var bare))
                return new StackTraceParseResult([bare]);
        }

        return new StackTraceParseResult(results);
    }

    private static bool TryParseHeaders(string line, List<ParsedTraceLine> results)
    {
        // Framework prints inner chains on the SAME line: "Outer: msg ---> Inner: msg";
        // modern .NET puts " ---> Inner: msg" on its own line (single part, regex strips the arrow).
        var added = false;
        foreach (var part in line.Split(" ---> ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = ExceptionHeader().Match(part);
            if (!m.Success || part.Contains("):", StringComparison.Ordinal)) continue;
            results.Add(new ParsedTraceLine(
                part, IsExceptionHeader: true,
                TypeNameNormalizer.Normalize(m.Groups["type"].Value),
                MethodName: "", Parameters: null, File: null, Line: null,
                IsDemystified: false, DemystifiedAsync: false));
            added = true;
        }
        return added;
    }

    /// <summary>
    /// Splits a frame body into the parts every known frame format shares, then hands the
    /// remaining method path to <see cref="SplitTypeAndMethod"/>.
    /// </summary>
    /// <remarks>
    /// The extraction below is <b>strictly ordered, not a set of alternatives</b>: each step is a
    /// format-specific peel applied in a fixed sequence, and every step's failure aborts the whole
    /// frame (it does not fall through to a later format). Order matters because the suffixes nest —
    /// <c>+ EnumerateFrameSuffixes</c> must run before the last-paren-group scan, since the
    /// Demystifier lambda's own <c>(...)</c> is the last one; and the local-function peel must run
    /// before the parameter list is read, because it REPLACES that list. Reordering or making the
    /// steps independent would silently change which format wins on ambiguous lines.
    /// </remarks>
    private static bool TryParseFrame(string raw, string body, out ParsedTraceLine frame)
    {
        frame = null!;

        var (s, file, lineNo) = StripFrameSuffixes(body.Trim());

        if (!EnumerateFrameSuffixes(s, out var core)) return false;
        var (s2, lambdaSuffix, localFunction) = core;

        // The parameter list is the LAST balanced (...) group (tolerates nested parens
        // from ValueTuple parameters and tuple return types).
        if (!TryFindLastParenGroup(s2, out var open, out var close)) return false;

        var parameters = s2[(open + 1)..close];

        // Method path: the identifier chain immediately before '(' — balanced <...> / [...]
        // groups are skipped so generic annotations and instantiation blocks don't truncate it.
        var start = ScanMethodPathStart(s2, open);
        var methodPart = s2[start..open];
        if (methodPart.Length == 0) return false;

        // Anything before the method path is Demystifier decoration (modifiers + return type).
        var prefix = s2[..start].Trim();
        var isDemystified = prefix.Length > 0 || lambdaSuffix || localFunction != null;
        var demystifiedAsync = prefix.Length > 0 &&
            $" {prefix} ".Contains(" async ", StringComparison.Ordinal);

        if (!SplitTypeAndMethod(methodPart, out var type, out var method)) return false;

        frame = new ParsedTraceLine(
            raw, IsExceptionHeader: false, type, method, parameters,
            file, lineNo, isDemystified, demystifiedAsync,
            DemystifiedLambda: lambdaSuffix, DemystifiedLocalFunction: localFunction);
        return true;
    }

    /// <summary>
    /// Peels the trailing machine-generated suffixes in their fixed order: the runtime
    /// <c>in File.cs:line N</c> tail, the Native-AOT <c>+ 0x39</c> offset, and the trailing
    /// whitespace the first two can leave behind.
    /// </summary>
    private static (string Body, string? File, int? Line) StripFrameSuffixes(string body)
    {
        string? file = null;
        int? lineNo = null;

        var fm = FileLineSuffix().Match(body);
        if (fm.Success)
        {
            file = fm.Groups["file"].Value;
            lineNo = int.Parse(fm.Groups["line"].Value, CultureInfo.InvariantCulture);
            body = body[..fm.Index];
        }

        // Native-AOT frames append an offset: "at MyApp.Foo.Bar() + 0x39".
        var om = OffsetSuffix().Match(body);
        if (om.Success) body = body[..om.Index];

        return (body.TrimEnd(), file, lineNo);
    }

    /// <summary>
    /// Removes the two Demystifier wrapper forms — <c>Method(params)+(args) => { }</c> (lambda)
    /// and <c>Method(params)+LocalFunc(args)</c> (local function) — leaving the plain
    /// <c>Type.Method(params)</c> core.
    /// </summary>
    /// <remarks>
    /// A lambda suffix is detected first and suppresses the local-function peel: the lambda form
    /// already consumed the final group, and the remaining head carries no local-function suffix to
    /// look for. Both forms end by re-establishing a balanced trailing group, so a body that claims
    /// the form but lacks one is not a frame at all.
    /// </remarks>
    private static bool EnumerateFrameSuffixes(
        string s, out (string Body, bool Lambda, string? LocalFunction) core)
    {
        core = default;

        // Demystifier lambda suffix: "Method(params)+(lambdaParams) => { }".
        var lambdaSuffix = false;
        string? localFunction = null;

        var am = LambdaArrowSuffix().Match(s);
        if (am.Success)
        {
            var head = s[..am.Index].TrimEnd();
            if (!TryFindLastParenGroup(head, out var lambdaOpen, out _)) return false;
            var beforeGroup = head[..lambdaOpen].TrimEnd();
            if (!beforeGroup.EndsWith("+", StringComparison.Ordinal)) return false;
            lambdaSuffix = true;
            s = beforeGroup[..^1].TrimEnd();
        }

        // Demystifier local-function suffix: "Method(params)+LocalFunc(lfParams)".
        if (!lambdaSuffix)
        {
            if (!TryPeelLocalFunctionSuffix(s, out s, out localFunction)) return false;
        }

        core = (s, lambdaSuffix, localFunction);
        return true;
    }

    /// <summary>
    /// Peels a <c>+LocalFunc(args)</c> tail when present, leaving the enclosing method's body.
    /// </summary>
    /// <remarks>
    /// "When present" is decided syntactically: the group before the current parameter list must be
    /// followed by <c>+</c> AND by a closing <c>)</c>, which only the <c>Method(params)</c> form
    /// produces. A body without that shape is returned unchanged — this is the one step whose
    /// absence is not a parse failure.
    /// </remarks>
    private static bool TryPeelLocalFunctionSuffix(string s, out string body, out string? localFunction)
    {
        body = s;
        localFunction = null;

        // The parameter list is the LAST balanced (...) group.
        if (!TryFindLastParenGroup(s, out var open, out _)) return false;

        var j = open - 1;
        while (j >= 0 && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j--;
        if (j < 1 || j >= open - 1 || s[j] != '+' || s[j - 1] != ')') return true;

        localFunction = s[(j + 1)..open];
        body = s[..j].TrimEnd();

        // The enclosing method must still carry its own parameter list.
        return TryFindLastParenGroup(body, out _, out _);
    }

    /// <summary>
    /// Splits a <c>Type.Method</c> path on the last top-level dot, honouring the
    /// <c>.ctor</c>/<c>.cctor</c> forms and stripping method generic arguments.
    /// </summary>
    /// <remarks>
    /// <c>.ctor</c>/<c>.cctor</c> keep their leading dot as part of the METHOD name, so the split
    /// point moves left past it; every other path splits on the last dot outside balanced
    /// <c><...></c>/<c>[...]</c> groups (<see cref="FindSplitDot"/>). A path with no usable
    /// split — no dot, or a dot at either end — is not a frame.
    /// </remarks>
    private static bool SplitTypeAndMethod(string methodPart, out string type, out string method)
    {
        type = "";
        method = "";

        var splitAt = methodPart.EndsWith("..ctor", StringComparison.Ordinal) ? methodPart.Length - 6
            : methodPart.EndsWith("..cctor", StringComparison.Ordinal) ? methodPart.Length - 7
            : FindSplitDot(methodPart);
        if (splitAt <= 0 || splitAt >= methodPart.Length - 1) return false;

        type = methodPart[..splitAt];
        method = methodPart[(splitAt + 1)..];

        // Strip method generic args: GetById[TKey] -> GetById
        var bracket = method.IndexOf('[');
        if (bracket > 0 && method.EndsWith("]", StringComparison.Ordinal))
            method = method[..bracket];

        return true;
    }

    /// <summary>Finds the trailing balanced (...) group of <paramref name="s"/>.</summary>
    private static bool TryFindLastParenGroup(string s, out int open, out int close)
    {
        open = -1;
        close = -1;
        if (s.Length == 0 || s[^1] != ')') return false;
        var depth = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(')
            {
                depth--;
                if (depth == 0)
                {
                    open = i;
                    close = s.Length - 1;
                    return true;
                }
            }
        }
        return false;
    }

    private static int ScanMethodPathStart(string s, int openParen)
    {
        var i = openParen - 1;
        while (i >= 0)
        {
            var c = s[i];
            if (c is '>' or ']')
            {
                var match = FindMatchingOpen(s, i, c == '>' ? '<' : '[');
                if (match < 0) break;
                i = match - 1;
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '.' or '+' or '`' or '|')
            {
                i--;
            }
            else
            {
                break;
            }
        }
        return i + 1;
    }

    private static int FindMatchingOpen(string s, int closeIdx, char openChar)
    {
        var closeChar = s[closeIdx];
        var depth = 0;
        for (var i = closeIdx; i >= 0; i--)
        {
            if (s[i] == closeChar) depth++;
            else if (s[i] == openChar)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Last '.' outside balanced &lt;...&gt; / [...] groups, or -1.</summary>
    private static int FindSplitDot(string s)
    {
        var i = s.Length - 1;
        while (i >= 0)
        {
            var c = s[i];
            if (c is '>' or ']')
            {
                var match = FindMatchingOpen(s, i, c == '>' ? '<' : '[');
                if (match < 0) return -1;
                i = match - 1;
            }
            else if (c == '.')
            {
                return i;
            }
            else
            {
                i--;
            }
        }
        return -1;
    }
}
