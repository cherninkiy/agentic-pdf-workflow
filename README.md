# Система обработки PDF (Agentic)

## Обзор

Этот репозиторий реализует систему обработки PDF‑документов в соответствии с архитектурным решением [ADR001](docs/adr/ADR001_PDF_Processing_Architecture.md) и планом реализации [AGENTIC_ROADMAP](docs/AGENTIC_ROADMAP.md). Система состоит из двух сервисов:

| Сервис | Ответственность |
|--------|-----------------|
| **ApiGateway** | HTTP‑API для загрузки PDF, получения списка документов и извлечённого текста. Реализует паттерн транзакционного outbox для надёжной доставки сообщений. |
| **Worker** | MAF‑оркестрируемый workflow с чекпоинтами. Извлекает текст из PDF (PdfPig + Tesseract OCR fallback), сохраняет результат и обновляет статус документа. При падении воркера — resume с последнего чекпоинта. |

Оба сервиса используют общую библиотеку **Shared**, содержащую контракты, DTO, перечисления и интерфейсы.

## Архитектура (Agentic)

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

Каждый шаг MAF-агента сохраняет своё состояние в таблицу `workflow_checkpoints` PostgreSQL. Если воркер упадёт после `ParseDocument`, при рестарте агент продолжит с `ExtractText`, а не сначала.

### Workflow шагов

| # | Шаг | Описание | Чекпоинт |
|---|-----|----------|----------|
| 1 | `DownloadDocument` | Скачивание PDF из файлового хранилища | PDF bytes (Base64) |
| 2 | `ParseDocument` | Извлечение текста через PdfPig | Извлечённый текст |
| 3 | `ExtractText` | OCR fallback через Tesseract если PdfPig вернул пустой текст | Финальный текст |
| 4 | `SaveResult` | Сохранение текста в БД | — |
| 5 | `UpdateStatus` | Обновление статуса документа на `completed` | — |

## Структура проекта

```
/src
  /ApiGateway          – ASP.NET Core Web API
  /Worker              – .NET Worker (MassTransit + MAF Agent)
  /Shared              – Контракты, модели и интерфейсы
/tests
  /ApiGateway.UnitTests – Юнит- и smoke-тесты API (8 тестов)
  /Worker.UnitTests     – Юнит-тесты воркера (16 тестов, включая MAF)
  /IntegrationTests     – Интеграционные тесты через Testcontainers (4 теста)
/samples                – Примеры PDF для тестирования (текстовый, скан, инвойс)
/scripts                – Вспомогательные скрипты (demo.sh)
/.github
  /workflows/ci.yml     – CI-pipeline (build, test x3, Docker образы)
docs/
  /adr/                 – Архитектурные решения (ADR-001)
  /AGENTIC_ROADMAP.md   – План миграции на MAF
  /AGENTIC_READINESS.md – Отчёт о готовности agentic-архитектуры
  /PRODUCTION_READINESS.md – Оценка production-готовности
  /TASK_COMPLETENESS.md – Отчёт о полноте реализации
  /ROADMAP.md           – План реализации MVP
/grafana
  /dashboards/          – Преднастроенный дашборд Grafana
/prometheus
  /prometheus.yml       – Конфигурация сбора метрик
docker-compose.yml      – Оркестрация PostgreSQL, RabbitMQ, ApiGateway и Worker
db/init.sql             – Инициализационный скрипт БД
```

## Документация

| Документ | Описание |
|----------|----------|
| [AGENTIC_ROADMAP.md](docs/AGENTIC_ROADMAP.md) | План миграции на MAF по этапам (1–9), статус выполнения |
| [AGENTIC_READINESS.md](docs/AGENTIC_READINESS.md) | Отчёт о готовности agentic-архитектуры |
| [ROADMAP.md](docs/ROADMAP.md) | План реализации MVP по дням (1–7) |
| [TASK_COMPLETENESS.md](docs/TASK_COMPLETENESS.md) | Сопоставление требований ТЗ с реализованным функционалом |
| [PRODUCTION_READINESS.md](docs/PRODUCTION_READINESS.md) | Оценка готовности к production |
| [ADR001](docs/adr/ADR001_PDF_Processing_Architecture.md) | Архитектурное решение: выбор MAF + MassTransit |

## Начало работы

### Предварительные требования

- **.NET 10 SDK** (`dotnet --version` → `10.0.x`)
- **Docker** (для запуска полной инфраструктуры)
- **PostgreSQL** и **RabbitMQ** (поднимаются через Docker Compose)

### Запуск локально (разработка)

1. **Клонировать репозиторий**
   ```bash
   git clone https://github.com/cherninkiy/agentic-pdf-workflow.git
   cd agentic-pdf-workflow
   ```

2. **Собрать проекты**
   ```bash
   dotnet build
   ```

3. **Запустить вспомогательные сервисы**
   ```bash
   docker compose up -d postgres rabbitmq
   ```

4. **Запустить API‑шлюз**
   ```bash
   cd src/ApiGateway
   dotnet run
   ```

5. **Запустить воркер** (в отдельном терминале)
   ```bash
   cd src/Worker
   dotnet run
   ```

6. **Взаимодействовать с API** – Swagger UI доступен по адресу `http://localhost:5000/swagger`.

### Запуск демо-скрипта

Автоматизированный скрипт `scripts/demo.sh` поднимает инфраструктуру, запускает сервисы, загружает все PDF из `samples/` и выводит результат:

```bash
./scripts/demo.sh
```

### Запуск тестов

```bash
dotnet test
```

Результат: **28 тестов** (8 ApiGateway + 16 Worker + 4 Integration) — все проходят.

## CI‑pipeline

GitHub Actions (`.github/workflows/ci.yml`) выполняет:

1. Установку системных зависимостей (Tesseract OCR + poppler-utils).
2. Восстановление и сборку всех проектов.
3. Запуск юнит‑тестов ApiGateway (8 тестов).
4. Запуск юнит‑тестов Worker (16 тестов: MAF agent, checkpoints, OCR, cancellation).
5. Запуск интеграционных тестов через Testcontainers (4 теста: PostgreSQL + RabbitMQ).
6. Сборку Docker‑образов `pdf-api-gateway` и `pdf-worker`.

## Гибридная архитектура: MAF + MassTransit

Система использует оба фреймворка на разных уровнях:

| Аспект | Технология | Роль |
|--------|------------|------|
| Межсервисная коммуникация | MassTransit | Приём команд от Gateway, retry/DLQ, надёжная доставка |
| Оркестрация шагов обработки | MAF (DocumentProcessingAgent) | Чекпоинты, resume после падения, расширяемость агентов |

### Почему не только MassTransit?

MassTransit обеспечивает надёжную доставку сообщений между сервисами, но **не решает проблему падения воркера внутри одной обработки**. Если Worker упал после PdfPig, но до сохранения в БД — MassTransit сделает retry всего сообщения с нуля.

MAF добавляет **чекпоинты на каждом шаге обработки**:

```
Без MAF:  crash → retry с нуля (Download → Parse → Extract → Save → Update)
С MAF:   crash → resume с последнего чекпоинта (Extract → Save → Update)
```

### Добавление новых агентов

Архитектура спроектирована для легкого добавления новых агентов. Пример — агент перевода:

```csharp
public class TranslationAgent : IAgent
{
    public string AgentName => "Translation";

    public IReadOnlyList<string> Activities => new List<string>
    {
        "DetectLanguage",
        "TranslateText",
        "SaveTranslation"
    }.AsReadOnly();

    public async Task<AgentResult> ExecuteAsync(
        AgentContext context,
        ICheckpointStore checkpointStore,
        CancellationToken cancellationToken = default)
    {
        // 1. Получить текст из предыдущего агента
        var text = context.GetPreviousResult<string>("ExtractText");

        // 2. Определить язык
        var language = await DetectLanguageAsync(text, cancellationToken);
        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "DetectLanguage",
            AgentResult.Success(language), cancellationToken);

        // 3. Перевести
        var translated = await _translationService.TranslateAsync(
            text, language, context.TargetLanguage, cancellationToken);
        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "TranslateText",
            AgentResult.Success(translated), cancellationToken);

        // 4. Сохранить
        await _repository.SaveTranslationAsync(
            context.DocumentId, translated, cancellationToken);
        await checkpointStore.SaveCheckpointAsync(
            AgentName, context.DocumentId, "SaveTranslation",
            AgentResult.Success(), cancellationToken);

        return AgentResult.Success();
    }
}
```

Новый агент подключается к оркестратору без изменения существующего кода:

```csharp
// В Pipeline — добавить после DocumentProcessingAgent
var pipeline = agentOrchestrator
    .AddAgent<DocumentProcessingAgent>()
    .AddAgent<TranslationAgent>()  // новый агент
    .Build();
```

## Выбор OCR‑решения

Для распознавания текста в отсканированных PDF используется **Tesseract OCR** (локальный, запускается через `pdftoppm` + `tesseract`).

Почему не другие варианты:

- **Azure AI Document Intelligence** — аккаунт долго верифицируется, не дождался ключа.
- **OCRBase (ocrbase.dev)** — API крайне медленный, запросы зависали на минуты.
- **OCR.Space** — аналогично, высокая задержка, нестабильная работа.

Tesseract работает локально, без интернета, бесплатно, не требует API-ключей.

## Известные ограничения и отложенные улучшения

- **Отдельный обработчик DLQ.** Сейчас ошибки идут в `_error` очередь MassTransit без отдельного сервиса‑обработчика.
- **Безопасность.** Нет авторизации, HTTPS. Для production — JWT + reverse proxy.
- **AI-агенты.** Перевод, суммаризация, NER — запланированы как следующие агенты.

## Сводка рабочего процесса

* **Загрузка (`POST /upload`)**
  1. Проверка файла (PDF, ≤ 4 МБ).
  2. Сохранение файла через `IFileStorage`.
  3. Создание `DocumentDto` со статусом `Uploaded`.
  4. Создание `OutboxMessage` с `PdfProcessingCommand`.
  5. Сохранение обеих записей в одной транзакции.

* **Outbox Publisher** (`BackgroundService`)
  - Периодически сканирует таблицу `outbox` и публикует непроцессированные сообщения в RabbitMQ через MassTransit.
  - После успешной публикации помечает запись как обработанную.

* **Worker (MAF Agent)**
  1. Idempotency check (processed_messages).
  2. Атомарное взятие задачи (UPDATE WHERE status=uploaded).
  3. Запуск `DocumentProcessingAgent` с чекпоинтами.
  4. Каждый шаг сохраняет чекпоинт в PostgreSQL.
  5. При падении — resume с последнего чекпоинта.
  6. После успешного завершения — очистка чекпоинтов.
  7. ACK сообщения.

## Итог

| Метрика | Значение |
|---------|----------|
| Язык / платформа | C# / .NET 10 |
| Брокер сообщений | RabbitMQ 4.x через MassTransit |
| База данных | PostgreSQL 16 + EF Core 10 |
| Agent Framework | Microsoft Agent Framework (MAF) |
| OCR (сканы) | Tesseract 5.3 (pdftoppm → PNG → tesseract) |
| Тесты | 28: 8 unit + 16 unit + 4 integration (Testcontainers) |
| Мониторинг | Prometheus + Grafana (health checks, метрики) |
| Демо | `./scripts/demo.sh` |
| Ссылки | [AGENTIC_ROADMAP](docs/AGENTIC_ROADMAP.md) · [AGENTIC_READINESS](docs/AGENTIC_READINESS.md) · [ADR](docs/adr/ADR001_PDF_Processing_Architecture.md) |