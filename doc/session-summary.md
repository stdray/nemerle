# Session Summary — reanemerle

## Tokens / Keys
- **YobaLog API Key:** `_hJAqQ2SGUOMfucTWyX36w` (wildcard, workspace=nemerle-lsp)
- **YobaLog Seq-compat URL:** `https://yobalog.3po.su/compat/seq/nemerle-lsp`
- **YobaLog Native API:** `https://yobalog.3po.su/api/v1/workspaces/nemerle-lsp/query`
- **NuGet PackageId:** `stdray.Nemerle.Compiler`
- **NuGet API Key:** in CI secrets (`NUGET_API_KEY`)
- **VSCode Extension:** `nemerle.nemerle-vscode` v0.1.0 at `~/.vscode/extensions/`
- **Portable VSCode:** `D:\programs\vscode`

## Problem List

### 1. MacroPhase.BeforeInheritance not resolving → FIXED
- **File:** `ManagerClass.n:616` — LoadNemerleMacros used dynamic version from compiler assembly (v=0.1.0.0) but Nemerle.Macros.dll is v=0.0.0.0 → FileNotFoundException
- **Fix:** hardcode version `0.0.0.0` (ManagerClass.n, commit `8d7fff6a9`)
- **File:** `Macros.n` — missing `using Nemerle;` → MacroPhase not in scope
- **Fix:** added `using Nemerle;` (commit `8d7fff6a9`)

### 2. Duplicate type warnings flooding Recovery → FIXED
- **File:** `NamespaceTree.Node.Hacks.n:27` — "defined in more than one assembly" warnings for type-forwards to same assembly counted toward Recovery limit
- **Fix:** compare `DefinitionAssembly.FullNameToken` — if same assembly, silently skip (commit `7ae3092e4`)

### 3. "macro with phase modifier must operate on type declaration parts" → REVERTED
- **File:** `MacroClassGen.n:455` — added target_type_suff inference from [MacroUsage] attribute
- **Problem:** caused SyntaxElement/parser errors for macros with TypeBuilder parameter
- **Fix:** reverted inference (commit `70ac51719`), added `tb : TypeBuilder` to test macros instead

### 4. Project files not loaded into Engine → FIXED
- **File:** `NprojLoader.cs` — MSBuild evaluation failed (SDK not found), CompilePatterns empty
- **Fix:** XML fallback parsing for `<Compile>` and `<Reference>` items, auto-add extension bin DLLs (commits `7d7bd3aca`, `c710edd03`)

### 5. EngineBridge hover: error:no_source → FIXED
- **File:** `LspIdeProject.n:TryFindSource` — URI mismatch: `file:///D:/...` vs `file:///d%3A/...`
- **Root cause:** `Uri.LocalPath` gives different formats (`/D:/...` vs `D:\...`)
- **Fix:** `UriToKey()` — strip leading `/`, replace `\`→`/`, `ToUpperInvariant()` (commit `c710edd03`)

### 6. Nemerle `when` doesn't return from loop → FIXED
- **File:** `LspIdeProject.n:TryFindSource` — `when (cond) Some(val)` evaluates discards value, loop continues to `None()`
- **Fix:** mutable `found = None()` with assignment in loop (commit `c710edd03`)

### 7. EngineBridge hover: error:null_tip → PARTIAL
- **File:** `EngineBridge.n:GetHoverText` — Engine's BeginGetQuickTipInfo returns null (method not type-checked)
- **Partial fix:** `RebuildProject` waits for BeginReloadProject (60s), added retry loop
- **Remaining:** engine needs CheckMethod before QuickTipInfo is available

### 8. Compiler-based hover replacing EngineBridge → DONE
- **File:** `EngineHost.cs:GetHoverInfo` — compiler type lookup + System.Type.GetType fallback + source-context
- **File:** `ServerState.cs:GetHoverAsync` — replaced EngineBridge with compiler-based hover
- **Tests:** 3/3 pass (compile, hover, hover on string→System.String)

### 9. Exception logging → FIXED
- **Files:** `EngineHost.cs` — replaced 4x `catch { }` with `catch (Exception ex) { _logger.LogWarning(...) }`
- Added `LoggerTextWriter` for Console.Error → ILogger redirect

### 10. Version/build time in logs → FIXED
- **File:** `Program.cs` — log server version + build timestamp
- **File:** `extension.ts` — log extension version from package.json

### 11. Expression in quasiquotes fails in LSP → UNFIXED
- **Symptom:** `parse error near keyword 'def'` at macro body line
- **Root cause:** compiler's MacroClassGen generates code that corrupts parser state in LSP environment
- **Note:** same macros pass in compiler test suite — difference in compilation options/environment
- **Workaround:** simplified Macros.n (removed `def greeting`, inlined quasiquotation)

### 12. NuGet publish → PARTIAL
- CI pushes NuGet on `nuget` tag (v1.3.0-rc.10361/10366/10369)
- Server still deploys Stage1 compiler DLLs (not NuGet) for testing
- NuGet cache clear+restore not working reliably (10369 not in cache)

### 13. VSCode test infrastructure → DONE
- **File:** `VscodeTestTests.cs` — 3 tests: compile, hover, hover on string→System.String
- **File:** `ServerHarness.cs` — in-process pipe-based LSP test harness (pre-existing)
- Run: `dotnet test --filter VscodeTestTests`

## Key Nemerle Findings → doc/findings.md
- `when` doesn't return from loops
- `Uri.LocalPath` / vs \
- `==` for strings is value equality (not reference)
- `Hashtable[int,T]` — Nemerle type, not Dictionary
- Static methods accessible via reflection
- `System.Type.GetType("string")` fails, need keyword→CLR mapping
- `$(...)` string interpolation not `{...}`
- `catch { | e when e is Type => }` not `catch { | e is Type => }`
- `Option<T>.Value` not `.Length`/`[]`
