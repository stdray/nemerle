# YobaLog: сценарий агента (opencode) для отладки компилятора

## Что нужно агенту — резюме

| Endpoint | Метод | Параметры | Назначение |
|----------|-------|-----------|------------|
| `/api/v1/ingest/clef` | POST | `?workspace=` + `?description=` | Отправка логов, workspace создаётся лениво при первом запросе |
| `/api/v1/query` | GET/POST | `workspace` + `kql` + `cursor` | Чтение KQL → JSON rows (все KQL-операторы, включая project/extend/summarize) |
| `/api/v1/share` | POST | `workspace` + `kql` + `?ttlHours=` | Создание share-ссылки с редактируемым KQL в браузере |

## Ключ

Агент получает wildcard-ключ с `CanCreate=true`:

```
X-Seq-ApiKey: dGhpcyBpcyBhIHdpbGRjYXJk...
```

Один ключ — все workspace. Агент НЕ управляет retention, НЕ создаёт ключи, НЕ продлевает окна. Всё это — зона ответственности пользователя и сервера.

## Ingest: создание workspace и отправка логов

```http
POST https://yobalog.3po.su/api/v1/ingest/clef?workspace=nemerle-macrophase&description=Debug+MacroPhase.BeforeInheritance
X-Seq-ApiKey: dGhpcyBpcyBhIHdpbGRjYXJk...
Content-Type: text/plain

{"@t":"2026-05-06T10:00:00.000Z","@l":"Debug","@mt":"LookupSymbol: name={Name}, found={Count}","Name":"MacroPhase","Count":0}
```

**Механика:**
- `?workspace=` — обязателен для wildcard-ключа
- `?description=` — обязателен при создании нового workspace
- Workspace не существует → создаётся лениво
- Workspace существует → `description=` игнорируется
- Имя workspace: slug `[a-z0-9][a-z0-9-]{1,39}`

## Query API: чтение через KQL

### GET

```http
GET https://yobalog.3po.su/api/v1/query?workspace=nemerle-macrophase&kql=events+|+where+Level+>=+3+|+order+by+Timestamp+asc+|+take+50
X-Seq-ApiKey: dGhpcyBpcyBhIHdpbGRjYXJk...
```

### POST (для длинных KQL)

```http
POST https://yobalog.3po.su/api/v1/query
X-Seq-ApiKey: dGhpcyBpcyBhIHdpbGRjYXJk...
Content-Type: application/json

{
  "workspace": "nemerle-macrophase",
  "kql": "events\n| where Properties.SourceContext == \"ConstantFolder\"\n| project Timestamp, Properties, Message\n| order by Timestamp asc",
  "cursor": null
}
```

### Ответ

```json
{
  "columns": ["Timestamp","Level","LevelName","Message","MessageTemplate","Exception","TraceId","SpanId","EventId","Properties"],
  "rows": [
    ["2026-05-06T10:00:01.000Z","Debug","Debug","FoldConstants returned: [ ]","FoldConstants returned: {Result}","","","",null,{"SourceContext":"ConstantFolder","Result":"[ ]"}],
    ["2026-05-06T10:00:00.000Z","Debug","Debug","LookupSymbol: name=MacroPhase, found=0","LookupSymbol: name={Name}, found={Count}","","","",null,{"SourceContext":"GlobalEnv","Name":"MacroPhase","Count":0}]
  ],
  "cursor": "AAECAwQFBgcICQoLDA0ODw==",
  "truncated": false
}
```

**Требования к ответу:**
- `Properties` — **обязательно** в columns. JSON-объект, содержит все кастомные свойства события
- Допустимы **все реализованные операторы KQL**: `project`, `extend`, `summarize` в том числе. Если KQL меняет форму — columns отражают результат
- `cursor` — непрозрачная base64-строка, монотонно возрастающая последовательность. Передавать `cursor` из предыдущего ответа для пагинации. `null` если страниц больше нет

## Share-ссылка: живой KQL для человека

```http
POST https://yobalog.3po.su/api/v1/share
X-Seq-ApiKey: dGhpcyBpcyBhIHdpbGRjYXJk...
Content-Type: application/json

{
  "workspace": "nemerle-macrophase",
  "kql": "events\n| where Properties.SourceContext == \"GlobalEnv\"\n| where Message contains \"LookupSymbol\"\n| order by Timestamp asc"
}
```

### Ответ

```json
{
  "url": "https://yobalog.3po.su/share/kql/abc123",
  "expiresAt": "2026-05-07T10:00:00Z"
}
```

### Поведение ссылки

| Клиент | Accept | Результат |
|--------|--------|-----------|
| Браузер | `text/html` | HTML-страница с **редактируемым** KQL textarea + таблица событий + infinite scroll |
| curl / агент | `*/*` | TSV-файл |

**Критично:** KQL textarea в браузере **редактируемая**. Человек, открывший share-ссылку, может менять where/take/order/project — полноценно исследовать.

## Self-instrumentation для Nemerle (`.n`)

Нужен минимальный C# helper, вызываемый из `.n` через Interop:

```csharp
public static class YobaLog
{
    private static readonly HttpClient _http = new() { BaseAddress = new Uri("https://yobalog.3po.su") };

    public static void Configure(string apiKey, string workspace) { ... }

    public static void Debug(string messageTemplate, params (string key, object value)[] props)
    {
        // POST /api/v1/ingest/clef?workspace={workspace}
        // X-Seq-ApiKey: {apiKey}
        // CLEF NDJSON: {"@t":"...","@l":"Debug","@mt":messageTemplate, ...props}
    }
    // Information, Warning, Error — аналогично
}
```

Использование из `.n` компилятора:

```n
// ConstantFolder.n
YobaLog.Debug("literal_field_value: qid={Qid}, lookup={Result}",
    ("Qid", qid), ("Result", lookupResult));
```

## Сценарий: отладка MacroPhase.BeforeInheritance

### Шаг 1: Инструментирование

Вставляю `YobaLog.Debug` в:
- `MacroClassGen.n:73` — что пришло в `ph`, что вернул `FoldConstants`
- `ConstantFolder.n:386` — что вернул `QidOfExpr`, что вернул `LookupSymbol`
- `GlobalEnv.n:378` — как разбился `id` на `(type_part, the_name)`, какие типы нашлись

### Шаг 2: Запуск и сбор

Сервер стартует, логи идут в `?workspace=nemerle-macrophase`. Сервер НЕ управляет созданием workspace — просто пишет.

### Шаг 3: Анализ

```kql
-- Вижу, что LookupSymbol возвращает пустой список
events
| where Properties.SourceContext == "ConstantFolder"
| order by Timestamp asc
| project Timestamp, Message, Result=Properties.Result

-- Родительский вызов — что пришло в LookupSymbol?
events
| where Properties.SourceContext == "GlobalEnv"
| project Timestamp, TypePart=Properties.TypePart, Name=Properties.TheName, Found=Properties.Count

-- Сравниваю с успешным случаем (Nemerle.Macros.dll)
events
| where Properties.SymbolId contains "MacroPhase"
| summarize Count=count() by Result=Properties.Result
```

### Шаг 4: Share

Нашёл, что `LookupTypes(["MacroPhase"])` возвращает пустой список без namespace `Nemerle`:

```
POST /api/v1/share
{ "workspace": "nemerle-macrophase",
  "kql": "events\n| where Properties.TypePart contains \"MacroPhase\"\n| order by Timestamp asc\n| project Timestamp, Message, TypePart=Properties.TypePart, Found=Properties.Count" }
```

Коллега открывает → видит KQL textarea → меняет фильтры → понимает что NamespaceTree не содержит `Nemerle.MacroPhase` → чинит.

### Шаг 5: Decision log

Записываю находку в decision-log.md со ссылкой на share:

> 2026-05-06 — MacroPhase.BeforeInheritance: `LookupTypes(["MacroPhase"])` returns empty in NamespaceTree when project uses PackageReference (different Nemerle.dll file from compiler). Root cause: two loaded Nemerle.dll files confuse name resolution. See share: https://yobalog.3po.su/share/kql/abc123

## Что НЕ нужно агенту

- **Retention management.** Сервер сам управляет retention. Логов мало, пространство считаем безграничным для агента.
- **Создание / удаление ключей.** Пользователь даёт ключ, агент использует.
- **Продление CreateWindow.** Окно создания — зона пользователя. Не укладываешься — бери новый ключ.
- **Парсинг cursor.** Непрозрачный токен, передавать как есть.
- **Rate limiting / retry.** При отладке потеря пары логов некритична. Fire-and-forget.
