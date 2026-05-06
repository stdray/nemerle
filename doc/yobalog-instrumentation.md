# YobaLog: доработка под сценарии отладки компилятора

## Сценарии

**А. Инструментирование кода компилятора.** При отладке бага `MacroPhase.BeforeInheritance` мне нужно вставлять диагностические `Log.Debug(...)` в критические точки компилятора, создавать под это выделенный workspace, собирать trace и через KQL локализовать причину.

**Б. Несколько параллельных расследований.** Одновременно могу отлаживать баг компилятора, баг VSCode-сервера, баг тестов — каждому своё workspace со своим retention.

**В. Шеринг контекста.** Скинуть коллеге ссылку не на TSV-дамп, а на живой KQL-запрос с отфильтрованными событиями, чтобы он мог дальше исследовать.

---

## Чего не хватает

### 1. Workspace management через Ingest API key

**Сейчас:** чтобы создать workspace, нужен Admin API key. У меня есть только Ingest key (`wE7zqtHYoEqsC0AjiXD75A`), который передаётся в `X-Seq-ApiKey`.

**Нужно:** возможность атомарно создавать workspace при первом ingest-запросе. То есть: если workspace с указанным именем не существует — создать, если существует — писать в него. Параметр: имя workspace в CLEF-событии (доп. свойство `@ws`) или в HTTP-заголовке (`X-YobaLog-Workspace`).

```http
POST /api/v1/ingest/clef
X-Seq-ApiKey: <ingest-key>
X-YobaLog-Workspace: nemerle-macrophase  # ← создать если нет

{"@t":"...", "@l":"Debug", "@mt":"FoldConstants returned: {Result}", "Result": "PExpr.Ref(...)"}
```

**Альтернативно:** новый endpoint `PUT /api/v1/workspaces/{name}` доступный по Ingest key (не требующий Admin key), идемпотентный, возвращает `201`/`200`.

---

### 2. Свойства как first-class citizens в KQL

**Сейчас:** `Properties.SourceContext`, `Properties.CallChain` — через JSON extraction, без индекса, медленно на больших объёмах.

**Нужно:** для часто-используемых свойств — явный индекс (allowlist на workspace). При `DeclareIndexAsync("SourceContext")` — колонка в `Events` таблице, доступна как `SourceContext` без `Properties.` префикса.

```kql
| where SourceContext == "ConstantFolder" and SymbolId == "MacroPhase.BeforeInheritance"
| order by Timestamp asc
| project Timestamp, Level, SymbolResult, Message
```

---

### 3. Интерактивные share-ссылки с KQL

**Сейчас:** `/share/{ws}/{id}.tsv` — статический TSV-экспорт.

**Нужно:** новый тип share-ссылки с живым KQL:
- `POST /share` с телом `{ workspace, kql, ttl }` → возвращает URL `/share/kql/{id}`
- При открытии — рендерится KQL UI с предзаполненным запросом и результатами
- Доступ без аутентификации (как текущий share, но интерактивный)
- TTL-контроль: 1h / 6h / 24h / 7d

**Пример сценария:** я добавил 10 `Log.Debug()` в компилятор, собрал логи в workspace `nemerle-macrophase`, отфильтровал через KQL, делюсь ссылкой — коллега сразу видит контекст, может менять where/take/order в браузере.

---

### 4. Tracing (OTLP spans) в share и KQL

**Сейчас:** OTLP traces пишутся в `{workspace}.traces.db`, есть waterfall UI. Но share-ссылки только для событий. Нет KQL-доступа к span'ам из share.

**Нужно:**
- Share-ссылка с KQL-запросом к span'ам: `POST /share { workspace, kql, target: "spans", ttl }`
- В share-интерфейсе — переключение events/spans, переход из span к связанным событиям по TraceId

**Сценарий:** я обернул `EngineHost.GetDiagnostics` в OTel Span. Делюсь ссылкой с KQL `| where Name contains "GetDiagnostics" | order by Duration desc`. Коллега кликает на span → видит waterfall → drill-down к событиям с тем же TraceId.

---

### 5. Self-instrumentation SDK для .n (Nemerle)

**Сейчас:** C# проекты используют `Seq.Extensions.Logging` + `Serilog.Sinks.Seq`. Nemerle-код (`.n`) может вызывать `System.Console.WriteLine` — неструктурированно.

**Нужно:** минимальный Nemerle-хелпер для structured logging в YobaLog.

*Вариант A (простой):* статический класс на C# (в `Nemerle.LanguageServer` или отдельной lib), который принимает message template + props и шлёт HTTP CLEF напрямую:

```n
YobaLog.Debug("FoldConstants returned: {Result}", result.ToString());
YobaLog.Information("LookupSymbol found: {Count} members", members.Length);
```

*Вариант B (нативный):* Nemerle-макрос, который генерирует Serilog-вызовы или прямые CLEF HTTP POST.

Рекомендуется вариант A — минимальный статический класс на ~50 строк, используемый из `.n` через Interop.

---

### 6. Per-workspace TTL / retention-класс

**Сейчас:** retention настраивается через Admin UI.

**Нужно:** при создании workspace (см. п.1) указывать TTL или retention-класс:
- `volatile` — 1h, автоматическая очистка
- `debug` — 24h
- `normal` — 30d (дефолт)
- `permanent` — never

Без этого debug-воркспейсы будут жить 30 дней и забивать диск.

---

## Приоритеты

| # | Что | Критичность | Зачем |
|---|-----|-------------|-------|
| 1 | Workspace через ingest key | Критично | Без этого не могу создавать workspace под каждое расследование |
| 2 | Интерактивные share-ссылки с KQL | Критично | Без этого не могу передать контекст коллеге (TSV — мёртвый) |
| 3 | Индексы для Properties.* | Важно | `SourceContext`, `SymbolId` — будут в каждом событии |
| 4 | Self-instrumentation для .n | Важно | Без этого не могу инструментировать компилятор (он на Nemerle) |
| 5 | OTLP traces в share/KQL | Средне | Пока не упёрся, но как только начну tracing компилятора — понадобится |
| 6 | Per-workspace TTL | Средне | Пока workspace мало, но после п.1 станет актуально |

---

## Пример workflow после доработок

```
1. VSCode-сервер стартует
   → Serilog шлёт CLEF в yobalog.3po.su
   → заголовок X-YobaLog-Workspace: nemerle-lsp
   → workspace создаётся атомарно (п.1)

2. Компилятор инструментирован (п.4):
   YobaLog.Debug("literal_field_value: id={Id}, lookup={Result}", id, lookupResult);

3. В браузере: KQL-запрос в workspace nemerle-lsp
   | where SourceContext == "ConstantFolder"           ← п.3, индексировано
   | where SymbolId contains "MacroPhase"
   | order by Timestamp asc

4. Нашёл проблему → Share → KQL (п.2)
   URL: https://yobalog.3po.su/share/kql/abc123
   Коллега открывает → видит тот же KQL, может доисследовать

5. Через час workspace авто-очищается (п.6, volatile TTL)
```
