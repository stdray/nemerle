namespace Nemerle.LanguageServer;

public record SymbolInfo(string Name, SymbolKind Kind, Range Range, Range SelectionRange, List<SymbolInfo>? Children = null);

public enum SymbolKind
{
    File = 1, Module = 2, Namespace = 3, Package = 4, Class = 5, Method = 6,
    Property = 7, Field = 8, Constructor = 9, Enum = 10, Interface = 11,
    Function = 12, Variable = 13, Constant = 14, String = 15, Number = 16,
    Boolean = 17, Array = 18, Object = 19, Key = 20, Null = 21,
    EnumMember = 22, Struct = 23, Event = 24, Operator = 25, TypeParameter = 26
}

public partial class AnalysisEngine
{
    public List<SymbolInfo> GetDocumentSymbols(string text)
    {
        var symbols = new List<SymbolInfo>();
        var lines = text.Split('\n');

        for (int lineNum = 0; lineNum < lines.Length; lineNum++)
        {
            var line = lines[lineNum];
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;

            // module X { ... }
            var moduleMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?module\s+(\w[\w']*)");
            if (moduleMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    moduleMatch.Groups[1].Value,
                    SymbolKind.Module,
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + moduleMatch.Length)),
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + moduleMatch.Length))));
            }

            // class X { ... }
            var classMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?(?:sealed\s+|abstract\s+|static\s+)?class\s+(\w[\w']*)");
            if (classMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    classMatch.Groups[1].Value,
                    SymbolKind.Class,
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + classMatch.Length)),
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + classMatch.Length))));
            }

            // variant X { ... }
            var variantMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?variant\s+(\w[\w']*)");
            if (variantMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    variantMatch.Groups[1].Value,
                    SymbolKind.Enum,
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + variantMatch.Length)),
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + variantMatch.Length))));
            }

            // interface X { ... }
            var ifaceMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?interface\s+(\w[\w']*)");
            if (ifaceMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    ifaceMatch.Groups[1].Value,
                    SymbolKind.Interface,
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + ifaceMatch.Length)),
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + ifaceMatch.Length))));
            }

            // enum X { ... }
            var enumMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?enum\s+(\w[\w']*)");
            if (enumMatch.Success && !trimmed.Contains("variant"))
            {
                symbols.Add(new SymbolInfo(
                    enumMatch.Groups[1].Value,
                    SymbolKind.Enum,
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + enumMatch.Length)),
                    new Range(new Position(lineNum, indent), new Position(lineNum, indent + enumMatch.Length))));
            }

            // def name(...) : type { }
            var defMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+|mutable\s+)?def\s+(\w[\w']*)");
            if (defMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    defMatch.Groups[1].Value,
                    SymbolKind.Function,
                    new Range(new Position(lineNum, indent + defMatch.Index), 
                              new Position(lineNum, indent + defMatch.Index + defMatch.Length)),
                    new Range(new Position(lineNum, indent), 
                              new Position(lineNum, indent + defMatch.Index + defMatch.Length))));
            }

            // macro X(...) { }
            var macroMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^(?:public\s+|private\s+|internal\s+)?macro\s+(\w[\w']*)");
            if (macroMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    macroMatch.Groups[1].Value,
                    SymbolKind.Function,
                    new Range(new Position(lineNum, indent + macroMatch.Index), 
                              new Position(lineNum, indent + macroMatch.Index + macroMatch.Length)),
                    new Range(new Position(lineNum, indent), 
                              new Position(lineNum, indent + macroMatch.Index + macroMatch.Length))));
            }

            // mutable x = ... (at top level)
            var mutMatch = System.Text.RegularExpressions.Regex.Match(trimmed,
                @"^mutable\s+(\w[\w']*)\s*:");
            if (mutMatch.Success)
            {
                symbols.Add(new SymbolInfo(
                    mutMatch.Groups[1].Value,
                    SymbolKind.Field,
                    new Range(new Position(lineNum, indent + mutMatch.Index), 
                              new Position(lineNum, indent + mutMatch.Index + mutMatch.Length)),
                    new Range(new Position(lineNum, indent), 
                              new Position(lineNum, indent + mutMatch.Index + mutMatch.Length))));
            }
        }

        return symbols;
    }
}
