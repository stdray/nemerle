# Nemerle: План сборки и тестирования retarget-compiler

## Цель ветки

Переход с `System.Reflection.Emit` (.NET Framework) на **dnlib** (.NET Core):
- `boot-dnlib/` — предсобранный бутстрап-компилятор под .NET Core 2.1
- Исходники (`ncc/frontend` + `ncc/backend`) — netstandard2.0, собираются новым компилятором
- Никакого .NET Framework — прощаемся с ним

## Системное окружение

- .NET SDK: 8.0.420, 10.0.107, 10.0.203
- .NET Runtime 2.1.30 (для бутстрап-компилятора)
- MSBuild: из SDK 10
- ОС: Windows

## Фаза A — Bootstrap (закрыта)

- [x] **A.0 Runtime для boot компилятора.** Установить .NET Core 2.1.30, создать `ncc.runtimeconfig.json` с `rollForward: LatestPatch` (внутри framework, не снаружи!).
- [x] **A.1 Shim для SecurityAttribute.** `System.Security.Permissions.SecurityAttribute` отсутствует в .NET Core 2.1 shared framework (он в `System.Runtime.Extensions.dll`). Создан shim DLL (`boot-dnlib/System.Security.Permissions.dll`), авто-собираемый Cake-задачей `FixBoot`.
- [x] **A.2 Проверка boot компилятора.** `dotnet boot-dnlib/ncc.exe test_hello.n -r boot-dnlib/System.Security.Permissions.dll -o test_hello.exe` — компилирует и запускает.

## Фаза B — Сборочная инфраструктура (закрыта)

- [x] **B.1 Ретаргетинг MSBuild-задачи.** `Nemerle.MSBuild.Tasks.csproj`: .NET Framework 4.5 → netstandard2.0. GAC-ссылки (`Microsoft.Build.*`) заменены на NuGet-пакеты. `Assembly.CodeBase` → `Assembly.Location`. `Registry.LocalMachine.OpenSubKey` → поиск через PATH.
- [x] **B.2 SDK-файлы в boot-dnlib.** `Nemerle.Sdk.props`, `Nemerle.Sdk.targets`, `Nemerle.MSBuild.targets` скопированы в `boot-dnlib/`. `<UsingTask>` путь исправлен на `$(MSBuildThisFileDirectory)`.
- [x] **B.3 Cake build script.** `build.cake`: Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → Test.

## Фаза C — Stage 1 сборка компилятора (закрыта)

- [x] **C.1 Nemerle.dll.** Сборка через `dotnet build Nemerle.nproj /p:Nemerle=boot-dnlib` — 0 errors.
- [x] **C.2 Nemerle.Compiler.dll.** Сборка через MSBuild с boot компилятором — 0 errors. Зависит от Nemerle.dll, NuGet-пакетов dnlib + System.CodeDom.
- [x] **C.3 Nemerle.Macros.dll.** — 0 errors.
- [x] **C.4 ncc-core.exe.** Точка входа. Собирается напрямую через `dotnet boot-dnlib/ncc.exe` (bypass MSBuild из-за netcoreapp2.1 TF). Требует runtime references: `System.Console.dll`, `System.Runtime.Extensions.dll`, `System.Threading.Thread.dll`, `System.IO.FileSystem.dll`. Копируется `dnlib.dll` и `System.Security.Permissions.dll` в `bin/Release/`.

## Фаза D — Тестовая инфраструктура (закрыта)

- [x] **D.1 Nemerle.Test.Framework.dll.** — netstandard2.0, собирается.
- [x] **D.2 Nemerle.Compiler.Test.dll (tests.exe).** Ретаргетинг net461 → netcoreapp2.1. Убраны GAC-ссылки `mscorlib` + `System`. Пересобран с Stage 1 компилятором для устранения version mismatch (1.2.0.795 → 1.2.0.811).
- [x] **D.3 HostedNcc (in-process).** Работает. В 4 раза быстрее ExternalNcc (0.25s/тест vs 1.0s). Требует `-r dotnet` для EXE-тестов.
- [x] **D.4 Runtimeconfig для EXE-тестов.** Cake-задача `Test` генерирует `<name>.runtimeconfig.json` для всех тестов с `BEGIN-OUTPUT`.
- [x] **D.5 Framework references.** Передаются через `-ref` флаги в `tests.exe`. Список: `System.Security.Permissions.dll` (shim), `System.Console.dll`, `System.Runtime.Extensions.dll`, `System.IO.FileSystem.dll`, `System.Threading.Thread.dll`, `System.Linq.dll`, `System.Text.RegularExpressions.dll`, `System.Collections.dll`.

## Фаза E — Оценка состояния (закрыта, 67% pass)

- [x] **E.1 Positive тесты.** 67/100 (67%). Остальные падают с managed exception (0xE0434352) — специфика .NET Core vs .NET Framework (missing APIs, разные типы в разных сборках).
- [x] **E.2 Negative тесты.** Не запущены (timeout на полном прогоне). Тест-раннер готов, нужно дожать.

## Фаза F — Stage 2 валидация (не начата)

- [ ] **F.1 Rebuild компилятора самим собой.** `dotnet build *.nproj /p:Nemerle=bin/Release` — собрать компилятор Stage 1 компилятором.
- [ ] **F.2 IL-валидация Stage1 vs Stage2.** Побайтовое сравнение DLL между стадиями.
- [ ] **F.3 Полный прогон тестов.** Positive + negative через Stage 2 компилятор, сравнение результатов со Stage 1.

## Фаза G — Долг (открытые вопросы)

- [ ] **G.1 Framework references в MSBuild.** Сейчас .nproj сборки полагаются на MSBuild `ResolveFrameworkReferences`. Для консистентности с C#/F# — проверить что все framework-сборки доходят до ncc через `@(ReferencePath)`.
- [ ] **G.2 ncc-core с apphost.** Текущий `ncc-core.exe` не имеет native apphost (собирается напрямую через ncc). Нужен `dotnet publish` или отдельный шаг apphost-embedding.
- [ ] **G.3 Shared obj/ directory.** `.nproj` файлы шарят `IntermediateOutputPath=obj/`, что вызывает конфликты `project.assets.json` при разных TF. Нужно разнести по проектным директориям или использовать solution-level restore.
- [ ] **G.4 Переход на .NET 5+.** Блокирован: компилятор делает `lookup("System.Security.Permissions.SecurityAttribute")` в `InitSystemTypes`, тип удалён из BCL в .NET 5+. Требует ребилда компилятора новым компилятором.
- [ ] **G.5 File lock MSBuild.Tasks.dll.** `CopyFile` в Cake BuildTasks падает с file-in-use на Windows. Добавлен try-catch как workaround.

## Test pass rate

| Suite | Passed | Total | % |
|-------|--------|-------|---|
| Positive (100) | 67 | 100 | 67% |
| Negative | — | 166 | не запущен |

## Инварианты

- **Boot compiler = .NET Core 2.1 ONLY.** `rollForward: LatestPatch`, не `LatestMajor`.
- **Shim DLL обязателен для boot compiler.** `System.Security.Permissions.dll` в `boot-dnlib/`.
- **Stage 1 compiler требует shim при компиляции.** Передаётся через MSBuild implicit ref или `-r` флаг.
- **HostedNcc = пересобранный test runner.** Version mismatch 1.2.0.795 vs 1.2.0.811 ломает in-process компиляцию.
- **EXE тесты = runtimeconfig.json + `-r dotnet`.** Без любого из двух — runtime crash.
