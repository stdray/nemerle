using System.Text.Json.Serialization;

namespace Nemerle.LanguageServer;

public record Position(int Line, int Character);

public record Range(Position Start, Position End);

public record TextDocumentIdentifier(string Uri);

public record VersionedTextDocumentIdentifier(string Uri, int Version) : TextDocumentIdentifier(Uri);

public record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

public record Location(string Uri, Range Range);

public record Diagnostic
{
    public required Range Range { get; init; }
    public DiagnosticSeverity? Severity { get; init; }
    public string? Code { get; init; }
    public string? Source { get; init; }
    public required string Message { get; init; }
}

public enum DiagnosticSeverity { Error = 1, Warning = 2, Information = 3, Hint = 4 }

public record CompletionParams(TextDocumentIdentifier TextDocument, Position Position)
{
    public CompletionContext? Context { get; init; }
}

public record CompletionContext(CompletionTriggerKind TriggerKind, string? TriggerCharacter = null);

public enum CompletionTriggerKind { Invoked = 1, TriggerCharacter = 2, TriggerForIncompleteCompletions = 3 }

public record CompletionItem
{
    public required string Label { get; init; }
    public CompletionItemKind? Kind { get; init; }
    public string? Detail { get; init; }
    public string? InsertText { get; init; }
    public string? Documentation { get; init; }
}

public enum CompletionItemKind
{
    Text = 1, Method = 2, Function = 3, Constructor = 4, Field = 5,
    Variable = 6, Class = 7, Interface = 8, Module = 9, Property = 10,
    Unit = 11, Value = 12, Enum = 13, Keyword = 14, Snippet = 15,
    Color = 16, File = 17, Reference = 18, Folder = 19, EnumMember = 20,
    Constant = 21, Struct = 22, Event = 23, Operator = 24, TypeParameter = 25
}

public record CompletionList(bool IsIncomplete, CompletionItem[] Items);

public record HoverParams(TextDocumentIdentifier TextDocument, Position Position);

public record Hover(string Contents, Range? Range = null);

public record DefinitionParams(TextDocumentIdentifier TextDocument, Position Position);

public record DocumentSymbolParams(TextDocumentIdentifier TextDocument);

public record InitializeParams
{
    public int? ProcessId { get; init; }
    public string? RootPath { get; init; }
    public string? RootUri { get; init; }
    public ClientCapabilities? Capabilities { get; init; }
}

public record ClientCapabilities
{
    public TextDocumentClientCapabilities? TextDocument { get; init; }
}

public record TextDocumentClientCapabilities
{
    public CompletionClientCapabilities? Completion { get; init; }
    public HoverClientCapabilities? Hover { get; init; }
}

public record CompletionClientCapabilities
{
    public CompletionItemCapabilities? CompletionItem { get; init; }
}

public record CompletionItemCapabilities
{
    public string[]? DocumentationFormat { get; init; }
    public bool? SnippetSupport { get; init; }
}

public record HoverClientCapabilities
{
    public string[]? ContentFormat { get; init; }
}

public record InitializeResult
{
    public ServerCapabilities Capabilities { get; init; } = new();
    public ServerInfo ServerInfo { get; init; } = new();
}

public record ServerCapabilities
{
    public bool HoverProvider { get; init; }
    public CompletionOptions? CompletionProvider { get; init; }
    public bool DefinitionProvider { get; init; }
    public bool ReferencesProvider { get; init; }
    public bool DocumentSymbolProvider { get; init; }
    public SignatureHelpOptions? SignatureHelpProvider { get; init; }
    public TextDocumentSyncOptions? TextDocumentSync { get; init; }
}

public record CompletionOptions(bool ResolveProvider = false, string[]? TriggerCharacters = null);

public record SignatureHelpOptions(string[]? TriggerCharacters = null);

public record TextDocumentSyncOptions
{
    public bool OpenClose { get; init; } = true;
    public TextDocumentSyncKind Change { get; init; } = TextDocumentSyncKind.Full;
    public bool WillSave { get; init; }
}

public enum TextDocumentSyncKind { None = 0, Full = 1, Incremental = 2 }

public record ServerInfo(string Name = "nemerle-language-server", string? Version = null);

public record DidOpenTextDocumentParams(TextDocumentItem TextDocument);
public record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);
public record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, TextDocumentContentChangeEvent[] ContentChanges);
public record TextDocumentContentChangeEvent(string Text);
public record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument);

public record PublishDiagnosticsParams(string Uri, Diagnostic[] Diagnostics);

public class LspNotification(string method, object? @params = null)
{
    public string Method => method;
    public object? Params => @params;
}

public class LspResponse(int id, object? result)
{
    public int Id => id;
    public object? Result => result;
}

public class LspRequest
{
    public int Id { get; init; }
    public required string Method { get; init; }
    public object? Params { get; init; }
}

[JsonSerializable(typeof(LspRequest))]
[JsonSerializable(typeof(LspNotification))]
[JsonSerializable(typeof(LspResponse))]
[JsonSerializable(typeof(InitializeParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(HoverParams))]
[JsonSerializable(typeof(Hover))]
[JsonSerializable(typeof(DefinitionParams))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(Diagnostic))]
public partial class LspJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
