# AGENTS.md

Guidance for coding agents working in this repository.

## Language

Reply in the user's language.

## Status

`retarget-compiler` branch — net8.0 bootstrap. Last known: 440/452 positive (97%), 165/166 negative (99%).

## VSCode extension status

`ide/vscode/` — work in progress. Target: VSCode extension via LSP, modeled after Ionide/FsAutoComplete.
Plan: `doc/vscode-plan.md` — keep checkbox statuses up to date:
- Mark completed items with `[x]`
- Add new checkboxes as tasks emerge
- Update the test count in Status after each test run
Last known: not started.

## Git-ignored files

`AGENTS.md`, `doc/*.md`, and `dotnet-tools.json` are in `.gitignore`. Changes to these files will NOT be committed.

## Commands

- **Full build:** `dotnet-cake build.cake --target=Stage1`
- **Full CI (build + validate + test):** `dotnet-cake build.cake --target=CI`
- **Test:** `dotnet-cake build.cake --target=Test` — logs in `bin/Release/Tests/test-positive.log` + `test-negative.log`
- **Single test:** `dotnet-cake build.cake --target=Test --testFilter="testsuite/positive/codedom.n"`
- **Dev loop:** edit `.n` source → `dotnet build Nemerle.Compiler.nproj -c Release /p:Nemerle=boot-dnlib` → copy DLLs to test runner dir
- Cake tasks order: Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → PrepareStage1Sdk → Stage2 → Stage3 → Validate → BuildTestInfrastructure → Test → ReplaceBootstrap

## Architecture

- **Boot compiler:** `boot-dnlib/` — prebuilt .NET 8.0 compiler (`ncc-core.exe` native apphost, `ncc-core.dll` managed). Also contains 100+ framework DLLs from .NET Core 2.1 used as compilation references.
- **Compiler source:** `ncc/backend/` (codegen, dnlib-based), `ncc/frontend/` (parsing, typing, macros), `ncc/shared/` (AssemblyInfo).
- **Standard library:** `lib/` → `Nemerle.nproj` (netstandard2.0).
- **Compiler:** `Nemerle.Compiler.nproj` (netstandard2.0, dnlib 4.5.0, System.CodeDom 4.7.0).
- **Macros:** `Nemerle.Macros.nproj` (SDK-style, used). `Nemerle.Macros-dnlib.nproj` is old-style and NOT built by Cake.
- **Entry point:** `ncc-core.nproj` (net8.0, references Nemerle.Compiler + Nemerle).
- **MSBuild integration:** `Nemerle.MSBuild.Tasks.csproj` (netstandard2.0). SDK props/targets in `tools/msbuild-task/`, copied to boot and each stage.
- **Test runner:** `snippets/Nemerle.Test/Nemerle.Compiler.Test/` — hosts compiler in-process (HostedNcc, default) or spawns process (ExternalNcc, fallback via `-ncc` flag).
- All `.nproj` files use `<NoStdLib>true` — the compiler bootstraps itself, no implicit stdlib.

## Hard invariants

- **Boot compiler runs on .NET 8.0.** `ncc-core.runtimeconfig.json`: `"version": "8.0"`, `"rollForward": "LatestMajor"`.
- **`-use-loaded-corlib` is dead code.** Defined in CompilationOptions.n, never read.
- **No System.Security.Permissions shim.** `is_security_attribute` (`ncc/backend/generation/AttributeCompilerClass.n:354`) uses string comparison.
- **dnlib version:** NuGet `dnlib` 4.5.0 preferred. Boot-dnlib has 3.3.0. Stage1 copies 4.5.0 from NuGet cache (`~/.nuget/packages/dnlib/4.5.0/`).
- **System.CodeDom NuGet 8.0.0** pulled at test time for `codedom.n`. Without it, that test fails.
- **All Stage1-3 assemblies have the SAME version** (from `git describe --tags --long`). Boot-dnlib version differs — each stage is self-consistent.
- **EXE tests need `.runtimeconfig.json`.** Tests with `BEGIN-OUTPUT` are compiled as `.exe`. Cake generates `<name>.runtimeconfig.json` automatically.
- **Test runner needs `-r dotnet`** for EXE tests. Hardcoded in Cake Test task.
- **Shared `obj/` directory.** Cake assigns per-stage: `obj/Stage1`, `obj/Stage2`, `obj/Stage3`. Build individual projects with `--no-dependencies` after restoring all.
- **`ncc-core.exe` runs directly** (no `dotnet` wrapper). MSBuildTask searches `ncc-core.exe` → `ncc-core.dll` → `ncc.exe`.
- **All tests get `-nowarn:10003`.** Passed in Cake Test task.

## Validate

`dotnet-cake build.cake --target=Validate` compares Stage1 vs Stage2 IL via `dotnet-ildasm` (only prerequisite for this step). IL is normalized (labels, macro IDs, GUIDs stripped). Known minor diffs in macro IDs are expected.

## Documents

- **`doc/plan.md`** — Russian. What was built, what remains.
- **`doc/decision-log.md`** — Russian. Architectural decisions, newest first.
- **`doc/vscode-plan.md`** — VSCode extension implementation checklist. Keep checkbox statuses up to date.
- All three are gitignored.

## Coding style

- Nemerle source (`ncc/`) — functional/declarative, pattern matching over if-else.
- MSBuild targets in `tools/msbuild-task/` — canonical source; `boot-dnlib/` copies are for bootstrapping.
- Cake — small tasks, one concern each. Use `DotNet*` aliases (not `DotNetCore*`).
- **TypeScript/JS tooling:** use **bun** everywhere (`bun install`, `bunx tsc`, `bun test`). Do NOT use npm/npx.

## Commit convention

- **Subject:** `type(scope): short description`, ≤ 72 chars, imperative mood, no trailing period.
  - Types: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`, `revert`.
  - Scopes: `build`, `compiler`, `test`, `msbuild`, `cake`.
- **Body:** why + tricky bits. End with test run totals.
- Russian OK in commit bodies and decision-log; English in code strings.
