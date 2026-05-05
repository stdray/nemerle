import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';
import {
    ExtensionContext, workspace, window, commands, OutputChannel
} from 'vscode';
import {
    LanguageClient, LanguageClientOptions, ServerOptions, TransportKind, Executable
} from 'vscode-languageclient/node';

let client: LanguageClient | null = null;
let outputChannel: OutputChannel;

export function activate(context: ExtensionContext) {
    outputChannel = window.createOutputChannel('Nemerle Language Server');
    outputChannel.appendLine('Nemerle extension activating...');

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
    });

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
