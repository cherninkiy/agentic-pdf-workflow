# Roadmap реализации: 5 дней (MVP) + 2 дня (Production Boost)

## Общая стратегия
- **Дни 1–5** – рабочий MVP со всеми core-фичами: загрузка, очередь, распознавание текста (PdfPig + Tesseract OCR fallback), статусная модель (INTEGER + lookup table), outbox, retry/DLQ, idempotency через MessageId, интеграционные тесты.
- **Дни 6–7** – production-улучшения: мониторинг (Prometheus + Grafana), health checks, graceful shutdown, чанкинг для Azure F0, базовый AI-роутинг (демо).

---

## День 1: Фундамент и инфраструктура ✅

**Цель:** Поднять базовые сервисы, создать скелеты API Gateway и Worker.

### Задачи
1. **Настройка Docker Compose** ✅
   - PostgreSQL (схема: `documents`, `outbox`)
   - RabbitMQ (создать exchanges/queues: `pdf_processing`, `retry`, `dlq`)
   - Контейнеры для Gateway и Worker (пока пустые).
   - Файловое хранилище: локальный Docker volume (общий том для Gateway и Worker).
2. **Создать проекты .NET 8** ✅
   - `ApiGateway` (ASP.NET Core Web API)
   - `Worker` (Console App + `IHostedService`)
   - `Shared` (общие DTO, интерфейсы для `IFileStorage`, `IOCRService`).
3. **Реализовать абстракции** ✅
   - `IFileStorage` с реализацией `MinioFileStorage` (через Minio SDK) и `LocalFileStorage` (для офлайн-разработки).
   - `IRepository` для работы с PostgreSQL (Dapper или EF Core).
4. **Инициализация БД** ✅
   - Таблица `documents`: `id`, `filename`, `status`, `created_at`, `started_at`, `completed_at`, `error_message`, `extracted_text` (text).
   - Таблица `outbox`: `id`, `document_id`, `message_payload` (json), `created_at`, `processed_at`.
   - Таблица `processed_messages` (для idempotency): `message_id`, `document_id`, `processed_at`.

**Результат:** Docker Compose поднимает все сервисы. Gateway и Worker запускаются и подключаются к БД, RabbitMQ, MinIO.

---

## День 2: API Gateway + Outbox Publisher ✅

**Цель:** Реализовать эндпоинты и гарантированную публикацию команд.

### Задачи
1. **API Controller** ✅
   - `POST /upload` – принимает PDF, генерирует `document_id`, сохраняет файл через `IFileStorage`, записывает метаданные в БД (status='uploaded') и **одновременно** добавляет запись в `outbox` с JSON‑сообщением (DocumentId, FilePath, MessageId).
   - Использовать транзакцию (Unit of Work).
2. **Фоновый Outbox Publisher** ✅
   - Сканирует `outbox` каждые 5 секунд, берёт непроцессированные записи.
   - Публикует сообщение в RabbitMQ (`pdf_processing` exchange/queue).
   - После успешной публикации помечает запись `processed_at = NOW()`.
   - Использовать MassTransit или RabbitMQ.Client с confirm mode.
3. **GET – эндпоинты** ✅
   - `GET /list` – возвращает список документов (id, filename, status, created_at).
   - `GET /text/{id}` – возвращает текст, если статус `completed`, иначе `202 Accepted` (если `processing`) или `409 Conflict` (если `failed`).

**Результат:** Можно загрузить PDF, получить `202`, и в БД появляется запись в outbox. Фоновый публикатор отправляет сообщение в очередь.

---

## День 3: Worker – базовая обработка (PdfPig + статусы) ✅

**Цель:** Worker потребляет сообщения, обновляет статусы, извлекает текст через PdfPig.

### Задачи
1. **Worker Consumer** ✅
   - Подписка на очередь `pdf_processing` (prefetch=1).
   - Получив сообщение, извлекает `DocumentId`, `FilePath`, `MessageId`.
2. **Idempotency check** ✅
   - Проверяет таблицу `processed_messages`. Если MessageId уже обработан – сразу ACK, игнорируем.
3. **Взятие задачи (конкурентно‑безопасное)** ✅
   - `UPDATE documents SET status='processing', started_at=NOW() WHERE id=@id AND status='uploaded'`. Если затронута 0 строк – значит, уже кто‑то другой обрабатывает → ACK и выход.
4. **Скачивание PDF** из MinIO/Local по `FilePath`.
5. **Извлечение текста через PdfPig** ✅
   - Если текст найден – сохраняем в `extracted_text` и обновляем статус на `completed`.
6. **Запись в processed_messages** (в той же транзакции, что и обновление документа) – вставляем `message_id`.
7. **Подтверждение (ACK)** сообщения в RabbitMQ.

**Обработка ошибок (временные):**  
- При любом исключении – вызываем `nack(requeue=false)`, сообщение уходит в retry exchange (пока без задержки, просто в DLQ).  
- *Позже (день 4) подключим retry с задержкой.*

**Результат:** Worker успешно обрабатывает текстовые PDF, статус меняется с `uploaded` → `processing` → `completed`. Ошибки пока валятся в DLQ.

---

## День 4: Retry механизм + Tesseract OCR Fallback ✅

**Цель:** Добавить отказоустойчивость (ретраи с задержкой) и OCR для скан-копий.

### Задачи
1. **Настроить Retry механизм в MassTransit** ✅
   - Использовать `.UseMessageRetry(r => r.Intervals(5s, 30s, 60s))`.
   - После исчерпания ретраев – отправка в DLQ (`_error` очередь MassTransit).
2. **Tesseract OCR интеграция** ✅
   - Реализовать `IOCRService` с методом `ExtractTextAsync(byte[] pdfContent)`.
   - Использовать `pdftoppm` (poppler-utils) для конвертации PDF в PNG, затем `tesseract` (eng+rus).
   - В Worker: если PdfPig вернул пустую строку, вызываем `TesseractOcrService`. Результат сохраняем в БД.
3. **Лимиты OCR** ✅
   - На входе проверять размер PDF ≤ 4 MB, иначе возвращать ошибку (статус `failed`).
   - *Чанкинг отложим на день 7.*
4. **Добавить статус `failed`** ✅
   - При исчерпании ретраев или фатальной ошибке (например, файл слишком большой) – обновить статус документа на `failed` с сохранением `error_message`.

**Результат:** Система выдерживает временные сбои, автоматически повторяет обработку, а скан-копии проходят через Tesseract OCR.

---

## День 5: Интеграция MAF workflow + чтение DLQ ⏳

**Цель:** Завершить MVP: обернуть логику Worker в Microsoft Agent Framework (MAF) и добавить простой обработчик DLQ.

### Задачи
1. **Реализовать Worker через MAF** ✅ (заменён на MassTransit)
   - ~~Создать `Agent` с шагами: `DownloadDocument`, `UpdateStatusProcessing`, `ExtractTextStep` (содержит PdfPig → fallback OCR), `SaveTextAndComplete`.~~
   - ~~Использовать встроенный в MAF механизм ретраев и checkpoint.~~
   - **Фактически:** MassTransit заменил MAF — retry, DLQ, graceful shutdown, ACK/nack реализованы через MassTransit. Чекпоинты и декларативные workflow — отложены (см. `README.md` раздел MAF vs MassTransit).
2. **Обработчик DLQ** ✅ (частично — через MassTransit `_error`)
   - MassTransit автоматически направляет неуспешные сообщения в `_error` очередь.
   - Отдельный сервис-обработчик DLQ пока не реализован (записано в README как отложенное улучшение).
3. **Написать интеграционные тесты** ✅
   - Docker Compose + Testcontainers для проверки полного цикла (4 теста).

**Результат:** Полноценный MVP, готовый к демонстрации. Все основные требования выполнены.

---

## День 6: Production Boost – Мониторинг и надёжность ✅

**Цель:** Добавить наблюдаемость и усилить стабильность.

### Задачи
1. **Prometheus + Grafana** ✅
   - Добавить метрики: `document_upload_total` (counter), `document_processing_duration_seconds` (histogram p50/p95).
   - Экспорт метрик через prometheus-net: `/metrics` на Gateway (5000) + MetricServer на Worker (5091).
   - Развернуть Grafana с преднастроенным дашбордом `grafana/dashboards/pdf-processing.json`.
2. **Health Checks** ✅
   - `/health/ready` – проверка БД (EF Core) и RabbitMQ (rabbitmq-diagnostics).
   - `/health/live` – проверка процесса.
   - Добавлен кастомный `RabbitMqHealthCheck` в `src/ApiGateway/HealthChecks/`.
3. **Graceful Shutdown** ✅ (реализовано через MassTransit)
   - MassTransit сам обрабатывает SIGTERM и завершает текущие сообщения.

**Результат:** Систему можно мониторить, алертить, безопасно останавливать.

---

## День 7: Production Boost – Оптимизация OCR + AI-роутинг (демо)

**Цель:** Показать расширяемость и production-готовность.

### Задачи
1. **Параллелизация OCR страниц** (отложено — записано в README)
   - Заменить `foreach` на `Parallel.ForEachAsync` + `SemaphoreSlim` для параллельной обработки страниц Tesseract.
2. **Базовый AI-роутинг**
   - Создать новый agent-подписчик на событие `document.completed` (можно через отдельную очередь `routing_queue`).
   - Использовать простую LLM (например, через Semantic Kernel с локальной моделью или просто BERT-классификатор из ML.NET) для категоризации текста на `invoice`, `contract`, `report`.
   - Результат сохранять в отдельное поле `document_category`.
   - Это не является требованием, но показывает расширяемость.
3. **Финальное тестирование и документация**
   - Подготовить `README.md` с инструкцией по запуску, переменными окружения, лимитами.
   - Видео-демо (по желанию).

**Результат:** Готовый production-ready прототип, который можно показывать как промышленный образец.

---

## Итоговая таблица

| День | Фокус | Ключевые артефакты | Статус |
|------|-------|---------------------|--------|
| 1 | Инфраструктура, скелеты | Docker Compose, проекты .NET, абстракции, таблицы БД | ✅ |
| 2 | API Gateway + Outbox | Эндпоинты `/upload`, `/list`, `/text/{id}`, фоновый публикатор | ✅ |
| 3 | Worker (PdfPig, статусы, идемпотентность) | Базовая обработка текста, статусная модель, ACK/nack | ✅ |
| 4 | Retry + Tesseract OCR | Retry механизм с задержками, интеграция Tesseract, статус `failed` | ✅ |
| 5 | MAF workflow + DLQ обработка | Законченный MVP, MassTransit (замена MAF), обработчик DLQ (частично) | ⏳ |
| 6 | Мониторинг + надёжность | Prometheus, Grafana, health checks, graceful shutdown | ✅ |
| 7 | Параллелизация OCR + AI-роутинг | Ускорение OCR, демонстрация расширяемости | 🔄 (отложено) |

**Рекомендация:** Если MAF в preview вызывает проблемы, заменить на MassTransit Sagas – это надёжнее. Но roadmap оставляет технологический выбор за разработчиком.