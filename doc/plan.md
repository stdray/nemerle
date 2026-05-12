# План: reanemerle

## Статус

- `reanemerle` branch — CI зелёный (Stage1→Stage2→Stage3→Validate→Pack→NuGetPush)
- NuGet: `stdray.Nemerle.Compiler` 1.3.0-rc.10341 опубликован на nuget.org
- `.gitignore`: toptal-шаблон (csharp,dotnetcore,vs,visualstudio,visualstudiocode,rider) + Nemerle-специфика
- CI: `ubuntu-latest`, `.NET 10.0.200`, `dotnet tool restore` + `dotnet cake`, concurrency на `github.ref`
- GitVersion: `ContinuousDelivery`, `next-version: 1.3.0`, label `rc`, `GitVersion.yml` в корне
- VSCode: мигрирован с HintPath на `<PackageReference Include="stdray.Nemerle.Compiler">`
- VSCode: 17/17 LSP тестов проходят (13 + 4 recovery)
- Все `.md` файлы коммитятся (`.gitignore` больше не игнорирует `AGENTS.md` и `doc/*.md`)

## Сделано

- [x] Реструктуризация YobaConf: `boot-dnlib/` → `boot/`, проекты → `src/`, legacy → `src/legacy/`
- [x] SDK файлы → `sdk/`, `testsuite/` → `tests/`
- [x] `global.json`: SDK 10.0.200, `rollForward: latestFeature`
- [x] `.config/dotnet-tools.json`: cake.tool 6.1.0, gitversion.tool 6.4.0, dotnet-ildasm 0.12.2
- [x] CI: `ubuntu-latest`, `dotnet tool restore` → `dotnet cake`, без `install -g`
- [x] `Dockerfile` для локальной симуляции CI
- [x] Кросс-платформенные фиксы: `MSBuildTask.cs` (.dll перед .exe, `FindExecutable("dotnet")`)
- [x] `build.cake`: `ncc-core.exe` → `ncc-core.dll`, `BuildTasks` явный `OutputPath`
- [x] `Nemerle.Compiler.Package.csproj`: `ncc-core.dll`, PackageId → `stdray.Nemerle.Compiler`
- [x] `.gitignore`: полный toptal-шаблон + Nemerle-специфика (150 строк)
- [x] Удалены 381 закоммиченных билд-артефактов (`bin/`, `obj/`, `tools/.store/`, `tools/dotnet-gitversion`)
- [x] `build.cake`: убран дубликат `#tool GitVersion.Tool`
- [x] CI: `concurrency` на `${{ github.ref }}`, `cancel-in-progress: true`
- [x] Validate: IL сравнение Stage1 vs Stage2 (Nemerle.Macros.dll — known non-deterministic macro IDs)
- [x] NuGet: `stdray.Nemerle.Compiler` 1.3.0-rc.10341 опубликован
- [x] `GitVersion.yml`: ContinuousDelivery, next-version 1.3.0, label rc, main branch reanemerle
- [x] VSCode: миграция с HintPath на `<PackageReference Include="stdray.Nemerle.Compiler">` с `ExcludeAssets="build;buildTransitive"`
- [x] VSCode: тестовый проект тоже очищен от HintPath (Nemerle.Macros приходит транзитивно)
- [x] `build.cake BuildVscode`: упрощён — убраны `NemerleBin` и ручное копирование DLL
- [ ] ReflectTypeBuilder fix + minimizeInterfaces (см. `PLAN.md`) — верификация логгированием
- [x] CI badges в README (CI + NuGet)
- [x] `build` job на `reanemerle` проходит зелёный
- [x] VSCode: recovery тесты (4 теста — Recovery, hover, ProjectReference, NprojLoader)
- [x] VSCode: MSBuild-парсинг `.nproj` (`NprojLoader` использует `Microsoft.Build.Evaluation.Project`)
- [x] `AGENTS.md` обновлён: актуальная архитектура, YobaConf, NuGet, GitVersion

## Осталось

- [ ] CI: добавить badge в README (done) + проверить отображение
- [ ] Удалить старый пакет `0.0.1-reanemerle.1` с nuget.org (deprecated)
- [ ] VSCode: F5 / Ctrl+F5 привязать к compile/compileRun
- [ ] VSCode: SignatureHelp, References, SemanticTokens
- [ ] VSCode: Macro expansion via Virtual Documents
- [ ] Автоматический NuGet push при релизных тегах (v1.3.0 и т.д.)

## VSCode: Компиляторный IntelliSense (замена EngineBridge hover/definition)

Цель: заменить Engine.BeginGetQuickTipInfo/GetGotoInfo на прямой доступ
к TExpr-дереву компилятора из EngineHost. Устраняет null_tip и
зависимость от асинхронного Engine.

- [x] 1. EngineHost: кешировать ManagerClass/TExpr после Run()
- [x] 2. EngineHost: FindTExprAt (simplified via NameTree lookup)() — обход TExpr.Visit() по Location.Contains()
- [x] 3. EngineHost: MakeHoverMarkdown (inline)() — TExpr/IMember/LocalValue → Markdown
- [x] 4. EngineHost: GetHoverInfo(uri, line, col) — публичный метод
- [x] 5. EngineHost: GetDefinitionLocation(uri, line, col) — публичный метод
- [x] 6. ServerState: GetHoverAsync → _engine.GetHoverInfo() вместо EngineBridge
- [ ] 7. ServerState: GetDefinitionAsync → _engine.GetDefinitionLocation()
- [ ] 8. EngineBridge.n: удалить GetHoverText, GetDefinitions, Complete
- [ ] 9. Тест: ServerHarness.SendHoverRequestAsync + CompilationTests на VscodeTestApp
- [ ] 10. Проверить hover на string, Greet, tb — реальный тип + место определения
- [ ] 11. Проверить definition (F12) — переход к определению
