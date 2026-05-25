# Production Readiness Assessment

> Оценка готовности текущей системы к эксплуатации в production-среде.

## Что готово для production

### Инфраструктура
- **Docker Compose** — все сервисы (PostgreSQL, RabbitMQ, Gateway, Worker) описаны с healthcheck и depend_on
- **Volume** — общий том `shared_storage` для Gateway и Worker
- **Инициализация БД** — `db/init.sql` создаёт схему при первом запуске

### Надёжность
- **Атомарные статусы** — `UPDATE ... WHERE status = {int}` предотвращает race condition
- **Идемпотентность** — `processed_messages` таблица дедуплицирует сообщения по MessageId
- **Outbox pattern** — гарантирует доставку: запись сначала в БД, потом публикация
- **Ретраи** — MassTransit автоматически повторяет 5s → 30s → 60s

### Мониторинг
- **Health checks** — `/health/live` (liveness) и `/health/ready` (readiness для k8s)
- **Prometheus метрики** — `document_upload_total`, `document_processing_duration_seconds`
- **Grafana** — преднастроенный дашборд: загрузки/мин, p50/p95 времени обработки

### Тестирование
- **37 тестов** — 12 ApiGateway unit + 21 Worker unit + 4 integration (Testcontainers)
- OutboxPublisher tests, retry→DLQ tests, concurrency/race condition tests
- **CI pipeline** — GitHub Actions с Tesseract OCR, 3 тестовых проекта, Docker build

### Graceful Shutdown
- **MassTransit** — обрабатывает SIGTERM, завершает текущие сообщения
- **Worker** — `MetricsHostedService` (IHostedService) корректно останавливает metric-сервер при SIGTERM

## Что требуется доработать

### Критично для production

| Задача | Важность | Описание |
|--------|----------|----------|
| **Отдельный обработчик DLQ** | Высокая | Сейчас ошибки уходят в `_error` очередь MassTransit. Нужен сервис, который читает DLQ, логирует и при необходимости репроцессит. |
| **Чекпоинты обработки** | 🟢 Реализовано | MAF (DocumentProcessingAgent) с чекпоинтами в PostgreSQL. Каждый шаг сохраняет состояние — при падении Worker продолжает с последнего чекпоинта. |
| **Rate limiting** | Средняя | API Gateway не ограничивает частоту запросов. Для production нужен `AspNetCoreRateLimit` или аналогичный middleware. |

### Мониторинг и наблюдаемость

| Задача | Важность | Описание |
|--------|----------|----------|
| **Структурированное логирование** | 🟢 Реализовано | Serilog + CompactJsonFormatter. Все логи в JSON-формате, готовы для Loki/Elasticsearch. |
| **OpenTelemetry трассировка** | Средняя | MassTransit + EF Core не имеют distributed tracing. Для отладки задержек нужен OpenTelemetry с Jaeger/Zipkin. |
| **Алерты** | Средняя | Prometheus есть, но нет alerting rules и Alertmanager для оповещений. |

### Безопасность

| Задача | Важность | Описание |
|--------|----------|----------|
| **JWT авторизация** | 🟢 Реализовано | JWT Bearer аутентификация. Dev-эндпоинт `/auth/token`, production через внешний IDP. |
| **HTTPS/TLS** | Средняя | Все эндпоинты работают по HTTP. В production — reverse proxy (nginx/traefik) с TLS. |
| **Секреты** | Средняя | .env файл с токенами в репозитории (GITHUB_TOKEN). В production — secrets manager/vault. |

### Производительность

| Задача | Важность | Описание |
|--------|----------|----------|
| **Пул воркеров** | Средняя | Сейчас 1 consumer с prefetch=1. Для масштабирования — несколько инстансов Worker. |
| **Кэширование** | Низкая | GET /list не кэшируется. Для частых запросов — Redis + output cache. |
| **Пагинация** | Низкая | GET /list возвращает все документы. Для тысяч записей нужна пагинация. |

### Инфраструктура

| Задача | Важность | Описание |
|--------|----------|----------|
| **Kubernetes манифесты** | Средняя | Сейчас всё в Docker Compose. Для k8s — Deployment, Service, ConfigMap. |
| **Даунтайм-деплой** | Низкая | docker compose stop + up приводит к потере сообщений в очереди. Для zero-downtime — rolling update. |

## Итоговая оценка

| Критерий | Оценка | Комментарий |
|----------|--------|-------------|
| Масштабирование | 🟡 Средняя | 1 Worker, 1 Gateway. Горизонтальное масштабирование возможно, но не тестировалось |
| Устойчивость к сбоям | 🟢 Высокая | Ретраи, DLQ, идемпотентность, атомарные статусы |
| Наблюдаемость | 🟢 Высокая | Prometheus + Grafana + health checks |
| Безопасность | 🟡 Средняя | JWT авторизация реализована. HTTPS и secrets management — в плане |
| Тестирование | 🟢 Высокая | 37 тестов (12+21+4), CI пайплайн |
| Документация | 🟢 Высокая | ADR, roadmap, README, task completeness, production readiness |
