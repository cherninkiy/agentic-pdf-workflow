# Agentic Readiness Report

## Статус: ✅ Завершено

Все 9 этапов миграции на MAF выполнены. Система готова к расширению через новых AI-агентов.

## Результат миграции

| Метрика | До (MVP) | После (Agentic) |
|---------|----------|-----------------|
| Платформа | .NET 8 | .NET 10 |
| Архитектура воркера | Линейная цепочка вызовов | MAF Agent с чекпоинтами |
| Resume после падения | Нет (retry с нуля) | Да (resume с последнего чекпоинта) |
| Тесты Worker | 7 | 16 (+9 MAF/checkpoint тестов) |
| Тесты всего | 19 | 28 |
| Добавление нового агента | Изменение Consumer | Новый класс `IAgent` + DI |

## Архитектура

### Компоненты

```
┌─────────────────────────────────────────────────────────────┐
│                    MassTransit Consumer                     │
│  (приём сообщений из RabbitMQ, retry/DLQ — без изменений)   │
└──────────────────────────┬──────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│              DocumentProcessingAgent (MAF)                  │
│                                                             │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐     │
│  │ Download     │──▶│ Parse        │──▶│ Extract      │     │
│  │ Document     │   │ Document     │   │ Text         │     │
│  └──────────────┘   └──────────────┘   └──────────────┘     │
│        │ checkpoint      │ checkpoint      │ checkpoint     │
│        ▼                 ▼                 ▼                │
│  ┌──────────────┐   ┌──────────────┐                        │
│  │ Save         │──▶│ Update       │                        │
│  │ Result       │   │ Status       │                        │
│  └──────────────┘   └──────────────┘                        │
│        │ checkpoint      │ checkpoint                       │
└─────────────────────────────────────────────────────────────┘
                           │
                           ▼
               ┌────────────────────────┐
               │  CheckpointStore       │
               │  (PostgreSQL + EF Core)│
               └────────────────────────┘
```

### Ключевые интерфейсы (Shared)

| Интерфейс | Назначение |
|-----------|------------|
| `IAgent` | Контракт агента: `AgentName`, `Activities`, `ExecuteAsync` |
| `IAgentOrchestrator` | Оркестрация пайплайна из нескольких агентов |
| `ICheckpointStore` | Сохранение/загрузка чекпоинтов |
| `AgentContext` | Контекст выполнения: `DocumentId`, `FilePath`, `CurrentActivity` |
| `AgentResult` | Результат шага: `IsSuccess`, `OutputData`, `ErrorMessage` |

### Модели данных (Shared)

| Модель | Назначение |
|--------|------------|
| `WorkflowCheckpoint` | Запись чекпоинта: `AgentName`, `DocumentId`, `CurrentActivity`, `StateData`, `IsCompleted`, `IsFailed` |
| `AgentDefinition` | Определение агента для оркестратора |

### Таблицы PostgreSQL

| Таблица | Назначение |
|---------|------------|
| `documents` | Метаданные документов (статус, текст, путь) |
| `outbox` | Транзакционный outbox |
| `processed_messages` | Идемпотентность потребителя |
| `workflow_checkpoints` | Чекпоинты MAF-агентов |
| `agent_definitions` | Определения агентов |

## Особенности миграции на .NET 10 + MAF

### Требования MAF к платформе

> [**System Requirements: .NET 10.0+**](https://github.com/microsoft/semantic-kernel/pkgs/nuget/Microsoft.SemanticKernel.Connectors.Memory.Kusto#system-requirements)

Релиз MAF не поддерживает .NET 8 и более ранние версии. По этой причине была выполнена миграция всего решения с `.net8.0` на `.net10.0` (Этап 1).

### Проблема совместимости пакетов

При переходе с `net8.0` на `net10.0` обнаружена несовместимость версий NuGet-пакетов:

- **MassTransit 8.5.9** объявляет зависимость от `Microsoft.Extensions.Diagnostics.HealthChecks (>= 10.0.0)` для .NET 10
- В проектах было `Version="8.0.*"` — это вызывало `NU1605: Detected package downgrade`
- **Решение**: обновить все пакеты `Microsoft.Extensions.*` и `Microsoft.EntityFrameworkCore.*` до версий 10.x

### Миграция на Microsoft Agent Framework

**Ключевые решения:**

1. **Гибридная архитектура**: MassTransit остаётся на границе сервисов (RabbitMQ, retry/DLQ), MAF работает внутри воркера как движок workflow.

2. **Чекпоинты в PostgreSQL**: Вместо in-memory хранилища используется PostgreSQL через EF Core. Это позволяет переживать перезапуски воркера.

3. **Resume логика**: При старте `ExecuteAsync` загружает завершённые чекпоинты и пропускает уже выполненные шаги, восстанавливая состояние из `StateData`.

4. **Base64 для бинарных данных**: PDF байты сохраняются в чекпоинте как Base64-строка. При resume — декодируются обратно в `byte[]`.

5. **Failure checkpoint**: При исключении сохраняется чекпоинт с `IsFailed = true` и сообщением ошибки. Это позволяет анализировать причины сбоев.

6. **Расширяемость через IAgent**: Новый агент — это просто новый класс, реализующий `IAgent`. Не требует изменения существующего кода.

## Пример: создание нового агента (TranslationAgent)

### Шаг 1: Создать класс агента

```csharp
using Shared.Interfaces;
using Shared.Models;
using Microsoft.Extensions.Logging;

namespace Worker.Agents;

public class TranslationAgent : IAgent
{
    public string AgentName => "Translation";

    public IReadOnlyList<string> Activities => new List<string>
    {
        "DetectLanguage",
        "TranslateText",
        "SaveTranslation"
    }.AsReadOnly();

    private readonly ITranslationService _translationService;
    private readonly IDocumentRepository _repository;
    private readonly ILogger<TranslationAgent> _logger;

    public TranslationAgent(
        ITranslationService translationService,
        IDocumentRepository repository,
        ILogger<TranslationAgent> logger)
    {
        _translationService = translationService;
        _repository = repository;
        _logger = logger;
    }

    public async Task<AgentResult> ExecuteAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Translation for {DocumentId}", context.DocumentId);

        // Загрузить завершённые чекпоинты (resume support)
        var completed = await checkpointStore.LoadCompletedCheckpointsAsync(
            AgentName, context.DocumentId, cancellationToken);
        var completedActivities = completed
            .Where(c => c.IsCompleted && !c.IsFailed)
            .Select(c => c.CurrentActivity)
            .ToHashSet();

        try
        {
            // Шаг 1: Определить язык
            string detectedLanguage;
            if (completedActivities.Contains("DetectLanguage"))
            {
                var cp = completed.First(c => c.CurrentActivity == "DetectLanguage");
                detectedLanguage = cp.StateData ?? "en";
            }
            else
            {
                var text = context.GetPreviousResult<string>("ExtractText");
                detectedLanguage = await _translationService.DetectLanguageAsync(
                    text, cancellationToken);
                await checkpointStore.SaveCheckpointAsync(
                    AgentName, context.DocumentId, "DetectLanguage",
                    AgentResult.Success(detectedLanguage), cancellationToken);
            }

            // Шаг 2: Перевести
            string translatedText;
            if (completedActivities.Contains("TranslateText"))
            {
                var cp = completed.First(c => c.CurrentActivity == "TranslateText");
                translatedText = cp.StateData ?? string.Empty;
            }
            else
            {
                var text = context.GetPreviousResult<string>("ExtractText");
                translatedText = await _translationService.TranslateAsync(
                    text, detectedLanguage, context.TargetLanguage, cancellationToken);
                await checkpointStore.SaveCheckpointAsync(
                    AgentName, context.DocumentId, "TranslateText",
                    AgentResult.Success(translatedText), cancellationToken);
            }

            // Шаг 3: Сохранить
            if (!completedActivities.Contains("SaveTranslation"))
            {
                await _repository.SaveTranslationAsync(
                    context.DocumentId, translatedText, cancellationToken);
                await checkpointStore.SaveCheckpointAsync(
                    AgentName, context.DocumentId, "SaveTranslation",
                    AgentResult.Success(), cancellationToken);
            }

            // Очистить чекпоинты
            await checkpointStore.DeleteCheckpointsAsync(
                AgentName, context.DocumentId, cancellationToken);

            return AgentResult.Success(translatedText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Translation failed for {DocumentId}", context.DocumentId);
            await checkpointStore.SaveCheckpointAsync(
                AgentName, context.DocumentId, "Failure",
                AgentResult.Failure(ex.Message), cancellationToken);
            throw;
        }
    }
}
```

### Шаг 2: Зарегистрировать в DI

```csharp
// В Program.cs воркера
builder.Services.AddScoped<TranslationAgent>();
builder.Services.AddScoped<ITranslationService, AzureTranslationService>();
```

### Шаг 3: Подключить к оркестратору

```csharp
// Пайплайн: PDF → текст → перевод
var pipeline = agentOrchestrator
    .AddAgent<DocumentProcessingAgent>()
    .AddAgent<TranslationAgent>()
    .Build();
```

## Тесты

### Покрытие чекпоинт-сценариев

| Тест | Сценарий |
|------|----------|
| `ExecuteAsync_FullWorkflow_CompletesSuccessfully` | Первый запуск, все 5 шагов |
| `ExecuteAsync_ResumeAfterCrash_SkipsCompletedActivities` | Resume после 2 шагов |
| `ExecuteAsync_ResumeFromMiddle_SkipsFirstThreeActivities` | Resume после 3 шагов |
| `ExecuteAsync_ResumeFromLastActivity_SkipsFirstFourActivities` | Resume после 4 шагов |
| `ExecuteAsync_AllActivitiesCompleted_OnlyCleansUp` | Все 5 шагов уже выполнены |
| `ExecuteAsync_CheckpointStateData_RoundtripsBase64Bytes` | Roundtrip бинарных данных через Base64 |
| `ExecuteAsync_FailureCheckpoint_PreservesErrorMessage` | Сообщение ошибки в failure checkpoint |

## Метрики

### До миграции (MVP)

- 19 тестов (8 ApiGateway + 7 Worker + 4 Integration)
- .NET 8
- Линейная обработка без resume

### После миграции (Agentic)

- 28 тестов (8 ApiGateway + 16 Worker + 4 Integration)
- .NET 10
- MAF Agent с чекпоинтами и resume