# AGENTS.md

Guidance for coding agents working in this repository.

## Status: retarget-compiler branch — boot compiler works, Stage 1 builds pass, tests at 67%

The `retarget-compiler` branch replaces `System.Reflection.Emit` (.NET Framework) with **dnlib** (.NET Core).
The boot compiler (`boot-dnlib/`) runs under .NET Core 2.1. Stage 1 compiles the compiler from source.
Test runner (`Nemerle.Compiler.Test`) runs positive/negative test suites via `HostedNcc` (in-process) or `ExternalNcc` (spawned).

## Build entry points

- **Local:** `dotnet-cake build.cake --target=Stage1` — full bootstrap + compiler build.
- **Cake tasks:** Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → Test.
- **Test:** `dotnet-cake build.cake --target=Test` — depends on Stage1, runs `tests.exe` against `testsuite/positive/` + `testsuite/negative/`.
- **Quick test (single file):** `dotnet snippets/Nemerle.Test/Nemerle.Compiler.Test/bin/Release/Nemerle.Compiler.Test.dll testsuite/positive/Issue-0352.n -r dotnet -p "-nowarn:10003"`
- **Dev rebuild loop:** edit `.n` source → `dotnet build Nemerle.Compiler.nproj -c Release /p:Nemerle=boot-dnlib` → copy DLLs to test runner dir.

## Prerequisites

- .NET Core 2.1 runtime (for boot compiler; installed via `dotnet-install.ps1 -Runtime dotnet -Version 2.1.30`)
- .NET SDK 8.0+ (for MSBuild tasks; `Nemerle.MSBuild.Tasks.dll` targets `netstandard2.0`)
- `dotnet-cake` tool (`dotnet tool install -g Cake.Tool`)

## Documents — what goes where

- **`doc/plan.md`** — what we've built (Stage 1, boot compiler, MSBuild tasks, test infra), what remains.
- **`doc/decision-log.md`** — every architectural decision with date / decision / reason / what was rolled back. **Newest entries on top.**
- **`AGENTS.md`** — this file; guidance for agents, invariants, build entry points.

## Hard invariants (easy to violate — read before coding)

- **Boot compiler pins .NET Core 2.1.** `boot-dnlib/ncc.exe` loads types via `-use-loaded-corlib` (typeof().Assembly). It crashes under .NET 8+ because corelib types diverged. Always use `ncc.runtimeconfig.json` with `"version": "2.1.0"` and NO rollForward to later majors.
- **`System.Security.Permissions.SecurityAttribute` is NOT in .NET Core 2.1 shared framework.** It lives in `System.Runtime.Extensions.dll`. The boot compiler needs the **shim DLL** (`boot-dnlib/System.Security.Permissions.dll`) as a `-r` reference. The shim is built by Cake's `FixBoot` task.
- **Stage 1 compiler uses Stage 1 libraries.** After Stage 1 build, the compiled `Nemerle.Compiler.dll` has version 1.2.0.811 (not 1.2.0.795 from boot-dnlib). The test runner must be rebuilt against the Stage 1 compiler to use `HostedNcc` (in-process). Without this, `HostedNcc` fails with version mismatch.
- **EXE tests need `.runtimeconfig.json`.** Tests with `BEGIN-OUTPUT` blocks are compiled as `.exe` and executed by the test runner. Without `<name>.runtimeconfig.json` next to the `.exe`, execution fails with `-2147450749` (0xE0434352, CLR exception). Cake's `Test` task generates these.
- **Test runner needs `-r dotnet` for EXE tests.** The test runner spawns compiled `.exe` files. On .NET Core 2.1, managed `.exe` files need the `dotnet` host to execute. Pass `-r dotnet` to `tests.exe`.
- **Shared `obj/` directory.** `.nproj` files share `IntermediateOutputPath=obj/`. Restoring different projects writes to the same `project.assets.json`, causing framework-target conflicts. Build individual projects with `--no-dependencies` after restoring all.
- **`ncc-core.exe` has no apphost.** It's compiled via `dotnet boot-dnlib/ncc.exe -t exe -o ncc-core.exe` — produces managed IL without native apphost. Must be run via `dotnet ncc-core.exe`, not directly.

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
