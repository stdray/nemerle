# План: ReflectTypeBuilder fix + .NET 8 миграция (v4)

## Контекст

Bootstrap pipeline Stage1-3 работает на .NET Core 2.1 → .NET 8.0 (через `ncc-core.runtimeconfig.json` с `rollForward: LatestMajor`).
При запуске компилятора на .NET 8/10 без правильного порядка инициализации — ICE InternalType.Void/Object != null.

**Статус:** Сейчас компилятор работает на .NET 8/10 через `ncc-core.dll` с `rollForward: LatestMajor`. CI зелёный.

## Теория (требует верификации логгированием)

ReflectTypeBuilder вызывает minimizeInterfaces() при построении КАЖДОГО типа.
minimizeInterfaces → TryRequire → Require → LowerBound → assertion на InternalType.Object.

На .NET Core 2.1 не проявляется (меньше типов в System.Runtime).
На .NET 8 больше типов → при сканировании сборки триггерится до InitSystemTypes.

## Итерация 1: Логгирование (ВЕРИФИКАЦИЯ)

Инструментировать:
- ncc/backend/hierarchy/Codec.n — перед minimizeInterfaces()
- ncc/frontend/typing/StaticTypeVar.n — в get_LowerBound
- ncc/backend/hierarchy/InternalTypeClass.n — вход/выход InitSystemTypes

Запустить на .NET 8 и 2.1, сравнить trace.

## Итерация 2: Фикс ReflectTypeBuilder

После подтверждения:
- Codec.n: minimizeInterfaces → возвращать allInterfaces без минимизации
- ExternalTypeInfo.n: сохранить raw interfaces, добавить FinalizeMinimization()
- InternalTypeClass.n: вызвать финализацию в конце InitSystemTypes

## Итерация 3: ncc-core-net8 БЕЗ C# wrapper

После фикса компилятор работает на .NET 8 нативно:
- dotnet ncc-core.dll с runtimeconfig 8.0 → без ICE
- C# wrapper НЕ НУЖЕН (компилятор сам работает на .NET 8)
- Stage1Net8: сгенерировать ncc-core-net8.nproj (TFM=net8.0) → SDK даст apphost
- Удалить ncc-core-cs/

## Итерация 4: Пайплайн

build.cake уже содержит правильную структуру.
Нужно: убрать -r dotnet из Test, runtimeconfig'и 2.1.0 для тестов.

## Итерация 5: ReplaceBootstrap

Stage3 → boot/, runtimeconfig 8.0. ✅ Done.

## Файлы

| Файл | Действие |
|------|----------|
| ncc/backend/hierarchy/Codec.n | Лог → фикс minimizeInterfaces |
| ncc/frontend/typing/StaticTypeVar.n | Лог LowerBound |
| ncc/backend/hierarchy/InternalTypeClass.n | Лог → вызов финализации |
| ncc/backend/hierarchy/Types/ExternalTypeInfo.n | raw interfaces |
| ncc/frontend/hierarchy/Types/TypeInfo.n | Уже починен |
| tools/msbuild-task/MSBuildTask.cs | Уже обновлён |
| build.cake | Мелкие правки |
| ncc-core-cs/ | УДАЛИТЬ |

## Верификация

1. Лог подтверждает minimizeInterfaces до InitSystemTypes
2. dotnet ncc-core.dll test.n с runtimeconfig 8.0 — без ICE
3. dotnet-cake --target=Stage1
4. dotnet-cake --target=BuildTests
5. dotnet-cake --target=Test > results.txt 2>&1
6. dotnet-cake --target=ReplaceBootstrap ✅
