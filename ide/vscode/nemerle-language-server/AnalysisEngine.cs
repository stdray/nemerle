using System.Text.RegularExpressions;

namespace Nemerle.LanguageServer;

public partial class AnalysisEngine
{
    // Definition patterns: keyword followed by identifier
    private static readonly string[] DefKeywords =
        ["def", "mutable", "class", "module", "variant", "macro", "enum",
         "interface", "struct", "delegate", "namespace", "public", "private",
         "internal", "protected", "static", "abstract", "virtual", "override", "sealed"];

    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_']*", RegexOptions.Compiled)]
    private static partial Regex IdentifierRegex();

    public string? GetWordAtPosition(string text, Position position)
    {
        var lines = text.Split('\n');
        if (position.Line >= lines.Length) return null;
        var line = lines[position.Line];
        var col = (int)position.Character;
        if (col > line.Length) col = line.Length;

        // Find word boundaries
        int start = col - 1;
        while (start >= 0 && IsIdentifierChar(line[start])) start--;
        int end = col;
        while (end < line.Length && IsIdentifierChar(line[end])) end++;

        var word = line[(start + 1)..end];
        return word.Length > 0 ? word : null;
    }

    public List<Location> FindDefinitions(string text, string word)
    {
        var results = new List<Location>();
        var lines = text.Split('\n');

        for (int lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            var line = lines[lineNum];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            foreach (var kw in DefKeywords)
            {
                var pattern = $@"\b{Regex.Escape(kw)}\s+({Regex.Escape(word)})\b";
                var match = Regex.Match(trimmed, pattern);
                if (match.Success && match.Groups.Count > 1)
                {
                    var nameGroup = match.Groups[1];
                    var col = indent + nameGroup.Index;
                    results.Add(new Location(
                        "file:///test/t.n",
                        new Range(
                            new Position(lineNum, col),
                            new Position(lineNum, col + nameGroup.Length))));
                    break; // Found in this line
                }
            }
        }

        // If not found by keyword, check if it's a function definition at line start
        if (results.Count == 0)
        {
            for (int lineNum = 0; lineNum < lines.Length; lineNum++)
            {
                var line = lines[lineNum];
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;
                var funcMatch = Regex.Match(trimmed,
                    $@"^{Regex.Escape(word)}\s*\(");
                if (funcMatch.Success)
                {
                    results.Add(new Location(
                        "file:///test/t.n",
                        new Range(
                            new Position(lineNum, indent),
                            new Position(lineNum, indent + word.Length))));
                    break;
                }
            }
        }

        return results;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '.';
}
