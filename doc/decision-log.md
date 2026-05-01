# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

## 2026-05-02 — Framework references: revert ManagerClass.n scan, pass via -r flags

**Решение:** откатить сканирование shared framework директории в `ManagerClass.n` (коммит `095a07b`). Передавать framework-сборки через `-r`/`-ref` флаги — как C#/F# MSBuild делает через `ResolveFrameworkReferences`.

**Причина:** модификация исходников компилятора — хак, который ломает bootstrap (скомпилированный компилятор не может пересобрать сам себя без shim). Правильный путь: MSBuild резолвит framework references и передаёт компилятору. Для тест-раннера — явный список `-ref` флагов.

**Что откатили:** framework-scan из ManagerClass.n (7 строк). Оставлен оригинальный `-use-loaded-corlib` (4 `typeof()` вызова + Nemerle).

**Трейдофф:** тест-раннер требует список `-ref` флагов (~8 сборок). Неудобно, но соответствует тому, как работает C#/F# SDK.

**Cross-refs:** `doc/plan.md` Phase D.5.

---

## 2026-05-02 — System.Security.Permissions shim DLL вместо правки ManagerClass.n

**Решение:** создать shim DLL `System.Security.Permissions.dll` (netstandard2.0, один класс `SecurityAttribute` + enum `SecurityAction`). Разместить в `boot-dnlib/`, авто-собирать Cake-задачей `FixBoot` если отсутствует.

**Причина:** `System.Security.Permissions.SecurityAttribute` отсутствует в .NET Core 2.1 shared framework (тип в `System.Runtime.Extensions.dll`, но она не грузится `-use-loaded-corlib`). Компилятор делает `lookup("System.Security.Permissions.SecurityAttribute")` в `InitSystemTypes` → ICE без этого типа.

**Альтернативы рассмотренные:**
- **Правка ManagerClass.n** (добавить `load(typeof(System.Environment))`) — работает, но меняет исходники компилятора. Отвергнуто в пользу "передавать через -r".
- **Правка InternalTypeClass.n** (убрать `lookup("SecurityAttribute")`) — требует знания семантики использования типа, risky change.
- **NuGet System.Security.Permissions 4.5.0** — тянет зависимости, не грузится dnlib-resolver'ом boot компилятора.

**Что откатили:** ничего — shim стабилен с первого коммита.

---

## 2026-05-02 — Retarget Nemerle.MSBuild.Tasks.csproj: netstandard2.0 вместо net8.0

**Решение:** целевая платформа MSBuild-задачи — `netstandard2.0` (не `net8.0`, не `netcoreapp2.1`).

**Причина:** netstandard2.0 грузится любым MSBuild:
- SDK 2.1 (MSBuild на .NET Core 2.1) — грузит
- SDK 10 (MSBuild на .NET 10) — грузит
- Никаких конфликтов версий

**Что откатили:** первоначальный выбор `net8.0` (не грузится в MSBuild 2.1) и идея `netcoreapp2.1` (не грузится в MSBuild 10).

**Cross-refs:** `doc/plan.md` Phase B.1.

---

## 2026-05-02 — Cake build.cake вместо ручной сборки

**Решение:** автоматизировать сборку через `build.cake` (Cake 6.0), задачи: Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → Test.

**Причина:** ручная сборка `.nproj` файлов с `dotnet build /p:Nemerle=...` требует знания порядка зависимостей и workaround'ов (shared obj, ncc-core прямой вызов). Cake скрывает сложность за одной командой.

**Дизайн-выборы:**
- **Stage1 (MSBuild .nproj):** DotNetRestore + DotNetBuild с `/p:Nemerle=boot-dnlib`. Проекты строятся последовательно с `--no-dependencies` из-за shared `obj/`.
- **ncc-core (прямой вызов):** bypass MSBuild из-за `netcoreapp2.1` TF (не поддерживается SDK 10). Вызов `dotnet boot-dnlib/ncc.exe` с явными `-r` флагами.
- **Test (tests.exe):** запуск `Nemerle.Compiler.Test.dll` через `dotnet`, передача `-r dotnet` (для EXE-тестов) и списка `-ref` флагов. Предгенерация `runtimeconfig.json` для EXE-тестов.

---

## 2026-05-02 — HostedNcc (in-process) вместо ExternalNcc для тестов

**Решение:** тест-раннер (tests.exe) пересобирается с Stage 1 компилятором и использует `HostedNcc` (in-process) вместо `ExternalNcc` (spawn процесса).

**Причина:** HostedNcc в ~4 раза быстрее (0.25s/тест vs 1.0s). ExternalNcc spawn'ит `dotnet ncc-core.exe` на каждый тест — 1+ секунда на процесс только на startup.

**Условия работы:**
- Тест-раннер должен быть собран с той же версией Nemerle.Compiler.dll, которая используется в HostedNcc (1.2.0.811)
- Без этого — version mismatch и runtime crash
- EXE-тестам по-прежнему нужен `-r dotnet` (HostedNcc не эмбедит apphost в скомпилированный `.exe`)

**Что откатили:** ExternalNcc как default для тестов (остался как fallback через `-ncc` флаг).

---

## 2026-05-02 — .NET Core 2.1 runtime как целевая платформа

**Решение:** boot компилятор и Stage 1 компилятор работают под .NET Core 2.1 (`Microsoft.NETCore.App`, version 2.1.0, rollForward: LatestPatch).

**Причина:** компилятор использует `-use-loaded-corlib` — загружает типы из рантайма через `typeof().Assembly`. Под .NET 8+ corelib-типы разошлись, компилятор падает с ICE в `InitSystemTypes`. Переход на .NET 5+ требует ребилда компилятора новым компилятором (курица-яйцо).

**Конфигурация:**
- `ncc.runtimeconfig.json`: `"version": "2.1.0"`, `"rollForward": "LatestPatch"` **внутри** `"framework"` (не снаружи!)
- Тест-раннер: такой же конфиг
- ncc-core.exe: без apphost (managed IL), запускается через `dotnet ncc-core.exe`

---

## 2026-05-02 — .gitignore: generated runtimeconfig + test outputs

**Решение:** все `testsuite/positive/*.runtimeconfig.json`, `testsuite/negative/*.runtimeconfig.json`, скомпилированные `.exe`/`.dll` в testsuite — в `.gitignore`.

**Причина:** генерируются Cake-задачей `Test` (и раннером `tests.exe`). Не должны коммититься.

---

## 2026-05-01 — Initial bootstrap: retarget MSBuild tasks, fix boot compiler

**Решение:** начать с ретаргетинга сборочной инфраструктуры, не трогая исходники компилятора:
1. `Nemerle.MSBuild.Tasks.csproj` — net4.5 → netstandard2.0
2. `ncc.runtimeconfig.json` — зафиксировать .NET Core 2.1
3. SDK-файлы в `boot-dnlib/` для поддержки `.nproj` сборок

**Причина:** без работающей сборки нельзя тестировать компилятор. Сборочная инфраструктура — минимальный self-contained шаг.

**Что откатили:** ничего — это первый коммит ветки.
