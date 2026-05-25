# Система обработки PDF (MVP)

## Обзор

Этот репозиторий реализует систему обработки PDF‑документов в соответствии с архитектурным решением [ADR001](docs/adr/ADR001_PDF_Processing_Architecture.md) и планом реализации [roadmap](docs/roadmap.md). Система состоит из двух сервисов:

| Сервис | Ответственность |
|--------|-----------------|
| **ApiGateway** | HTTP‑API для загрузки PDF, получения списка документов и извлечённого текста. Реализует паттерн транзакционного outbox для надёжной доставки сообщений. |
| **Worker** | Потребитель сообщений `PdfProcessingCommand`, извлекает текст из PDF (PdfPig + Tesseract OCR fallback), сохраняет результат и обновляет статус документа. |

Оба сервиса используют общую библиотеку **Shared**, содержащую контракты, DTO, перечисления и интерфейсы.

## Структура проекта

```
/src
  /ApiGateway          – ASP.NET Core Web API
  /Worker              – .NET Worker (MassTransit consumer)
  /Shared              – Контракты, модели и интерфейсы
/tests
  /ApiGateway.UnitTests – Юнит‑ и smoke‑тесты API (8 тестов)
  /Worker.UnitTests     – Юнит‑тесты воркера (7 тестов, включая Tesseract OCR)
  /IntegrationTests     – Интеграционные тесты через Testcontainers (4 теста)
/samples                – Примеры PDF для тестирования (текстовый, скан, инвойс)
/.github
  /workflows/ci.yml     – CI‑pipeline (build, test x3, Docker образы)
docs/
  /adr/                 – Архитектурные решения (ADR-001)
  /roadmap.md           – План реализации
docker-compose.yml      – Оркестрация PostgreSQL, RabbitMQ, ApiGateway и Worker
db/init.sql             – Инициализационный скрипт БД
```

## Начало работы

### Предварительные требования

- **.NET 8 SDK** (`dotnet --version` → `8.0.x`)
- **Docker** (для запуска полной инфраструктуры)
- **PostgreSQL** и **RabbitMQ** (поднимаются через Docker Compose)

### Запуск локально (разработка)

1. **Клонировать репозиторий**
   ```bash
   git clone https://github.com/cherninkiy/agentic-pdf-workflow.git
   cd agentic-pdf-workflow
   ```

2. **Запустить вспомогательные сервисы**
   ```bash
   docker compose up -d postgres rabbitmq
   ```

3. **Запустить API‑шлюз**
   ```bash
   cd src/ApiGateway
   dotnet run
   ```

4. **Запустить воркер** (в отдельном терминале)
   ```bash
   cd src/Worker
   dotnet run
   ```

5. **Взаимодействовать с API** – Swagger UI доступен по адресу `http://localhost:5000/swagger`.

### Запуск тестов

Все юнит‑ и smoke‑тесты работают с in‑memory базой и не требуют внешних сервисов.

```bash
dotnet test
```

## CI‑pipeline

GitHub Actions (`.github/workflows/ci.yml`) выполняет:

1. Установку системных зависимостей (Tesseract OCR + poppler-utils).
2. Восстановление и сборку всех проектов.
3. Запуск юнит‑тестов ApiGateway (8 тестов).
4. Запуск юнит‑тестов Worker (7 тестов: обработка, OCR, отмена).
5. Запуск интеграционных тестов через Testcontainers (4 теста: PostgreSQL + RabbitMQ).
6. Сборку Docker‑образов `pdf-api-gateway` и `pdf-worker`.

Пайплайн запускается при каждом push/PR в ветку `main` и в feature‑ветки.

## MAF vs MassTransit: выбор технологий

В [ADR001](docs/adr/ADR001_PDF_Processing_Architecture.md) изначально планировалось использовать **Microsoft Agent Framework (MAF)** для оркестрации шагов обработки документа (`DownloadDocument → UpdateStatusProcessing → ExtractTextStep → SaveTextAndComplete`) с встроенными чекпоинтами и декларативными ретраями. Однако в ходе реализации MVP был выбран **MassTransit** – зрелый фреймворк для обмена сообщениями.

На практике `MassTransit` взял на себя бóльшую часть того, что должен был дать MAF:

| Задача | MassTransit | MAF |
|--------|-------------|---------------------|
| Надёжная доставка сообщений через RabbitMQ | ✅ First‑class поддержка, конфигурация в несколько строк | ❌ Требует ручной настройки поверх Raw RabbitMQ |
| Retry‑механизм (5s → 30s → 60s) | ✅ `.UseMessageRetry()` с экспоненциальной задержкой | ❌ Нужно писать кастомный ретрай посредник |
| Dead Letter Queue | ✅ Встроенная `_error` очередь | ❌ Отсутствует, требуется самостоятельная реализация |
| Graceful Shutdown | ✅ Автоматически обрабатывает SIGTERM | ❌ Не документирован |
| Ограничение параллелизма (prefetch=1) | ✅ `e.PrefetchCount` | ❌ Нет поддержки |
| Idempotency Consumer | ✅ Легко реализуется через фильтры | ❌ Нет встроенных механизмов |

**Вывод для MVP:** MassTransit позволяет быстро получить надёжную систему обмена сообщениями без написания низкоуровневого кода. Это прагматичный выбор, который гарантирует стабильность на старте.

### MAF сегодня (на момент MVP)

С апреля 2026 **Microsoft Agent Framework стал production‑ready** и официально рекомендован для **координации AI‑агентов** (перевод, суммаризация, классификация, маршрутизация). MAF предоставляет:

- **Durable workflows** – чекпоинты на каждом шаге, позволяющие продолжить обработку после падения воркера.
- **Agent‑ориентированную модель** – каждый агент имеет свою память, инструменты и может общаться с другими агентами.
- **Встроенную наблюдаемость** через OpenTelemetry.
- **Поддержку LLM** (Semantic Kernel под капотом) для принятия решений на основе извлечённого текста.

### Гибридная архитектура (рекомендация для будущих итераций)

Ничто не мешает комбинировать оба фреймворка:

- **MassTransit** остаётся на границе сервисов: приём команд от Gateway, отправка результатов.
- **MAF** запускается **внутри** воркера как движок для сложной обработки PDF:

```csharp
public async Task Consume(ConsumeContext<PdfProcessingCommand> context)
{
    var agent = new DocumentProcessingAgent(); // MAF Agent
    var result = await agent.ProcessAsync(
        context.Message.DocumentId,
        context.Message.FilePath,
        context.CancellationToken
    );
    // сохранить результат через репозиторий
}
```

Это даёт:
- Гарантированную доставку и ретраи от MassTransit.
- Чекпоинты, AI‑агентов и расширяемость от MAF.

### Итог

| Аспект | Решение в текущем MVP | План на production |
|--------|------------------------|---------------------|
| Межсервисная коммуникация | MassTransit | MassTransit (оставить) |
| Оркестрация шагов обработки | Ручная (один consumer) | MAF (чекпоинты + AI‑агенты) |
| Retry/DLQ | MassTransit | MassTransit (базовый) + MAF checkpoint recovery |

**Кратко:** MassTransit – правильный выбор для MVP. MAF будет добавлен, когда понадобятся **AI‑агенты и долгоживущие пайплайны** (search + retrieve + rerank + generate). Сейчас система готова к такому расширению – достаточно заменить внутреннюю логику Consumer на вызов MAF‑агента.

## Выбор OCR-решения

Для распознавания текста в отсканированных PDF используется **Tesseract OCR** (локальный, запускается через `pdftoppm` + `tesseract`).

Почему не другие варианты:

- **Azure AI Document Intelligence** — аккаунт долго верифицируется, не дождался ключа.
- **OCRBase (ocrbase.dev)** — API крайне медленный, запросы зависали на минуты.
- **OCR.Space** — аналогично, высокая задержка, нестабильная работа.

Tesseract работает локально, без интернета, бесплатно, не требует API-ключей. Качество распознавания ниже облачных аналогов, но для MVP достаточно.

## Известные ограничения и отложенные улучшения

- **Последовательная обработка страниц OCR.** Tesseract обрабатывает страницы в цикле `foreach`. Параллелизация через `Parallel.ForEachAsync` отложена на MVP — сейчас `prefetch=1` и один consumer, узкое место не критично. Для production добавить `SemaphoreSlim` (ограничение на количество одновременных процессов tesseract).
- **Чекпоинты на каждый шаг обработки.** Если Worker упал после PdfPig, но до сохранения в БД, ретрай начинается с нуля. Возможное решение: MassTransit Sagas.
- **Отдельный обработчик DLQ.** Сейчас ошибки идут в `_error` очередь MassTransit без отдельного сервису‑обработчика.

## Сводка рабочего процесса (комментарии в коде)

* **Загрузка (`POST /upload`)**
  1. Проверка файла (PDF, ≤ 4 МБ).
  2. Сохранение файла через `IFileStorage`.
  3. Создание `DocumentDto` со статусом `Uploaded`.
  4. Создание `OutboxMessage` с `PdfProcessingCommand`.
  5. Сохранение обеих записей в одной транзакции.

* **Outbox Publisher** (`BackgroundService`)
  - Периодически сканирует таблицу `outbox` и публикует непроцессированные сообщения в RabbitMQ через MassTransit.
  - После успешной публикации помечает запись как обработанную.
