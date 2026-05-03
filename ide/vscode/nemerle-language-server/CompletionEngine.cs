using System.Text.RegularExpressions;

namespace Nemerle.LanguageServer;

public partial class CompletionEngine
{
    private static readonly string[] Keywords =
    [
        // Types
        "class", "struct", "interface", "variant", "enum", "delegate", "module",
        "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "char", "string", "object", "void", "array", "list",

        // Modifiers
        "public", "private", "internal", "protected", "static", "sealed", "abstract",
        "virtual", "override", "extern", "mutable", "volatile", "partial", "new",

        // Declarations
        "def", "this", "base", "namespace", "using", "macro", "syntax",

        // Control flow
        "if", "else", "match", "for", "foreach", "while", "do", "repeat",
        "try", "catch", "finally", "throw", "break", "continue", "return",
        "when", "unless", "lock", "using", "checked", "unchecked", "ignore",

        // Expressions
        "fun", "assert", "is", "as", "typeof", "ref", "out", "params",

        // Literals and constants
        "true", "false", "null",

        // Pattern matching
        "_", "and", "or", "not", "with",

        // Other
        "get", "set", "value", "in", "where", "yield", "ensures", "requires",
        "invariant", "implements", "expose", "callcomp",

        // Attributes (as keywords)
        "[Record]", "[Accessor]", "[NotNull]", "[Immutable]",
        "[OverrideObjectEquals]", "[ProxyPublicMembers]", "[DebuggerBrowsable]",

        // Preprocessor
        "#if", "#elif", "#else", "#endif", "#region", "#endregion",
        "#define", "#undef", "#error", "#warning", "#line", "#pragma",
    ];

    private static readonly CompletionItem[] KeywordItems = Keywords
        .Select(k => new CompletionItem
        {
            Label = k,
            Kind = k.StartsWith('#') ? CompletionItemKind.Keyword : CompletionItemKind.Keyword,
            Detail = $"Nemerle keyword: {k}",
            InsertText = k.StartsWith('[') ? k + "\n" : k + (k is "class" or "module" or "variant" or "namespace" or "struct" or "interface" or "try" or "if" or "else" or "for" or "foreach" or "while" or "unless" or "match" or "fun" or "when" or "lock" or "checked" or "unchecked" ? " " : " ")
        })
        .ToArray();

    // Common Nemerle stdlib types
    private static readonly string[] StdlibTypes =
    [
        "List", "Map", "Set", "Hashtable", "Queue", "Stack", "array",
        "option", "NList", "pair", "LazyValue", "Nemerle.Collections.NList",
        "System.Console", "System.Math", "System.String", "System.Int32",
        "System.Boolean", "System.DateTime", "System.IO.File", "System.IO.Path",
        "System.IO.Directory", "System.Linq.Enumerable",
    ];

    public List<CompletionItem> GetCompletions(string text, Position position)
    {
        var lines = text.Split('\n');
        if (position.Line >= lines.Length)
            return new List<CompletionItem>();

        var line = lines[position.Line];
        var prefix = GetWordAtPosition(line, (int)position.Character);
        var result = new List<CompletionItem>();

        // Keywords and stdlib types filtered by prefix
        foreach (var kw in KeywordItems)
        {
            if (MatchesPrefix(kw.Label, prefix))
                result.Add(kw);
        }

        // Stdlib types
        foreach (var t in StdlibTypes)
        {
            if (MatchesPrefix(t, prefix))
            {
                var shortName = t.Split('.').Last();
                result.Add(new CompletionItem
                {
                    Label = t,
                    Kind = t.Contains('.') ? CompletionItemKind.Module : CompletionItemKind.Class,
                    Detail = $"Type: {t}",
                    InsertText = shortName
                });
            }
        }

        // Identifiers from the current file
        var identifiers = ExtractIdentifiers(text);
        foreach (var id in identifiers)
        {
            if (MatchesPrefix(id, prefix) && result.All(r => r.Label != id))
            {
                result.Add(new CompletionItem
                {
                    Label = id,
                    Kind = CompletionItemKind.Variable,
                    Detail = "Local identifier",
                    InsertText = id
                });
            }
        }

        return result;
    }

    private static bool MatchesPrefix(string word, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return true;
        return word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWordAtPosition(string line, int col)
    {
        // Extract the word before the cursor
        if (col <= 0) return "";
        if (col > line.Length) col = line.Length;

        int start = col - 1;
        while (start >= 0 && IsIdentifierChar(line[start]))
            start--;
        return line[(start + 1)..Math.Min(col, line.Length)];
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '.' || c == '[' || c == '#' || c == '@';

    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_'\.]*", RegexOptions.Compiled)]
    private static partial Regex IdentifierRegex();

    private static HashSet<string> ExtractIdentifiers(string text)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var matches = IdentifierRegex().Matches(text);
        foreach (Match m in matches)
        {
            var v = m.Value;
            if (v.Length >= 2 && !IsKeywordLike(v))
                ids.Add(v);
        }
        return ids;
    }

    private static bool IsKeywordLike(string w)
    {
        return w is "true" or "false" or "null" or "this" or "base" or "def" or "class" or "module"
            or "if" or "else" or "for" or "while" or "return" or "match" or "fun" or "try" or "catch"
            or "using" or "namespace" or "public" or "private" or "static" or "mutable" or "int"
            or "string" or "bool" or "void" or "when" or "unless" or "variant" or "macro";
    }
}
