# Полезные находки (nemerle/opencode)

## Nemerle-синтаксис

### `when` не делает return
В Nemerle `when (cond) expr` — условное вычисление, не возврат из функции.
Внутри `foreach`:
```n
// НЕВЕРНО — Some(...) вычисляется и отбрасывается, цикл продолжается
foreach (kv in dict)
  when (match) Some(kv.Value);
None()  // всегда сюда

// ВЕРНО — mutable с присваиванием
mutable found = None();
foreach (kv in dict)
  when (match) found = Some(kv.Value);
found  // возвращает правильное
```

### `if` vs `when`
- `if (cond) { ... } else { ... }` — работает в Nemerle (в отличие от ранних версий)
- `when (cond) expr` — эквивалент `if` без `else`
- `when (cond) expr else expr` — `if/else` через `when`

### `==` для строк — НЕ reference equality
В Nemerle `==` для строк работает как в C# — value equality (вопреки слухам).
Доказано: `"D:/PRJ/..." == "D:/PRJ/..."` → True на идентичных строках.

**НО:** `Uri.LocalPath` для `file:///d%3A/...` даёт `/D:/...`, а для `file:///d:/...` даёт `D:\...`.
Разные форматы! Reference equality тут НИ ПРИ ЧЁМ — строки реально разные.
Фикс: `UriToKey()` — обрезать ведущий `/`, заменить `\` на `/`, ToUpper.

### `catch` с pattern matching
```n
// Простой catch-all
catch { | e => ... }

// С фильтром по типу
catch {
  | e when e is InvalidOperationException => ...
}
```
`| e is Type =>` в catch — **не работает**. Надо `| e when e is Type =>`.

### Строковая интерполяция: `$(...)` а не `{...}`
```n
// ВЕРНО
$"Hello $(name)!"
// НЕВЕРНО — будет буквально "{name}"
$"Hello {name}!"
```

### `Hashtable` в Nemerle — не Dictionary
`Nemerle.Collections.Hashtable[int, T]` — свой тип, не `System.Collections.Generic.Dictionary`.
`foreach (kv in ht)` — `kv` это `KeyValuePair<int, T>`, работает как в C#.

## Nemerle→C# interop

### `List[string]` из Nemerle — плохая совместимость с C#
Nemerle `List[string]` вызывает `MissingMethodException` при вызове из C#.
Заменить на `array[string]` → C# видит как `string[]`.

### Статические методы из Nemerle доступны через reflection
`GetMethod("MethodName", BindingFlags.Static|NonPublic)` работает.

## Компилятор

### Assembly версии
- NuGet Nemerle.Compiler.dll: **v=0.0.0.0**, PT=5291d186334f6101
- Stage1 Nemerle.Compiler.dll: **v=0.1.0.0**, PT=5291d186334f6101
- NuGet Nemerle.Macros.dll: **v=0.0.0.0**, PT=5291d186334f6101
- Stage1 Nemerle.Macros.dll: **v=0.0.0.0**, PT=5291d186334f6101

!ВАЖНО: Stage1 Compiler имеет ВЕРСИЮ 0.1.0.0, а Stage1 Macros — 0.0.0.0.
`LoadNemerleMacros` берёт версию из `typeof(ManagerClass).Assembly` → 0.1.0.0,
но Macros имеет 0.0.0.0 → FileNotFoundException.
Фикс: использовать версию 0.0.0.0 жёстко (ManagerClass.n:616).

### PublicKeyToken
- NuGet+Stage1 Nemerle.Compiler: 5291d186334f6101 (hex)
- НЕ 821452091345179971 (это decimal байтов)
- `AssemblyName.GetPublicKeyToken()` возвращает `byte[]`,
  конвертировать: `-join ($bytes | % { $_.ToString('x2') })`

### `InternalTypeClass.InitSystemTypes` — список нужных типов
Вызывает `Manager.Lookup(typeName)` для ~45 типов.
Требует `System.Runtime.dll`, `System.Collections.dll`,
`System.Diagnostics.Debug.dll`, `System.Runtime.InteropServices.dll`,
`System.Runtime.Serialization.Primitives.dll`, `System.Reflection.dll`.

### `DoNotLoadStdlib` — default `false`
Компилятор САМ загружает `typeof(object).Assembly` + `typeof(Uri).Assembly` +
`typeof(XmlNode).Assembly` + `typeof(NList).Assembly` (Nemerle.dll).
Не нужно передавать System.*.dll в `options.References`.

### Quasiquotation генерит код с `GlobalEnv`/`SyntaxElement`
`<[...]>` требует `using Nemerle.Compiler;` + `using Nemerle.Compiler.Parsetree;`
в исходном файле. Без них — "unbound name".

### `Message.FatalError` — immediate Recovery
`Message.FatalError` немедленно кидает Recovery. Не использовать для "мягких" ошибок.

## VSCode / LSP

### URI-форматы
- VSCode: `file:///d%3A/prj/...` (percent-encoded `:`)
- .NET `new Uri(filePath)`: `file:///d:/prj/...` (lowercase, unescaped)
- .NET `Uri.LocalPath`:
  - для `file:///d%3A/...` → `/d:/prj/...` (Unix-style, со слешем)
  - для `file:///d:/...` → `d:\prj\...` (Windows-style, без слеша)
  - для `file:///D:/...` и `file:///d:/...` → разный регистр!

**Фикс:** `UriToKey(uri)` — `LocalPath`, отрезать ведущий `/`, `Replace('\\', '/')`, `ToUpperInvariant()`.

### `Console.SetError` — работает но `Debug.Listeners` — нет
`Debug.Listeners.Add(new TextWriterTraceListener(...))` — не компилируется в .NET 8+.
`Console.SetError(writer)` — работает, перехватывает `Console.Error.WriteLine`.

### FileWriter для Nemerle-дебага
`System.IO.Path.Combine(System.IO.Path.GetTempPath(), "file.txt")` — работает.
`System.IO.File.AppendAllText(path, text)` — работает.
!В Nemerle-строках `\n` = `$(System.Environment.NewLine)`, а не `` `n ``.

### EngineBridge
- `AddOrUpdateDocument` ждёт 5s на каждый файл (`WaitOne(5000)`)
- `RebuildProject` — был async, теперь ждёт `WaitOne(60000)`
- `GetDiagnostics` / `GetHoverText` / `GetDefinitions` → C# через `array[string]`
- `QuickTipInfo` возвращает null пока метод не type-check'нут

### MSBuild в LSP
- `new Project(nprojPath)` падает если нет SDK (`Microsoft.Build.Exceptions.InvalidProjectFileException`)
- Fallback: парсить XML через `XDocument`, читать `<Compile>` и `<Reference>` элементы
- `<Compile Include="*.n">` → `Directory.GetFiles(dir, "*.n")`

