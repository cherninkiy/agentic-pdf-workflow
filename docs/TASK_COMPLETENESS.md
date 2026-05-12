# Отчёт о полноте реализации задачи

> Этот документ сопоставляет исходные требования (docs/TASK.md) с реализованным решением.
> Отражает финальное состояние MVP (май 2026).

## Функциональные требования

### API Gateway — REST API

| Требование | Статус | Детали |
|------------|--------|--------|
| POST /upload — загрузка PDF | ✅ | Лимит 4 МБ, проверка расширения .pdf, возвращает 202 Accepted |
| GET /list — список PDF | ✅ | Метаданные: id, filename, status, created_at |
| GET /text/{id} — текст документа | ✅ | 200 (готово), 202 (в обработке), 409 (ошибка), 404 (не найден) |

### Background Worker

| Требование | Статус | Детали |
|------------|--------|--------|
| Получать сообщения из RabbitMQ | ✅ | MassTransit consumer на очереди `pdf_processing` |
| Извлекать текст из PDF | ✅ | PdfPig для текстовых PDF + Tesseract OCR для сканов |
| Сохранять результат в PostgreSQL | ✅ | Атомарный UPDATE с оптимистичной блокировкой |
| Обновлять статус обработки | ✅ | INTEGER: Uploaded(0)/Processing(1)/Completed(2)/Failed(3) |

### Вне scope (как указано в ТЗ)

| Требование | Статус |
|------------|--------|
| Frontend | ❌ Не реализовано (не требуется) |
| Авторизация | ❌ Не реализовано (не требуется) |
| Сложная инфраструктура | ❌ Не реализовано (не требуется) |

## Фокус архитектуры

### Backend

| Аспект | Реализация |
|--------|------------|
| Язык | C# 12 / .NET 8 |
| API фреймворк | ASP.NET Core 8 Web API |
| Фоновая обработка | .NET Generic Host + MassTransit consumer |
| Тестирование | 19 тестов: 8 unit (ApiGateway) + 7 unit (Worker) + 4 integration (Testcontainers) |
| Структура | /src (ApiGateway, Worker, Shared) + /tests (3 проекта) |

### Очереди сообщений (RabbitMQ)

| Аспект | Реализация |
|--------|-----------|
| Брокер | RabbitMQ 4.x через MassTransit |
| Очередь | `pdf_processing`, prefetch=1 |
| Ретраи | 3 попытки: 5s → 30s → 60s (MassTransit `UseMessageRetry`) |
| Dead Letter Queue | Встроенная `_error` очередь MassTransit |
| Идемпотентность | Таблица `processed_messages` — дедупликация по MessageId |
| Outbox | Транзакционный outbox: документ + outbox в одной транзакции |
| Публикатор | Background service, опрос каждые 5s, публикация через MassTransit |

### База данных (PostgreSQL)

| Аспект | Реализация |
|--------|-----------|
| ORM | Entity Framework Core 8 |
| Таблицы | `documents`, `outbox`, `processed_messages`, `document_statuses` |
| Статусы | INTEGER + lookup table (0=Uploaded, 1=Processing, 2=Completed, 3=Failed) |
| Оптимистичная блокировка | Атомарный `UPDATE documents SET status={int} WHERE id=@id AND status={int}` |
| Инициализация схемы | `db/init.sql` для production, `EnsureCreated()` для dev |
| Таймстемпы | `NOW()` от БД (не от часов приложения) |

### OCR Pipeline

| Аспект | Реализация |
|--------|-----------|
| Текстовые PDF | PdfPig (прямое извлечение текста) |
| Сканированные PDF | Tesseract OCR (pdftoppm → PNG → tesseract eng+rus) |
| Параллельная обработка | `Parallel.ForEachAsync` + SemaphoreSlim (ProcessorCount / 2) |
| Очистка | Временные файлы удаляются в `finally` |

## Мониторинг и надёжность (День 6)

| Аспект | Реализация |
|--------|-----------|
| Health checks | `/health/live` (только процесс), `/health/ready` (Postgres + RabbitMQ) |
| Prometheus метрики | `document_upload_total` (counter), `document_processing_duration_seconds` (histogram) |
| Экспорт метрик | Gateway: `/metrics` порт 5000, Worker: MetricServer порт 5091 |
| Grafana | Дашборд в `grafana/dashboards/pdf-processing.json` |
| Graceful shutdown | MassTransit обрабатывает SIGTERM, завершает текущие сообщения |

## Покрытие тестами

| Набор тестов | Тестов | Тип |
|--------------|--------|-----|
| ApiGateway.UnitTests | 8 | Unit + smoke (in-memory DB) |
| Worker.UnitTests | 7 | Unit (PdfPig, Tesseract OCR) |
| IntegrationTests | 4 | Integration (Testcontainers: PostgreSQL + RabbitMQ) |
| **Итого** | **19** | |

## CI Pipeline

- Устанавливает Tesseract OCR + poppler-utils на runner
- Запускает все 3 тестовых проекта
- Собирает Docker образы обоих сервисов
- Запускается при push/PR в `main` и `dev`