# AGENTS.md

Guidance for coding agents working in this repository.

## Status: retarget-compiler branch — net8.0 bootstrap, tests: 440/452 positive (97%), 165/166 negative (99%)

The `retarget-compiler` branch replaces `System.Reflection.Emit` (.NET Framework) with **dnlib** (.NET Core).
The boot compiler (`boot-dnlib/`) runs on .NET 8.0 (via `ncc-core.exe` apphost). Stage 1-3 compiles the compiler from source sequentially.
Test runner (`Nemerle.Compiler.Test`) runs positive/negative test suites via `HostedNcc` (in-process) or `ExternalNcc` (spawned).

## Build entry points

- **Local:** `dotnet-cake build.cake --target=Stage1` — full bootstrap + compiler build.
- **Cake tasks:** Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → PrepareStage1Sdk → Stage2 → Stage3 → Validate → BuildTestInfrastructure → Test → ReplaceBootstrap.
- **Test:** `dotnet-cake build.cake --target=Test` — runs test suites in parallel, logs to `bin/Release/Tests/test-positive.log` + `test-negative.log`.
- **Quick test (single file):** `dotnet-cake build.cake --target=Test --testFilter="testsuite/positive/codedom.n"`
- **Dev rebuild loop:** edit `.n` source → `dotnet build Nemerle.Compiler.nproj -c Release /p:Nemerle=boot-dnlib` → copy DLLs to test runner dir.

## Temp files — NEVER pollute project root

- All build artifacts live in `bin/` (gitignored)
- Test output logs automatically saved to `bin/Release/Tests/test-*.log`
- Ad-hoc scripts/output write to system temp dir (`/tmp/` or `%TEMP%`), NOT project root

## Prerequisites

- .NET SDK 8.0+ (for MSBuild tasks + runtime; `Nemerle.MSBuild.Tasks.dll` targets `netstandard2.0`)
- `dotnet-cake` tool (`dotnet tool install -g Cake.Tool`)
- `dotnet-ildasm` tool (`dotnet tool install -g dotnet-ildasm`) — for IL validation

## Documents — what goes where

- **`doc/plan.md`** — what we've built (Stage 1, boot compiler, MSBuild tasks, test infra), what remains.
- **`doc/decision-log.md`** — every architectural decision with date / decision / reason / what was rolled back. **Newest entries on top.**
- **`AGENTS.md`** — this file; guidance for agents, invariants, build entry points.

## Hard invariants (easy to violate — read before coding)

- **Boot compiler runs on .NET 8.0.** `boot-dnlib/ncc-core.exe` is a native apphost (net8.0 TFM). The `-use-loaded-corlib` flag is dead code (never read in compiler source).
- **No System.Security.Permissions shim.** The shim DLL was removed — `is_security_attribute` in AttributeCompilerClass.n uses string comparison against `dnlib` types only.
- **All Stage1-3 assemblies have the SAME version** (determined by `git describe --tags --long`). Boot-dnlib has a different version (built from older commit). Version mismatch is NOT a problem — each stage is self-consistent.
- **EXE tests need `.runtimeconfig.json`.** Tests with `BEGIN-OUTPUT` blocks are compiled as `.exe`. Without `<name>.runtimeconfig.json`, execution fails. Cake's `Test` task generates these.
- **Test runner needs `-r dotnet` for EXE tests.** The test runner spawns compiled `.exe` files which need the `dotnet` host.
- **Shared `obj/` directory.** `.nproj` files share `IntermediateOutputPath=obj/`. Build individual projects with `--no-dependencies` after restoring all.
- **`ncc-core.exe` runs directly** (no `dotnet` wrapper). MSBuildTask searches `ncc-core.exe` → `ncc-core.dll` → `ncc.exe` in that order. On `.exe` match, runs directly; on `.dll` match, uses `dotnet` host.

## Coding style

- Nemerle source files in `ncc/` — functional/declarative style, pattern matching over if-else.
- MSBuild targets in `tools/msbuild-task/` and `boot-dnlib/` — these are the canonical source; `boot-dnlib/` copies are for bootstrapping.
- Cake script (`build.cake`) — keep tasks small, one concern per task. Use `DotNet*` aliases (not `DotNetCore*`).

## Commit convention

- **Subject:** `type(scope): short description`, ≤ 72 chars, imperative mood, no trailing period.
    - Types: `feat`, `fix`, `refactor`, `test`, `style`, `docs`, `chore`, `build`, `revert`.
    - Scopes: `build`, `compiler`, `test`, `msbuild`, `cake`.
- **Body:** Explain the **why** and tricky bits. End with totals when tests were run.
- Russian is fine in commit bodies and decision-log; English in code strings.
