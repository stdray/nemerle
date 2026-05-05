using System.Text;
using System.Text.RegularExpressions;

namespace Nemerle.LanguageServer;

/// <summary>
/// Converts WpfHint XML format to LSP Markdown for hover tooltips.
/// WpfHint tags: <hint>, <keyword>, <b>, <i>, <u>, <code>, <ref>, <params>, <lb/>
/// </summary>
public static partial class HintMarkdownRenderer
{
    [GeneratedRegex(@"<lb\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex LbRegex();

    [GeneratedRegex(@"<keyword>(.*?)</keyword>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex KeywordRegex();

    [GeneratedRegex(@"<b>(.*?)</b>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"<i>(.*?)</i>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"<code>(.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"<ref>(.*?)</ref>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RefRegex();

    [GeneratedRegex(@"<pre>(.*?)</pre>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex PreRegex();

    [GeneratedRegex(@"<hint\b[^>]*>|</hint>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HintTagRegex();

    [GeneratedRegex(@"<u>(.*?)</u>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnderlineRegex();

    [GeneratedRegex(@"<hint\s+value\s*=\s*'[^']*'\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HintValueRegex();

    [GeneratedRegex(@"<params>(.*?)</params>", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ParamsRegex();

    [GeneratedRegex(@"<param>\s*<b>(.*?)</b>\s*(.*?)\s*</param>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ParamRegex();

    public static string ToMarkdown(string? wpfHintXml)
    {
        if (string.IsNullOrWhiteSpace(wpfHintXml)) return "";

        var sb = new StringBuilder(wpfHintXml);

        // Replace block-level breaks with real newlines
        ReplaceAll(sb, LbRegex(), "\n");

        // Replace formatting tags with Markdown equivalents
        ReplaceAll(sb, BoldRegex(), "**$1**");
        ReplaceAll(sb, ItalicRegex(), "*$1*");
        ReplaceAll(sb, CodeRegex(), "`$1`");
        ReplaceAll(sb, UnderlineRegex(), "_$1_");
        ReplaceAll(sb, RefRegex(), "`$1`");
        ReplaceAll(sb, KeywordRegex(), "**$1**");

        // Replace <pre> with code blocks
        ReplaceAll(sb, PreRegex(), "\n```\n$1\n```\n");

        // Replace <param> with table-like format
        ReplaceAll(sb, ParamRegex(), "- **$1** $2");

        // Replace <params> container — just keep its content
        ReplaceAll(sb, ParamsRegex(), "$1");

        // Remove <hint> tags and hint attributes
        ReplaceAll(sb, HintTagRegex(), "");
        ReplaceAll(sb, HintValueRegex(), "");

        // Clean up: remove duplicate newlines
        var result = sb.ToString();
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        result = result.Trim();

        return result;
    }

    private static void ReplaceAll(StringBuilder sb, Regex regex, string replacement)
    {
        var text = sb.ToString();
        var replaced = regex.Replace(text, replacement);
        if (replaced != text)
        {
            sb.Clear();
            sb.Append(replaced);
        }
    }
}
