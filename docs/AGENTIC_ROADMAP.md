# Переход на Microsoft Agent Framework (MAF)

> **Статус: ✅ Завершено** — все 9 этапов выполнены.

## Цель

Миграция воркера с линейной обработки PDF на **оркестрируемый workflow** с чекпоинтами через Microsoft Agent Framework. Архитектура должна позволять легко добавлять новых агентов (перевод, NER, суммаризация) без изменения ядра.

## Требования

- **.NET SDK**: 10.0 (уже установлено: 10.0.107)
- **MAF пакет**: `Microsoft.Agents.AI` 1.5.0
- **Чекпоинты**: PostgreSQL (EF Core)
- **Шаги workflow**:
  - `DownloadDocument` — скачивание файла из storage
  - `ParseDocument` — извлечение текста через PdfPig
  - `ExtractText` — OCR fallback через Tesseract
  - `SaveResult` — сохранение текста в БД
  - `UpdateStatus` — обновление статуса документа
- **Масштабируемость**: архитектура должна позволять легко добавлять новых агентов

## Архитектура

### Текущая архитектура (MVP)

```
MassTransit Consumer → DocumentProcessingService → PdfTextExtractor → TesseractOcrService
                        ↓
                   Repository (PostgreSQL)
```

Линейная цепочка вызовов. При падении воркера — полный рестарт с нуля.

### Целевая архитектура (Agentic)

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

### Принцип работы чекпоинтов

Каждый шаг MAF-агента сохраняет своё состояние в `workflow_checkpoints` таблицу PostgreSQL. Если воркер упадёт после `ParseDocument`, при рестарте агент продолжит с `ExtractText`, а не сначала.

### Расширяемость для новых агентов

Новый агент (например, перевод) добавляется как отдельный класс, реализующий общий интерфейс `IAgent`:

```csharp
// Пример: агент перевода (не реализуем сейчас)
public class TranslationAgent : IAgent
{
    public string AgentName => "Translation";
    
    public async Task<AgentResult> ExecuteAsync(
        AgentContext context, 
        CancellationToken cancellationToken)
    {
        // 1. Получить текст из предыдущего шага (SaveResult)
        var text = context.GetPreviousResult<string>("SaveResult");
        
        // 2. Вызвать LLM или сервис перевода
        var translated = await _translationService.TranslateAsync(text, context.TargetLanguage);
        
        // 3. Сохранить результат
        return AgentResult.Success(translated);
    }
}
```

Оркестратор может объединять агентов в pipeline:

```csharp
// Пример pipeline: PDF → текст → перевод
var pipeline = agentOrchestrator
    .AddAgent<DocumentProcessingAgent>()  // текущий агент
    .AddAgent<TranslationAgent>()          // новый агент
    .Build();
```

## Этапы реализации

### Этап 1: Миграция на .NET 10 и установка MAF

- [x] Обновить `Worker.csproj` на `net10.0`
- [x] Обновить `ApiGateway.csproj` на `net10.0`
- [x] Обновить `Shared.csproj` на `net10.0`
- [x] Обновить тестовые проекты на `net10.0`
- [x] Установить `Microsoft.Agents.AI` 1.5.0 в Worker
- [x] Установить `Microsoft.Agents.AI.Abstractions` в Shared
- [x] Проверить что solution собирается

**Коммит**: `feat(worker): migrate to .NET 10 and install Microsoft.Agents.AI`

---

### Этап 2: Модели данных для чекпоинтов

- [x] Создать `WorkflowCheckpoint` модель в Shared
- [x] Создать `AgentDefinition` модель в Shared
- [x] Добавить `DbSet<WorkflowCheckpoint>` в `AppDbContext`
- [x] Добавить `DbSet<AgentDefinition>` в `AppDbContext`
- [x] Создать SQL миграцию для новых таблиц
- [x] Обновить `db/init.sql`

**Коммит**: `feat(shared): add workflow checkpoint and agent definition models`

---

### Этап 3: Интерфейсы агентов

- [x] Создать `IAgent` интерфейс в Shared
- [x] Создать `IAgentOrchestrator` интерфейс в Shared
- [x] Создать `AgentContext` класс в Shared
- [x] Создать `AgentResult` класс в Shared
- [x] Создать `ICheckpointStore` интерфейс в Shared

**Коммит**: `feat(shared): define agent abstractions (IAgent, IAgentOrchestrator, AgentContext)`

---

### Этап 4: Реализация CheckpointStore (PostgreSQL)

- [x] Создать `PostgreSqlCheckpointStore` в Worker
- [x] Реализовать `SaveCheckpointAsync`
- [x] Реализовать `LoadCheckpointAsync`
- [x] Реализовать `DeleteCheckpointAsync`
- [x] Добавить регистрацию в DI

**Коммит**: `feat(worker): implement PostgreSQL checkpoint store for MAF`

---

### Этап 5: Реализация DocumentProcessingAgent

- [x] Создать класс `DocumentProcessingAgent` в Worker
- [x] Реализовать `DownloadDocument` — скачивание из storage
- [x] Реализовать `ParseDocument` — PdfPig извлечение
- [x] Реализовать `ExtractText` — Tesseract OCR fallback
- [x] Реализовать `SaveResult` — сохранение текста
- [x] Реализовать `UpdateStatus` — обновление статуса
- [x] Каждый шаг должен сохранять чекпоинт
- [x] При старте — проверка существующего чекпоинта (resume)

**Коммит**: `feat(worker): implement DocumentProcessingAgent with MAF checkpoints`

---

### Этап 6: Рефакоринг PdfProcessingConsumer

- [x] Заменить вызов `DocumentProcessingService` на `DocumentProcessingAgent`
- [x] Сохранить MassTransit retry/DLQ как базовую защиту
- [x] Добавить логирование прогресса workflow
- [x] Обработка ошибок — чекпоинты позволяют resume

**Коммит**: `feat(worker): refactor consumer to use MAF DocumentProcessingAgent`

---

### Этап 7: Обновление Program.cs и DI

- [x] Зарегистрировать `DocumentProcessingAgent` в DI
- [x] Зарегистрировать `PostgreSqlCheckpointStore` в DI
- [x] Обновить конфигурацию MAF
- [x] Удалить старый `DocumentProcessingService` (или оставить для fallback)

**Коммит**: `feat(worker): register MAF services in DI container`

---

### Этап 8: Тесты

- [x] Создать `DocumentProcessingAgentTests`
- [x] Тест `DownloadDocument` с mock storage
- [x] Тест `ParseDocument` с тестовым PDF
- [x] Тест `ExtractText` с mock OCR
- [x] Тест `SaveResult` с in-memory DB
- [x] Тест `UpdateStatus` с проверкой статуса
- [x] Тест resume после checkpoint
- [x] Обновить существующие тесты при необходимости

**Коммит**: `test(worker): add DocumentProcessingAgent unit tests with checkpoint scenarios`

---

### Этап 9: Документация и отчёт

- [x] Обновить `README.md` — описать новую архитектуру
- [x] Создать `docs/AGENTIC_READINESS.md` — отчёт на русском
- [x] Отметить все пункты в этом roadmap как выполненные
- [x] Пример создания нового агента (перевод) в документации

**Коммит**: `docs: add agentic architecture documentation and readiness report`

## Итого

| Этап | Описание | Коммит |
|------|----------|--------|
| 1 | Миграция на .NET 10 + MAF | `feat(worker): migrate to .NET 10 and install Microsoft.Agents.AI` |
| 2 | Модели данных | `feat(shared): add workflow checkpoint and agent definition models` |
| 3 | Интерфейсы агентов | `feat(shared): define agent abstractions` |
| 4 | CheckpointStore | `feat(worker): implement PostgreSQL checkpoint store` |
| 5 | DocumentProcessingAgent | `feat(worker): implement DocumentProcessingAgent` |
| 6 | Рефакторинг Consumer | `feat(worker): refactor consumer to use MAF` |
| 7 | DI конфигурация | `feat(worker): register MAF services in DI` |
| 8 | Тесты | `test(worker): add DocumentProcessingAgent tests` |
| 9 | Документация | `docs: add agentic architecture documentation` |