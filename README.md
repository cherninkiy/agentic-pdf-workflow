# Система обработки PDF (MVP)

## Обзор

Этот репозиторий реализует минимальную систему обработки PDF‑документов в соответствии с архитектурным решением (ADR) и планом реализации. Система состоит из двух сервисов:

| Сервис | Ответственность |
|--------|-----------------|
| **ApiGateway** | HTTP‑API для загрузки PDF, получения списка документов и извлечённого текста. Реализует паттерн транзакционного outbox для надёжной доставки сообщений. |
| **Worker** | Потребитель сообщений `PdfProcessingCommand`, извлекает текст из PDF (PdfPig + опциональный Azure OCR), сохраняет результат и обновляет статус документа. |

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
