# Magentic-оркестрация для кодогенерации в Harness.Core

Дата: 2026-07-28
Статус: утверждён, готов к планированию реализации

## Задача

Добавить в харнесс оркестрацию Microsoft Agent Framework Workflows — Magentic
([документация](https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/magentic?pivots=programming-language-csharp))
для сложных задач кодогенерации. Оркестрация должна работать по схеме из документации:
Task Ledger → Progress Ledger → проверки «задача выполнена?» / «есть ли прогресс?» /
«stall count > 2 → replan» → финальный ответ. Участники оркестрации должны сохранять все
текущие возможности харнесса (файлы, локальный поиск, PowerShell, песочница Python,
плагины, навыки, файловая память, трассировка).

Код размещается в `namespace Harness.Core.Workflows`, класс `Magentic`, и регистрируется
в `AgentHost`.

## Решения, принятые при обсуждении

| Вопрос | Решение |
|---|---|
| Точка входа | Инструмент агента `magentic_codegen` в `ChatOptions.Tools`; главный ReAct-агент делегирует задачу сам |
| Состав участников | Architect + Planner (пре-фаза) → Coder + Reviewer + Tester (цикл Magentic), менеджер отдельно |
| Подтверждения внутри оркестрации | Авто-одобрение; пользователь подтверждает один раз сам вызов `magentic_codegen` |
| Видимость прогресса | Serilog + OpenTelemetry + Markdown-отчёт в рабочей папке; UI не трогаем |
| Модель | Текущая модель хоста для всех шести агентов (общий `OllamaApiClient`) |

## Архитектура

### Зависимости

- `Microsoft.Agents.AI.Workflows` **1.15.0** (проверено на nuget.org; версия совпадает
  с остальными пакетами Agent Framework в проекте) → `Harness.Core.csproj`.
- `NoWarn` в `Harness.Core.csproj` дополняется `MAAIW001` (типы Magentic экспериментальные).

Используемые типы: `MagenticWorkflowBuilder`, `MagenticPlanCreatedEvent`,
`MagenticReplannedEvent`, `MagenticProgressLedgerUpdatedEvent`
(`Microsoft.Agents.AI.Workflows.Specialized.Magentic`), `InProcessExecution`,
`StreamingRun`, `TurnToken`, `WorkflowOutputEvent`, `WorkflowErrorEvent`,
`ExecutorFailedEvent`, `AgentRunUpdateEvent` (`Microsoft.Agents.AI.Workflows`).

### Класс `Harness.Core.Workflows.Magentic`

Единственный публичный тип фичи. Не создаёт собственную инфраструктуру — принимает уже
существующие ресурсы `AgentHost`, поэтому не участвует в их жизненном цикле и не требует
`IDisposable`.

Конструктор принимает:

- `IChatClient chatClient` — тот же `OllamaApiClient`, что и у главного агента (смена
  модели через `AgentHost.SetModelAsync` автоматически действует и на оркестрацию);
- `string workingDirectory` — рабочая папка сессии (корень FileAccess и отчётов);
- `HyperlightCodeActProvider codeAct` — песочница Python для Coder;
- `LocalShellExecutor shellExecutor` — PowerShell для Tester;
- `AIFunction searchFilesTool` — локальный поиск по содержимому;
- `Func<IEnumerable<AIFunction>> pluginTools` — инструменты плагинов; делегат, а не
  снимок списка, потому что плагины загружаются горячо;
- `int maxContextWindowTokens`, `int maxOutputTokens` — те же бюджеты, что у хоста;
- `string tracingSourceName` — имя источника OpenTelemetry.

Публичная поверхность:

- `AIFunction AsAIFunction()` — инструмент `magentic_codegen(task)`, описание и параметры
  на русском (как у `search_files`);
- `Task<string> RunAsync(string task, CancellationToken cancellationToken)` — тело
  инструмента; возвращает финальный ответ менеджера и путь к отчёту.

### Шесть агентов

Пять ролей (кроме менеджера) строятся через `chatClient.AsHarnessAgent(new HarnessAgentOptions { ... })` —
то есть получают весь текущий набор преимуществ харнесса: FileAccess с корнем в рабочей
папке, файловую память, провайдер навыков (`skills/`), todo-провайдер, телеметрию,
`DisableWebSearch = true`, greedy-сэмплинг (`Temperature = 0`, `TopP = 1`, `Seed = 0`) и
те же лимиты токенов. Различаются инструкцией, именем и набором инструментов:

| Роль | Инструменты | Фаза |
|---|---|---|
| `Architect` | `search_files`, чтение файлов (FileAccess read-only) | пре-фаза |
| `Planner` | `search_files`, чтение файлов | пре-фаза |
| `Manager` | нет | цикл |
| `Coder` | чтение/запись/правка файлов, `execute_code` (CodeAct), инструменты плагинов, `search_files` | цикл |
| `Reviewer` | `search_files`, чтение файлов | цикл |
| `Tester` | `run_shell` (`dotnet build`, `dotnet test`, запуск скриптов), чтение файлов | цикл |

Read-only роли (`Architect`, `Planner`, `Reviewer`, `Tester`) получают `FileAccessStore`
с корнем в рабочей папке и `FileAccessProviderOptions { DisableWriteTools = true }` —
запись отключена самим провайдером, а не только инструкцией; менять файлы может только
`Coder`. Менеджер инструментов не получает вовсе и строится как обычный `ChatClientAgent`,
а не харнесс-агент: оркестратор Magentic подставляет собственные промпты леджеров и
разбирает ответ как JSON, поэтому системная обвязка харнесса ему только мешает.

Подтверждения: `ToolApprovalAgentOptions` участников настроен на авто-одобрение
(`requireApproval: false` для shell, авто-правила для остальных инструментов).
Оправдание: пользователь уже подтвердил сам вызов `magentic_codegen`, канал не имеет
UI для диалогов (как и `RunPluginRequestAsync`), а защита остаётся структурной —
deny-list в `ShellPolicy` (`rm -rf`, `sudo`, fork-bomb, `mkfs`, `Format-Volume`, запись
в `/dev/sd`), таймаут shell 30 секунд, FileAccess ограничен рабочей папкой, Python
исполняется в Hyperlight-песочнице.

### Поток выполнения

1. **Architect** получает исходную задачу и разбирает кодовую базу: текущая структура,
   затрагиваемые файлы, целевое решение, риски. Результат — один `ChatMessage`.
2. **Planner** получает задачу + разбор архитектора и выдаёт пронумерованный пошаговый
   план работ с критериями приёмки для каждого шага.
3. Задача для менеджера собирается как исходный запрос + разбор архитектора + план.
   Обе пре-фазные роли вызываются напрямую (`agent.RunAsync`), вне workflow —
   так порядок «сначала архитектура и план, потом цикл» гарантирован, а не оставлен
   на усмотрение менеджера.
4. Строится workflow:

   ```csharp
   Workflow workflow = new MagenticWorkflowBuilder(managerAgent)
       .AddParticipants([coderAgent, reviewerAgent, testerAgent])
       .WithName("Harness Magentic Codegen")
       .WithDescription("Coder, reviewer and tester driven by a Magentic manager.")
       .RequirePlanSignoff(false)
       .WithMaxRounds(10)
       .WithMaxStalls(3)
       .WithMaxResets(2)
       .Build();
   ```

   Внутренний цикл (Task Ledger, Progress Ledger, проверки завершения/прогресса/stall,
   replan, финальный синтез) реализован самим фреймворком — это ровно схема из
   документации, руками её писать не нужно.
5. Запуск: `InProcessExecution.RunStreamingAsync(workflow, [new ChatMessage(ChatRole.User, task)])`,
   затем `run.TrySendMessageAsync(new TurnToken(emitEvents: true))`.
6. Поток событий `run.WatchStreamAsync()` разбирается по типам:

   | Событие | Действие |
   |---|---|
   | `MagenticPlanCreatedEvent` | Serilog Information + раздел «План» в отчёт |
   | `MagenticReplannedEvent` | Serilog Warning + раздел «Перепланирование» в отчёт |
   | `MagenticProgressLedgerUpdatedEvent` | Serilog Information + строка раунда (speaker, инструкция, признаки завершения/прогресса) в отчёт |
   | `AgentRunUpdateEvent` | накопление текста ответа по `ExecutorId` (кто говорит) в отчёт |
   | `WorkflowOutputEvent` с `List<ChatMessage>` | финальный ответ |
   | `WorkflowErrorEvent`, `ExecutorFailedEvent` | Serilog Error + раздел «Ошибки» в отчёт, цикл не прерывается принудительно |

7. Отчёт пишется в `<workingDir>/magentic/run-yyyyMMdd-HHmmss.md`: задача, разбор
   архитектора, план, раунды (speaker + леджер + ответ агента), ошибки, финальный ответ.
   Папка создаётся при первом запуске.
8. Инструмент возвращает финальный ответ менеджера и путь к отчёту. Ответ обрезается по
   верхней границе (порядка 8000 символов) с пометкой об обрезке — полный текст всегда
   остаётся в отчёте, чтобы длинный транскрипт не съедал контекст главного агента.

### Регистрация в `AgentHost`

- Экземпляр `Magentic` создаётся локально в `BuildAgent()`, а его `AsAIFunction()`
  добавляется в `ChatOptions.Tools`. Поле не нужно: `BuildAgent()` вызывается при смене
  модели и при горячей загрузке плагинов, поэтому оркестрация всегда получает актуальные
  бюджеты токенов, а инструменты плагинов передаются делегатом и тоже остаются актуальными.
- В `instructions` добавляется абзац: `magentic_codegen` — многоагентная оркестрация для
  крупных задач кодогенерации (архитектор и планировщик строят план, затем цикл
  кодер → ревьюер → тестировщик), с явным указанием, что для мелких правок её вызывать
  не нужно.
- Инструмент помечается как требующий подтверждения — один диалог на запуск оркестрации.

### Обработка ошибок и отмена

- Ошибки исполнителей (`ExecutorFailedEvent`) и workflow (`WorkflowErrorEvent`) попадают
  в отчёт и в возвращаемый текст, но не превращаются в исключение: менеджер по схеме
  Magentic отдаёт «educated guess», и этот результат полезнее, чем пустой отказ.
- Исчерпание лимитов раундов/сбросов — штатный исход: возвращается последний ответ
  менеджера с пометкой, что лимит достигнут.
- `CancellationToken` пробрасывается в пре-фазу и в поток событий; при отмене отчёт
  дописывается и сохраняется (запись отчёта в `finally`).
- Отказ записи отчёта (`IOException`, `UnauthorizedAccessException`) логируется через
  `Log.Warning` и не роняет запуск — тот же приём, что и в `SeedExampleScripts`.

## Что намеренно не делается

- **Human-in-the-loop plan review** (`RequirePlanSignoff(true)`): требует чекпоинтов и
  проброса `MagenticPlanReviewRequest` в оба хоста. План и так проверяем в отчёте, а сам
  запуск подтверждается диалогом. Добавить, если появится потребность править план на лету.
- **Живой прогресс в UI**: событие `AgentHost` + подписки в `MyHarness` и `HarnessCli`.
  Пока хватает Serilog и отчёта.
- **Отдельные модели для ролей**: конфигурация в `secret.json`. Все роли используют
  текущую модель хоста.
- **Тесты**: тестового проекта в решении нет; тесты не пишутся без отдельного запроса.

## Критерии приёмки

1. `Harness.Core` собирается без предупреждений (`MAAIW001` подавлен точечно в `NoWarn`).
2. `magentic_codegen` виден главному агенту и вызывается с подтверждением.
3. Запуск на реальной задаче кодогенерации доходит до финального ответа, а в
   `<workingDir>/magentic/` появляется отчёт с планом, раундами и финальным ответом.
4. Смена модели в UI и горячая загрузка плагина не ломают инструмент (агент
   пересобирается, инструмент остаётся, Coder видит новые инструменты плагинов).
5. Отмена запуска оставляет корректный отчёт.
