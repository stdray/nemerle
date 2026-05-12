import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import {
    ExtensionContext, workspace, window, commands, Uri, OutputChannel,
    TextDocumentContentProvider, EventEmitter, Event, TextDocumentChangeEvent,
    ViewColumn
} from 'vscode';
import {
    LanguageClient, LanguageClientOptions, ServerOptions, TransportKind, Executable
} from 'vscode-languageclient/node';

let client: LanguageClient | null = null;
let outputChannel: OutputChannel;

class NemerleMacroContentProvider implements TextDocumentContentProvider {
    private _onDidChange = new EventEmitter<Uri>();
    get onDidChange(): Event<Uri> { return this._onDidChange.event; }

    private _expansions = new Map<string, string>();

    provideTextDocumentContent(uri: Uri): string {
        return this._expansions.get(uri.toString()) ?? '// No expansion available';
    }

    setExpansion(uri: Uri, text: string) {
        this._expansions.set(uri.toString(), text);
        this._onDidChange.fire(uri);
    }
}

let macroProvider: NemerleMacroContentProvider;

export function activate(context: ExtensionContext) {
    outputChannel = window.createOutputChannel('Nemerle Language Server');
    const pkg = context.extension.packageJSON;
    outputChannel.appendLine(`Nemerle extension v${pkg.version} activating...`);

    const serverExe = findServerExe();
    if (!serverExe) {
        void window.showErrorMessage(
            'Nemerle language server not found. ' +
            'Run build.cake --target=BuildVscode or set nemerle.server.path in settings.'
        );
        outputChannel.appendLine('ERROR: Server executable not found');
        return;
    }

    outputChannel.appendLine(`Server path: ${serverExe}`);

    const dotnetArgs = workspace.getConfiguration('nemerle.server').get<string[]>('dotnetArgs', []);

    let serverOptions: ServerOptions;
    if (serverExe.endsWith('.dll')) {
        serverOptions = {
            command: 'dotnet',
            args: [serverExe, ...dotnetArgs],
            transport: TransportKind.stdio
        };
    } else {
        serverOptions = {
            command: serverExe,
            args: dotnetArgs,
            transport: TransportKind.stdio
        };
    }

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'nemerle' }, { scheme: 'untitled', language: 'nemerle' }],
        synchronize: {
            fileEvents: workspace.createFileSystemWatcher('**/*.nproj')
        },
        outputChannel: outputChannel
    };

    client = new LanguageClient(
        'nemerle-language-server',
        'Nemerle Language Server',
        serverOptions,
        clientOptions
    );

    client.start().then(() => {
        outputChannel.appendLine('Nemerle language server ready');
    }).catch((err) => {
        outputChannel.appendLine(`Server start FAILED: ${err}`);
    });

    client.onDidChangeState((e) => {
        outputChannel.appendLine(`Client state: ${e.oldState} -> ${e.newState}`);
    });

    client.onNotification('textDocument/publishDiagnostics', (params: any) => {
        const uri = params.uri;
        const count = params.diagnostics?.length ?? 0;
        if (count > 0) {
            outputChannel.appendLine(`Diagnostics for ${uri}: ${count} issue(s)`);
            for (const d of params.diagnostics.slice(0, 5)) {
                outputChannel.appendLine(`  [${d.severity}] L${d.range.start.line}: ${d.message}`);
            }
        }
    });

    client.onNotification('window/logMessage', (params: any) => {
        outputChannel.appendLine(`[Server] ${params.type}: ${params.message}`);
    });

    // Register Virtual Document provider for macro expansion
    macroProvider = new NemerleMacroContentProvider();
    const disposable = workspace.registerTextDocumentContentProvider('nemerle-macro', macroProvider);

    context.subscriptions.push(disposable);

    // Register compile commands
    context.subscriptions.push(
        commands.registerCommand('nemerle.compile', async () => {
            const editor = window.activeTextEditor;
            if (!editor || editor.document.languageId !== 'nemerle') {
                void window.showWarningMessage('No Nemerle file open');
                return;
            }
            if (!client) return;
            const result = await client.sendRequest('nemerle/compile', {
                textDocument: { uri: editor.document.uri.toString() }
            });
            outputChannel.show();
            outputChannel.appendLine((result as any).output);
            const success = (result as any).success;
            if (success) void window.showInformationMessage('Compilation successful');
            else void window.showErrorMessage(`Compilation failed with ${(result as any).errorCount} error(s)`);
        }),
        commands.registerCommand('nemerle.compileRun', async () => {
            const editor = window.activeTextEditor;
            if (!editor || editor.document.languageId !== 'nemerle') {
                void window.showWarningMessage('No Nemerle file open');
                return;
            }
            if (!client) return;
            const result = await client.sendRequest('nemerle/compileRun', {
                textDocument: { uri: editor.document.uri.toString() }
            });
            outputChannel.show();
            outputChannel.appendLine((result as any).output);
            if ((result as any).success) void window.showInformationMessage('Run successful');
            else void window.showErrorMessage('Run failed');
        }),
        commands.registerCommand('nemerle.expandMacro', async (params: { line: number; col: number }) => {
            const editor = window.activeTextEditor;
            if (!editor || !client) return;
            const uri = editor.document.uri.toString();
            const query = `${uri}?line=${params.line}&col=${params.col}`;
            const result = await client.sendRequest('nemerle/macroExpand', {
                textDocument: { uri: query }
            });
            const text = (result as any).text || '// No expansion';
            const vscUri = Uri.parse(`nemerle-macro://expand/${params.line}_${params.col}.n`);
            macroProvider.setExpansion(vscUri, text);
            const doc = await workspace.openTextDocument(vscUri);
            await window.showTextDocument(doc, ViewColumn.Beside);
        })
    );
}

export function deactivate(): Thenable<void> | undefined {
    outputChannel?.appendLine('Nemerle extension deactivating');
    return client?.stop();
}

function findServerExe(): string | null {
    const extPath = path.resolve(__dirname, '..');

    // 1. User-specified path
    const configured = workspace.getConfiguration('nemerle.server').get<string>('path');
    if (configured && fs.existsSync(configured)) {
        return configured;
    }

    // 2. In the extension bundle
    const candidates = [
        path.join(extPath, 'bin', 'nemerle-language-server.exe'),
        path.join(extPath, 'bin', 'nemerle-language-server.dll')
    ];
    for (const c of candidates) {
        if (fs.existsSync(c)) return c;
    }

    // 3. In the repository build output (for development)
    const repoRoot = path.resolve(extPath, '..', '..', '..');
    const devCandidates = [
        path.join(repoRoot, 'ide', 'vscode', 'nemerle-language-server', 'bin', 'Release', 'net8.0', 'nemerle-language-server.exe'),
        path.join(repoRoot, 'ide', 'vscode', 'nemerle-language-server', 'bin', 'Release', 'net8.0', 'nemerle-language-server.dll'),
        path.join(repoRoot, 'bin', 'Release', 'nemerle-language-server.exe'),
        path.join(repoRoot, 'bin', 'Release', 'nemerle-language-server.dll')
    ];
    for (const c of devCandidates) {
        if (fs.existsSync(c)) return c;
    }

    // 4. Global dotnet tool
    const toolPath = path.join(os.homedir(), '.dotnet', 'tools', 'nemerle-language-server.exe');
    if (fs.existsSync(toolPath)) return toolPath;
    const toolPathDll = path.join(os.homedir(), '.dotnet', 'tools', 'nemerle-language-server.dll');
    if (fs.existsSync(toolPathDll)) return toolPathDll;

    return null;
}
