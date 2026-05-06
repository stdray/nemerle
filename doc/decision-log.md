# Decision Log

Лог архитектурных решений. Формат: дата — решение — причина — что откатили (если было). Новые записи сверху.

---

## 2026-05-06 — Все .md файлы коммитятся

**Решение:** убрать `AGENTS.md` и `doc/*.md` из `.gitignore`. Все markdown-файлы разработки теперь в репозитории.

**Причина:** потерян `doc/decision-log.md` при shredding `.gitignore` (коммит `342a79b81`). Восстановлен из `retarget-compiler`. Риск повторной потери документации неприемлем.

---

## 2026-05-06 — PackageId: stdray.Nemerle.Compiler

**Решение:** префикс `stdray.` для NuGet пакета.

**Причина:** `Nemerle.Compiler` уже занят на nuget.org (владелец `hardcase`, ver 1.2.547). Без префикса — 403 при push.

---

## 2026-05-06 — GitVersion.yml: ContinuousDelivery, label 'rc'

**Решение:** `GitVersion.yml` с `mode: ContinuousDelivery`, `next-version: 1.3.0`, `label: 'rc'`.

**Причина:** без конфига GitVersion даёт `0.0.1-reanemerle.1`. NuGet требует буквенный pre-release label — `rc` вместо пустого/числового.

---

## 2026-05-06 — YobaConf tooling без StartProcess

**Решение:** `global.json` + `.config/dotnet-tools.json` + `dotnet tool restore`. `StartProcess` запрещён в Cake кроме `dotnet-ildasm`.

**Причина:** YobaConf паттерн — предсказуемый tool restore, кросс-платформенный `dotnet tool run`.

---

## 2026-05-06 — VSCode: PackageReference вместо HintPath

**Решение:** заменить HintPath-ссылки (`$(NemerleBin)\Nemerle.dll` etc.) на `<PackageReference Include="stdray.Nemerle.Compiler">` с `ExcludeAssets="build;buildTransitive"`.

**Причина:** PackageReference даёт automatic restore через NuGet, не зависит от локального `boot/`. `ExcludeAssets` предотвращает импорт Nemerle MSBuild targets в C# проект.

---

## 2026-05-06 — ncc-core.dll вместо ncc-core.exe

**Решение:** `ncc-core.dll` (managed) — entry point компилятора, без .exe apphost.

**Причина:** кросс-платформенность. Linux не имеет `.exe` apphost. Запуск через `dotnet ncc-core.dll`.

---

## 2026-05-06 — MSBuildTask: .dll перед .exe, FindExecutable("dotnet")

**Решение:** `MSBuildTask.cs:104`: порядок поиска `ncc-core.dll` → `ncc-core.exe` → `ncc.exe`. `GenerateFullPathToTool`: `FindExecutable("dotnet")` вместо `dotnet.exe`.

**Причина:** кросс-платформенность. Linux: нет `.exe`, `dotnet` без расширения.

---

## 2026-05-02 — Framework references: revert ManagerClass.n scan, pass via -r flags

**Решение:** откатить сканирование shared framework директории в `ManagerClass.n` (коммит `095a07b`). Передавать framework-сборки через `-r`/`-ref` флаги — как C#/F# MSBuild делает через `ResolveFrameworkReferences`.

**Причина:** модификация исходников компилятора — хак, который ломает bootstrap (скомпилированный компилятор не может пересобрать сам себя без shim). Правильный путь: MSBuild резолвит framework references и передаёт компилятору.

**Что откатили:** framework-scan из ManagerClass.n.

---

## 2026-05-02 — System.Security.Permissions shim DLL

**Решение:** shim DLL `System.Security.Permissions.dll` (netstandard2.0, один класс). В `boot/`, авто-собирается Cake-задачей `FixBoot`.

**Причина:** `System.Security.Permissions.SecurityAttribute` отсутствует в .NET Core shared framework. Компилятор делает `lookup("System.Security.Permissions.SecurityAttribute")` → ICE без shim.

---

## 2026-05-02 — Retarget Nemerle.MSBuild.Tasks: netstandard2.0

**Решение:** `netstandard2.0` (не `net8.0`, не `netcoreapp2.1`).

**Причина:** грузится любым MSBuild (2.1–10), нет конфликтов версий.

---

## 2026-05-02 — Cake build.cake вместо ручной сборки

**Решение:** `build.cake` (Cake 6.0+). Задачи: Clean → FixBoot → BuildTasks → PrepareSdk → Stage1 → Stage2 → Stage3 → Validate → Test → Pack → NuGetPush.

**Причина:** ручная сборка `.nproj` с `dotnet build /p:Nemerle=...` требует знания порядка и workaround'ов. Cake скрывает сложность.

---

## 2026-05-02 — HostedNcc (in-process) для тестов

**Решение:** тест-раннер использует `HostedNcc` (in-process) вместо `ExternalNcc`.

**Причина:** в ~4 раза быстрее (0.25s/тест vs 1.0s). ExternalNcc spawn'ит `dotnet` на каждый тест.

---

## 2026-05-02 — .NET Core 2.1 как целевая платформа (исторически)

**Решение:** boot компилятор под .NET Core 2.1. **Отменено:** переход на .NET 8.0 bootstrap (см. `PLAN.md` ReflectTypeBuilder fix).

---

## 2026-05-01 — Initial bootstrap: retarget MSBuild tasks

**Решение:** начать с ретаргетинга сборочной инфраструктуры, не трогая исходники компилятора.

**Причина:** без работающей сборки нельзя тестировать компилятор.
