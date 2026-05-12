"use strict";
var __createBinding = (this && this.__createBinding) || (Object.create ? (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    var desc = Object.getOwnPropertyDescriptor(m, k);
    if (!desc || ("get" in desc ? !m.__esModule : desc.writable || desc.configurable)) {
      desc = { enumerable: true, get: function() { return m[k]; } };
    }
    Object.defineProperty(o, k2, desc);
}) : (function(o, m, k, k2) {
    if (k2 === undefined) k2 = k;
    o[k2] = m[k];
}));
var __setModuleDefault = (this && this.__setModuleDefault) || (Object.create ? (function(o, v) {
    Object.defineProperty(o, "default", { enumerable: true, value: v });
}) : function(o, v) {
    o["default"] = v;
});
var __importStar = (this && this.__importStar) || (function () {
    var ownKeys = function(o) {
        ownKeys = Object.getOwnPropertyNames || function (o) {
            var ar = [];
            for (var k in o) if (Object.prototype.hasOwnProperty.call(o, k)) ar[ar.length] = k;
            return ar;
        };
        return ownKeys(o);
    };
    return function (mod) {
        if (mod && mod.__esModule) return mod;
        var result = {};
        if (mod != null) for (var k = ownKeys(mod), i = 0; i < k.length; i++) if (k[i] !== "default") __createBinding(result, mod, k[i]);
        __setModuleDefault(result, mod);
        return result;
    };
})();
Object.defineProperty(exports, "__esModule", { value: true });
exports.activate = activate;
exports.deactivate = deactivate;
const path = __importStar(require("path"));
const fs = __importStar(require("fs"));
const os = __importStar(require("os"));
const vscode_1 = require("vscode");
const node_1 = require("vscode-languageclient/node");
let client = null;
let outputChannel;
class NemerleMacroContentProvider {
    _onDidChange = new vscode_1.EventEmitter();
    get onDidChange() { return this._onDidChange.event; }
    _expansions = new Map();
    provideTextDocumentContent(uri) {
        return this._expansions.get(uri.toString()) ?? '// No expansion available';
    }
    setExpansion(uri, text) {
        this._expansions.set(uri.toString(), text);
        this._onDidChange.fire(uri);
    }
}
let macroProvider;
function activate(context) {
    outputChannel = vscode_1.window.createOutputChannel('Nemerle Language Server');
    const pkg = context.extension.packageJSON;
    outputChannel.appendLine(`Nemerle extension v${pkg.version} activating...`);
    const serverExe = findServerExe();
    if (!serverExe) {
        void vscode_1.window.showErrorMessage('Nemerle language server not found. ' +
            'Run build.cake --target=BuildVscode or set nemerle.server.path in settings.');
        outputChannel.appendLine('ERROR: Server executable not found');
        return;
    }
    outputChannel.appendLine(`Server path: ${serverExe}`);
    const dotnetArgs = vscode_1.workspace.getConfiguration('nemerle.server').get('dotnetArgs', []);
    let serverOptions;
    if (serverExe.endsWith('.dll')) {
        serverOptions = {
            command: 'dotnet',
            args: [serverExe, ...dotnetArgs],
            transport: node_1.TransportKind.stdio
        };
    }
    else {
        serverOptions = {
            command: serverExe,
            args: dotnetArgs,
            transport: node_1.TransportKind.stdio
        };
    }
    const clientOptions = {
        documentSelector: [{ scheme: 'file', language: 'nemerle' }, { scheme: 'untitled', language: 'nemerle' }],
        synchronize: {
            fileEvents: vscode_1.workspace.createFileSystemWatcher('**/*.nproj')
        },
        outputChannel: outputChannel
    };
    client = new node_1.LanguageClient('nemerle-language-server', 'Nemerle Language Server', serverOptions, clientOptions);
    client.start().then(() => {
        outputChannel.appendLine('Nemerle language server ready');
    }).catch((err) => {
        outputChannel.appendLine(`Server start FAILED: ${err}`);
    });
    client.onDidChangeState((e) => {
        outputChannel.appendLine(`Client state: ${e.oldState} -> ${e.newState}`);
    });
    client.onNotification('textDocument/publishDiagnostics', (params) => {
        const uri = params.uri;
        const count = params.diagnostics?.length ?? 0;
        if (count > 0) {
            outputChannel.appendLine(`Diagnostics for ${uri}: ${count} issue(s)`);
            for (const d of params.diagnostics.slice(0, 5)) {
                outputChannel.appendLine(`  [${d.severity}] L${d.range.start.line}: ${d.message}`);
            }
        }
    });
    client.onNotification('window/logMessage', (params) => {
        outputChannel.appendLine(`[Server] ${params.type}: ${params.message}`);
    });
    // Register Virtual Document provider for macro expansion
    macroProvider = new NemerleMacroContentProvider();
    const disposable = vscode_1.workspace.registerTextDocumentContentProvider('nemerle-macro', macroProvider);
    context.subscriptions.push(disposable);
    // Register compile commands
    context.subscriptions.push(vscode_1.commands.registerCommand('nemerle.compile', async () => {
        const editor = vscode_1.window.activeTextEditor;
        if (!editor || editor.document.languageId !== 'nemerle') {
            void vscode_1.window.showWarningMessage('No Nemerle file open');
            return;
        }
        if (!client)
            return;
        const result = await client.sendRequest('nemerle/compile', {
            textDocument: { uri: editor.document.uri.toString() }
        });
        outputChannel.show();
        outputChannel.appendLine(result.output);
        const success = result.success;
        if (success)
            void vscode_1.window.showInformationMessage('Compilation successful');
        else
            void vscode_1.window.showErrorMessage(`Compilation failed with ${result.errorCount} error(s)`);
    }), vscode_1.commands.registerCommand('nemerle.compileRun', async () => {
        const editor = vscode_1.window.activeTextEditor;
        if (!editor || editor.document.languageId !== 'nemerle') {
            void vscode_1.window.showWarningMessage('No Nemerle file open');
            return;
        }
        if (!client)
            return;
        const result = await client.sendRequest('nemerle/compileRun', {
            textDocument: { uri: editor.document.uri.toString() }
        });
        outputChannel.show();
        outputChannel.appendLine(result.output);
        if (result.success)
            void vscode_1.window.showInformationMessage('Run successful');
        else
            void vscode_1.window.showErrorMessage('Run failed');
    }), vscode_1.commands.registerCommand('nemerle.expandMacro', async (params) => {
        const editor = vscode_1.window.activeTextEditor;
        if (!editor || !client)
            return;
        const uri = editor.document.uri.toString();
        const query = `${uri}?line=${params.line}&col=${params.col}`;
        const result = await client.sendRequest('nemerle/macroExpand', {
            textDocument: { uri: query }
        });
        const text = result.text || '// No expansion';
        const vscUri = vscode_1.Uri.parse(`nemerle-macro://expand/${params.line}_${params.col}.n`);
        macroProvider.setExpansion(vscUri, text);
        const doc = await vscode_1.workspace.openTextDocument(vscUri);
        await vscode_1.window.showTextDocument(doc, vscode_1.ViewColumn.Beside);
    }));
}
function deactivate() {
    outputChannel?.appendLine('Nemerle extension deactivating');
    return client?.stop();
}
function findServerExe() {
    const extPath = path.resolve(__dirname, '..');
    // 1. User-specified path
    const configured = vscode_1.workspace.getConfiguration('nemerle.server').get('path');
    if (configured && fs.existsSync(configured)) {
        return configured;
    }
    // 2. In the extension bundle
    const candidates = [
        path.join(extPath, 'bin', 'nemerle-language-server.exe'),
        path.join(extPath, 'bin', 'nemerle-language-server.dll')
    ];
    for (const c of candidates) {
        if (fs.existsSync(c))
            return c;
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
        if (fs.existsSync(c))
            return c;
    }
    // 4. Global dotnet tool
    const toolPath = path.join(os.homedir(), '.dotnet', 'tools', 'nemerle-language-server.exe');
    if (fs.existsSync(toolPath))
        return toolPath;
    const toolPathDll = path.join(os.homedir(), '.dotnet', 'tools', 'nemerle-language-server.dll');
    if (fs.existsSync(toolPathDll))
        return toolPathDll;
    return null;
}
//# sourceMappingURL=extension.js.map