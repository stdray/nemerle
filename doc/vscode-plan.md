# VSCode Extension Implementation Plan

Model: Ionide/FsAutoComplete (F# LSP server + VSCode client).
Target: ~74 hours to working prototype.

## Status

Prototype works in VSCode: syntax highlight, diagnostics (real compiler), completion
(Engine-backed: semantic + lexical fallback), hover (Engine type info via HintMarkdownRenderer),
go-to-definition (Engine.GetGotoInfo), documentSymbol, compile/run commands.
17/17 LSP tests pass. CI: 440/452 positive (97%), 165/166 negative (99%).
NuGet: migrated from HintPath to PackageReference `stdray.Nemerle.Compiler` 1.3.0-rc.10341.
Recovery: 4 tests verify server survives compiler errors (Recovery.Tests) via EngineHost crash handler.
MSBuild nproj: NprojLoader uses Microsoft.Build.Evaluation.Project (no raw XML).

## 0. Preparation — Copy and retarget IntelliSense engine

- [x] 0.1 Copy `snippets/VS2010/Nemerle.Compiler.Utils/` → `ide/vscode/nemerle-language-core/`
- [x] 0.2 Remove WinForms/WPF files: `AstBrowserForm.n`, `CompiledUnitAstBrowser.n`, `CodeFormatting/Formatter.n`
- [x] 0.3 Create `Nemerle.Language.Core.nproj` (netstandard2.0, `<NoStdLib>true`)
- [x] 0.4 Replace `Assembly.LoadWithPartialName()` — removed EngineCallbackStub entirely (test-only stub)
- [x] 0.5 Replace `RuntimeEnvironment.GetRuntimeDirectory()` → `typeof(object).Assembly.Location` (2 files)
- [x] 0.6 Removed `AssemblyBuilder` check → `Assembly.IsDynamic`; dropped `using System.Reflection.Emit`
- [x] 0.7 Build verified: 0 errors, 7 pre-existing warnings

## 1. LSP Server

### 1.1 Project scaffolding — DONE
- [x] 1.1.1 Create `Nemerle.LanguageServer.csproj` (net8.0, `OutputType=Exe`, `ServerGarbageCollection=true`)
- [x] 1.1.2 Add NuGet: `Serilog`, `Serilog.Sinks.File`
- [x] 1.1.3 Add `PackageReference stdray.Nemerle.Compiler` (Nemerle+Compiler+Macros+dnlib) + `ProjectReference Nemerle.Language.Core`
- [x] 1.1.4 `Program.cs` — entry point, Serilog config, preload Nemerle assemblies, start server

### 1.2 Core state — DONE
- [x] 1.2.1 `ServerState.cs` — open documents, cached text, engine/completion/analysis engines
- [x] 1.2.2 `EngineHost.cs` — HostedNcc-style compiler wrapper: temp files, compile, collect diagnostics
- [x] 1.2.3 `LspTransport.cs` — stdin/stdout JSON-RPC reader/writer with LSP header parsing

### 1.3 Project system — MSBuild-based
- [x] 1.3.1 `ProjectSystem/NprojLoader.cs` — parse `.nproj` via MSBuild `Microsoft.Build.Evaluation.Project`, extract sources/refs/defines
- [x] 1.3.1b `ProjectSystem/NprojLoader.cs` — `ResolveProjectReferences` resolves `.nproj` → output DLL paths
- [ ] 1.3.2 `ProjectSystem/NuGetRestorer.cs` — run `dotnet restore` on project change (deferred, low priority)
- [ ] 1.3.3 `ProjectSystem/WorkspaceManager.cs` — discover `.nproj` in workspace root, track multiple projects (deferred)

### 1.4 Adapters (IIdeEngine ↔ LSP) — 🔴 PRIORITY #1

These are the key to replacing lexical stubs with real semantic IntelliSense
(from VS integration's `Nemerle.Compiler.Utils` engine):

- [x] 1.4.1 `Adapters/LspIdeSource.n` — in language-core, implements `IIdeSource` for LSP text buffer
- [x] 1.4.2 `Adapters/LspIdeProject.n` — in language-core, implements `IIdeProject` for LSP workspace
- [x] 1.4.3 `EngineBridge.n` — in language-core, manages Engine lifecycle: Initialize, Complete, GetDefinitions, GetHoverText
- [x] 1.4.4 `HintMarkdownRenderer.cs` — C# converter: WpfHint XML tags → Markdown (b,i,u,code,ref,pre,param,lb)
- [ ] 1.4.5 `Adapters/PositionMapper.cs` — `Location` ↔ `Position`, `NSpan` ↔ `Range`

### 1.5 LSP Handlers

- [x] 1.5.1 `Handlers/InitializeHandler.cs` — capabilities, workspace peek, loads .nproj
- [x] 1.5.2 `Handlers/TextDocumentHandler.cs` — `didOpen/Close/Change/Save`
- [x] 1.5.3 `Handlers/CompletionHandler.cs` — Engine.Completion() with lexical fallback
- [x] 1.5.4 `Handlers/HoverHandler.cs` — Engine.BeginGetQuickTipInfo() + HintMarkdownRenderer, lexical fallback
- [x] 1.5.5 `Handlers/DefinitionHandler.cs` — Engine.GetGotoInfo() with lexical fallback
- [x] 1.5.8 `Handlers/DocumentSymbolHandler.cs` — AnalysisEngine.GetDocumentSymbols
- [x] 1.5.9 `Handlers/DiagnosticHandler.cs` — EngineHost (HostedNcc-style), works well

Not yet implemented:
- [ ] 1.5.6 `Handlers/ReferencesHandler.cs` — needs Engine.FindAllSymbols()
- [ ] 1.5.7 `Handlers/SignatureHelpHandler.cs` — needs Engine.GetMethodTipInfo()
- [ ] 1.5.10 `Handlers/SemanticTokensHandler.cs` — needs Engine.UpdateTypeHighlightings() + Engine.HighlightUsages()

### 1.6 Custom endpoints (nemerle/*)
- [x] 1.6.1 `nemerle/compile` — compile current file, return diagnostics + success
- [x] 1.6.2 `nemerle/compileRun` — compile + run exe, return stdout/stderr
- [x] 1.6.3 VSCode commands: `Nemerle: Compile` + `Nemerle: Compile and Run`

## 2. Tests

### 2.1 Test infrastructure — DONE
- [x] 2.1.1 Create `Nemerle.LanguageServer.Tests.csproj` (net8.0, xUnit, AwesomeAssertions)
- [x] 2.1.2 `Infrastructure/ServerHarness.cs` — in-process pipe-based LSP test harness (init, didOpen, WaitForDiagnostics)
- [ ] 2.1.3 `Infrastructure/CursorExtractor.cs` — extract `$0` marker positions from test source

### 2.2 Test suites
- [x] 2.2.0 `RecoveryTests.cs` — recovery tests (server survives compiler errors, hover after Recovery, ProjectReference prevents Recovery) — 4 tests
- [x] 2.3.1 `DiagnosticsTests.cs` — error diagnostics, parse error ranges, multiple errors (3 tests)
- [x] 2.3.2 `CompletionTests.cs` — keywords, prefix filter, local identifiers, stdlib types, CompletionList (5 tests)
- [x] 2.3.3 `IntegrationTests.cs` — workspace+.nproj, multi-doc, rapid changes, variant types, completion in module (5 tests)
- [ ] 2.3.4 `HoverTests.cs`
- [ ] 2.3.5 `DefinitionTests.cs`
- [ ] 2.3.6 `EngineIntegrationTests.cs` — tests for real IIdeEngine after 1.4 adapters are done

## 3. VSCode Client Extension — DONE

- [x] 3.1 Create `package.json` — activation events, language config, LSP client config
- [x] 3.2 `src/extension.ts` — launch server process, register LSP client, handle config changes
- [x] 3.3 `language-configuration.json` — bracket auto-close, comment toggling (`//`, `/* */`)
- [x] 3.4 TextMate grammar — based on textmate/nemerle.tmbundle, extended with preprocessor, verbatim strings, etc.
- [x] 3.5 Snippets — 17 snippets (class, module, variant, match, fun, def, for, while, macro, etc.)
- [x] 3.6 Installed in VSCode, syntax highlighting confirmed working ✅

## 4. Build Integration — DONE

- [x] 4.1 Add `BuildVscode` task to `build.cake` (depends on Stage1)
- [x] 4.2 Add `TestVscode` task to `build.cake` (runs xUnit tests via `dotnet test`)
- [ ] 4.3 Configure `dotnet test` with `--logger trx` for CI (deferred)
- [ ] 4.4 Add `nemerle-language-server` to dotnet tool packaging (`PackAsTool=true`) (deferred)

## 5. Documentation

- [x] 5.1 Update `AGENTS.md` with VSCode extension status section
- [x] 5.2 Update `doc/decision-log.md` with architectural decisions from this plan
- [x] 5.3 Keep this checklist updated — mark completed items, add new tasks as they emerge

## 6. Compiler fixes (done alongside prototype)

- [x] 6.1 `LibraryReference.n:251` — `GetInternalType` handles `NotLoaded`/`NotLoadedList`/`CachedAmbiguous` (fixed ICE "not loaded internal type")
- [x] 6.2 `build.cake` — duplicate variable declarations in Test task fixed
- [x] 6.3 `Nemerle.Compiler.Test.nproj` — added `ProduceReferenceAssembly=false` for SDK 10 compatibility
- [x] 6.4 CI verified: 440/452 positive (97%), 165/166 negative (99%) — no regressions

## Current priorities (post-engine-integration)

1. ✅ **1.4 Adapters** — `LspIdeSource` + `LspIdeProject` (in language-core), `EngineBridge` (init + Complete + GetDefinitions + GetHover)
2. ✅ **1.5 Handlers** — Completion/Hover/Definition via Engine, with lexical fallback
3. ✅ **nemerle/compile + nemerle/compileRun** — LSP endpoints + VSCode commands
4. 🔲 **F5 / Ctrl+F5** — привязать к compile/compileRun
5. 🔲 **SignatureHelp** — `Engine.GetMethodTipInfo()` → подсказка параметров
6. 🔲 **References** — `Engine.FindAllSymbols()` + `Engine.HighlightUsages()`
7. 🔲 **SemanticTokens** — `SyntaxClassifier`/`TypeClassifier`/`UsageClassifier` из VS-интеграции
8. 🔲 **Macro expansion via Virtual Documents** — `TextDocumentContentProvider` + команда `nemerle.expandMacro`
9. 🔲 **Deferred** — NuGetRestorer, WorkspaceManager, dotnet tool packaging

## 7. Macro Expansion (Virtual Documents)

Цель: рекурсивное раскрытие макросов с навигацией через LSP — вместо «каскада баблов» (невозможно в VSCode API).

- [ ] 7.1 `HintMarkdownRenderer` — рендеринг `<hint value='After expanding'>` как ссылку-команду `[Expand](command:nemerle.expandMacro?...)`
- [ ] 7.2 `EngineBridge.ExpandMacro(uri, line, col) : string` — вызывает Engine для получения раскрытого кода макроса
- [ ] 7.3 `nemerle/macroExpand` LSP handler — возвращает раскрытый текст
- [ ] 7.4 `NemerleMacroContentProvider` — `TextDocumentContentProvider`, схема `nemerle-macro://`
- [ ] 7.5 `extension.ts` — команда `nemerle.expandMacro`: запрос к LSP → виртуальный документ
- [ ] 7.6 Виртуальный документ поддерживает LSP (hover, definition, completion) → рекурсивное раскрытие

## Extra tasks completed (not in original plan)
- [x] Fix compiler: `GetInternalType` handles `NotLoaded`/`NotLoadedList`/`CachedAmbiguous` (LibraryReference.n:251)
- [x] Fix Cake: duplicate variable declarations, BuildVscode SDK path
- [x] Fix: `ProduceReferenceAssembly=false` in Nemerle.Compiler.Test.nproj
- [x] Reretarget: language-core rebuilt with boot-dnlib 1.2.0.829, 0 MSB3277 warnings
- [x] Fix: BOM in LSP transport (`UTF8Encoding(false)`)
- [x] Fix: `extPath` server discovery path (one `..` not two)
- [x] Fix: definition URI hardcoded to `file:///test/t.n`
- [x] Recovery tests: EngineHost crash handler + 4 RecoveryTests (Recovery, hover, ProjectReference, NprojLoader)
- [x] MSBuild nproj: `NprojLoader.cs` uses `Microsoft.Build.Evaluation.Project` (not raw XML)
- [x] NuGet: migrate from HintPath to `<PackageReference Include="stdray.Nemerle.Compiler">` + `ExcludeAssets="build;buildTransitive"`
- [x] Tests: remove HintPath to Nemerle.Macros (transitive via PackageReference)
- [x] build.cake BuildVscode: remove NemerleBin property and manual DLL copy
