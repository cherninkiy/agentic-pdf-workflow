# Система обработки PDF (MVP)

## Обзор

Этот репозиторий реализует минимальную систему обработки PDF‑документов в соответствии с архитектурным решением (ADR) и планом реализации. Система состоит из двух сервисов:

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
  /ApiGateway.UnitTests – Юнит‑ и smoke‑тесты API
  /Worker.UnitTests     – Юнит‑тесты воркера
/.github
  /workflows/ci.yml     – CI‑pipeline (сборка, тесты, Docker‑образы)
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

1. Восстановление и сборку всех проектов.
2. Запуск юнит‑тестов для обоих сервисов.
3. Сборку Docker‑образов `pdf-api-gateway` и `pdf-worker`.

Пайплайн запускается при каждом push/PR в ветку `main` и в feature‑ветки.

## MAF vs MassTransit — что используется вместо Microsoft Agent Framework

В ADR-001 планировалось использовать **Microsoft Agent Framework (MAF)** для оркестрации шагов обработки документа: `DownloadDocument → UpdateStatusProcessing → ExtractTextStep → SaveTextAndComplete`. Каждый шаг — независимая единица с собственным checkpoint.

На практике `MassTransit` взял на себя бóльшую часть того, что должен был дать MAF:

| Что планировалось через MAF | Кто реализовал |
|----------------------------|----------------|
| Ретри-механизм (задержки 5s → 30s → 60s) | MassTransit `.UseMessageRetry()` |
| Dead Letter Queue | MassTransit встроенная `_error` очередь |
| Graceful Shutdown | MassTransit сам обрабатывает SIGTERM |
| ACK/nack управление | MassTransit автоматически |
| Ограничение параллелизма (prefetch=1) | MassTransit `e.PrefetchCount` |

Что MAF бы дал дополнительно (и пока не реализовано):

- **Чекпоинты на каждый шаг** — при падении Worker после PdfPig, но до сохранения в БД, ретрай начинается с нуля (качка PDF + парсинг). MAF позволил бы продолжить с шага сохранения.
- **Декларативное расширение** — добавить новый шаг (NER, перевод, суммаризация) можно было бы простым добавлением Agent'а в workflow. Сейчас для этого нужен отдельный consumer или правка `DocumentProcessingService`.

**Вывод:** для MVP MassTransit достаточен. Чекпоинты станут актуальны, когда появится много шагов или дорогие внешние вызовы. MassTransit Sagas — возможная альтернатива MAF для production.

## Выбор OCR-решения

Для распознавания текста в отсканированных PDF используется **Tesseract OCR** (локальный, запускается через `pdftoppm` + `tesseract`).

Почему не другие варианты:

- **Azure AI Document Intelligence** — запросил доступ к API, аккаунт долго верифицируется, не дождался ключа.
- **OCRBase (ocrbase.dev)** — API крайне медленный, запросы зависали на минуты.
- **OCR.Space** — аналогично, высокая задержка, нестабильная работа.

Tesseract работает локально, без интернета, бесплатно, не требует API-ключей. Качество распознавания ниже облачных аналогов, но для MVP достаточно.

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
