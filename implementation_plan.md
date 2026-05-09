# Implementation Plan

[Overview]
Implement a two-service PDF processing system (API Gateway + Background Worker) with RabbitMQ messaging, transactional outbox, PdfPig/OCR text extraction, and Docker-based infrastructure.

This plan implements the full MVP (Days 1-5) as defined in `docs/roadmap.md`, following the architecture decisions documented in `docs/adr/ADR001_PDF_Processing_Architecture.md`. The system consists of three .NET 8 projects (ApiGateway, Worker, Shared) orchestrated via Docker Compose with PostgreSQL, RabbitMQ, and MinIO. The implementation uses MassTransit instead of the proposed MAF (Microsoft Agent Framework) since MassTransit is production-ready, well-documented, and natively supports transactional outbox, retry with delayed redelivery, and saga patterns—providing the same architectural benefits without preview-technology risk. The codebase currently contains only an empty solution file and documentation; all source code, infrastructure configuration, and database schemas must be created from scratch.

[Types]
Define all shared DTOs, enums, and interfaces in the Shared project with exact field specifications for cross-service contracts.

- **DocumentStatus enum** (string-backed): `uploaded`, `processing`, `completed`, `failed`
- **DocumentDto record**: `Id (Guid)`, `Filename (string)`, `Status (DocumentStatus)`, `CreatedAt (DateTime)`, `StartedAt (DateTime?)`, `CompletedAt (DateTime?)`, `ErrorMessage (string?)`, `ExtractedText (string?)`
- **DocumentListItem record**: `Id (Guid)`, `Filename (string)`, `Status (DocumentStatus)`, `CreatedAt (DateTime)`
- **PdfProcessingCommand record** (MassTransit message contract): `DocumentId (Guid)`, `FilePath (string)`, `MessageId (Guid)`, `RetryCount (int)`
- **IFileStorage interface**: `SaveAsync(Stream content, string fileName) -> string filePath`, `GetAsync(string filePath) -> Stream`, `DeleteAsync(string filePath)`
- **IOCRService interface**: `ExtractTextAsync(byte[] pdfContent) -> string?`
- **IDocumentRepository interface**: `CreateAsync(DocumentDto)`, `GetByIdAsync(Guid) -> DocumentDto?`, `GetAllAsync() -> List<DocumentDto>`, `UpdateStatusAsync(Guid, DocumentStatus, ...)`, `GetOutboxPendingAsync() -> List<OutboxMessage>`, `MarkOutboxProcessedAsync(Guid)`
- **OutboxMessage record**: `Id (Guid)`, `DocumentId (Guid)`, `MessagePayload (string)`, `CreatedAt (DateTime)`, `ProcessedAt (DateTime?)`
- **ProcessedMessage record**: `MessageId (Guid)`, `DocumentId (Guid)`, `ProcessedAt (DateTime)`
- **UploadResponse record**: `DocumentId (Guid)`, `Status (string)`, `Message (string)`

[Files]
Create 3 new .NET 8 projects and supporting infrastructure files; modify the solution file to reference all projects.

**New files to create:**

1. `src/Shared/Shared.csproj` — Class library for shared contracts
2. `src/Shared/Models/DocumentStatus.cs` — Status enum
3. `src/Shared/Models/DocumentDto.cs` — Document data transfer object
4. `src/Shared/Models/DocumentListItem.cs` — List item DTO
5. `src/Shared/Models/OutboxMessage.cs` — Outbox record
6. `src/Shared/Models/ProcessedMessage.cs` — Idempotency record
7. `src/Shared/Models/PdfProcessingCommand.cs` — MassTransit message contract
8. `src/Shared/Models/UploadResponse.cs` — Upload response DTO
9. `src/Shared/Interfaces/IFileStorage.cs` — File storage abstraction
10. `src/Shared/Interfaces/IOCRService.cs` — OCR service abstraction
11. `src/Shared/Interfaces/IDocumentRepository.cs` — Repository abstraction

12. `src/ApiGateway/ApiGateway.csproj` — ASP.NET Core Web API project
13. `src/ApiGateway/Program.cs` — Host builder with DI, MassTransit, DB setup
14. `src/ApiGateway/appsettings.json` — Configuration
15. `src/ApiGateway/appsettings.Development.json` — Dev overrides
16. `src/ApiGateway/Controllers/DocumentsController.cs` — REST endpoints
17. `src/ApiGateway/Services/DocumentService.cs` — Business logic
18. `src/ApiGateway/BackgroundServices/OutboxPublisher.cs` — Background outbox → queue publisher
19. `src/ApiGateway/Data/DocumentRepository.cs` — EF Core DbContext + repository implementation
20. `src/ApiGateway/Data/AppDbContext.cs` — EF Core DbContext
21. `src/ApiGateway/Storage/LocalFileStorage.cs` — Local filesystem implementation of IFileStorage
22. `src/ApiGateway/Storage/MinioFileStorage.cs` — MinIO/S3 implementation of IFileStorage
23. `src/ApiGateway/Extensions/ServiceCollectionExtensions.cs` — DI registration helpers

24. `src/Worker/Worker.csproj` — Console app project
25. `src/Worker/Program.cs` — Host builder, MassTransit consumer registration
26. `src/Worker/appsettings.json` — Configuration
27. `src/Worker/appsettings.Development.json` — Dev overrides
28. `src/Worker/Consumers/PdfProcessingConsumer.cs` — MassTransit IConsumer
29. `src/Worker/Services/PdfTextExtractor.cs` — PdfPig extraction logic
30. `src/Worker/Services/AzureOcrService.cs` — Azure AI Document Intelligence OCR
31. `src/Worker/Services/DocumentProcessingService.cs` — Orchestration: download → extract → save
32. `src/Worker/Data/DocumentRepository.cs` — Repository implementation (Worker-side)
33. `src/Worker/Data/AppDbContext.cs` — EF Core DbContext
34. `src/Worker/Storage/LocalFileStorage.cs` — Local file storage for Worker
35. `src/Worker/Storage/MinioFileStorage.cs` — MinIO storage for Worker

36. `src/Worker/Dockerfile` — Worker container
37. `src/ApiGateway/Dockerfile` — API Gateway container

38. `docker-compose.yml` — Orchestration file at repo root
39. `docker-compose.override.yml` — Dev overrides with volume mounts
40. `.env.example` — Environment variable template

41. `db/init.sql` — Database initialization script (idempotent CREATE TABLE IF NOT EXISTS)

**Files to modify:**
- `agentic-pdf-workflow.sln` — Add all 3 projects

**Files to delete/move:** None

[Functions]
Implement all application logic as methods across the service layer, controllers, consumers, and background services.

**ApiGateway functions:**

- `DocumentsController.Upload(IFormFile)` — POST /upload — Validate file, generate DocumentId, save file via IFileStorage, create document record + outbox entry in transaction, return 202 Accepted with DocumentId
- `DocumentsController.GetList()` — GET /list — Query all documents, return list of DocumentListItem
- `DocumentsController.GetText(Guid id)` — GET /text/{id} — Check status: completed→200+text, processing→202, failed→409, not found→404
- `DocumentService.CreateDocumentAsync(Stream, string filename)` — Core upload business logic with transactional outbox
- `DocumentService.GetAllDocumentsAsync()` — List retrieval
- `DocumentService.GetDocumentTextAsync(Guid id)` — Text retrieval with status check
- `OutboxPublisher.ExecuteAsync(CancellationToken)` — Background loop: poll outbox every 5s, publish pending messages to MassTransit/RabbitMQ, mark processed
- `DocumentRepository.CreateAsync(DocumentDto, OutboxMessage)` — Atomic insert in transaction
- `DocumentRepository.GetOutboxPendingAsync()` — SELECT WHERE processed_at IS NULL
- `DocumentRepository.MarkOutboxProcessedAsync(Guid id)` — UPDATE processed_at = NOW()
- `LocalFileStorage.SaveAsync(Stream, string)` — Write to configured local path
- `LocalFileStorage.GetAsync(string)` — Read file from disk
- `MinioFileStorage.SaveAsync(Stream, string)` — PutObjectAsync
- `MinioFileStorage.GetAsync(string)` — GetObjectAsync

**Worker functions:**

- `PdfProcessingConsumer.Consume(ConsumeContext<PdfProcessingCommand>)` — Main consumer: 1) idempotency check on processed_messages, 2) optimistic lock (UPDATE WHERE status=uploaded), 3) download PDF, 4) extract text, 5) save result + processed_message in transaction, 6) ACK
- `DocumentProcessingService.ProcessDocumentAsync(Guid docId, string filePath)` — Orchestrates download → extract → save flow
- `PdfTextExtractor.ExtractTextAsync(byte[] pdfContent)` — Use PdfPig to extract text; if empty/null, call IOCRService
- `AzureOcrService.ExtractTextAsync(byte[] pdfContent)` — Use Azure.AI.DocumentIntelligence SDK; handle F0 limits (≤4MB, first 2 pages)
- `DocumentRepository.UpdateStatusAndTextAsync(Guid id, DocumentStatus status, string? text, string? error)` — Atomic update with optimistic concurrency
- `DocumentRepository.GetByIdAsync(Guid id)` — Read document metadata
- `DocumentRepository.MarkMessageProcessedAsync(Guid messageId, Guid documentId)` — Insert into processed_messages

[Classes]
Define 3 new classes with specific responsibilities; the solution currently has zero projects and zero classes.

**New classes (by project):**

**Shared (class library - no DI, pure contracts):**
- `DocumentStatus` — string enum: Uploaded, Processing, Completed, Failed
- `DocumentDto` — record with all document fields
- `DocumentListItem` — record with list-view fields
- `OutboxMessage` — record
- `ProcessedMessage` — record
- `PdfProcessingCommand` — record implementing MassTransit CorrelatedBy<Guid>
- `UploadResponse` — record

**ApiGateway (ASP.NET Core):**
- `DocumentsController : ControllerBase` — REST API with 3 endpoints
- `DocumentService` — Business logic facade
- `OutboxPublisher : BackgroundService` — Recurring outbox processor
- `AppDbContext : DbContext` — EF Core context with DbSet<DocumentDto>, DbSet<OutboxMessage>, DbSet<ProcessedMessage>
- `DocumentRepository : IDocumentRepository` — EF Core repository
- `LocalFileStorage : IFileStorage` — Local filesystem storage
- `MinioFileStorage : IFileStorage` — MinIO SDK storage

**Worker (Console + IHostedService via MassTransit):**
- `PdfProcessingConsumer : IConsumer<PdfProcessingCommand>` — MassTransit consumer
- `DocumentProcessingService` — Core processing orchestration
- `PdfTextExtractor` — PdfPig text extraction with OCR fallback
- `AzureOcrService : IOCRService` — Azure AI Document Intelligence client
- `AppDbContext : DbContext` — EF Core context (Worker-side, separate project)
- `DocumentRepository : IDocumentRepository` — EF Core repository
- `LocalFileStorage : IFileStorage` — Local filesystem (Worker-side)
- `MinioFileStorage : IFileStorage` — MinIO SDK (Worker-side)

[Dependencies]
Add 8 NuGet packages across projects and 3 Docker images for infrastructure.

**NuGet packages (all latest stable for .NET 8):**
- `MassTransit` (all projects) — Message bus abstraction with RabbitMQ transport
- `MassTransit.RabbitMQ` (all projects) — RabbitMQ transport for MassTransit
- `MassTransit.EntityFrameworkCore` (ApiGateway, Worker) — EF Core integration for outbox
- `Microsoft.EntityFrameworkCore` (ApiGateway, Worker) — ORM for PostgreSQL
- `Npgsql.EntityFrameworkCore.PostgreSQL` (ApiGateway, Worker) — PostgreSQL provider for EF Core
- `UglyToad.PdfPig` (Worker only) — PDF text extraction
- `Azure.AI.DocumentIntelligence` (Worker only) — Azure OCR SDK
- `Minio` (ApiGateway, Worker) — MinIO/S3 SDK for object storage

**Docker images:**
- `postgres:16-alpine` — Database
- `rabbitmq:4-management` — Message broker (management plugin included)
- `minio/minio` — Object storage (S3-compatible)

[Testing]
Implement Docker Compose-based integration testing manually (no unit test framework) by verifying the full cycle: upload → queue → process → retrieve.

Test scenarios to execute manually via curl/Postman:
1. Upload a text-based PDF → verify status becomes `completed` → GET /text/{id} returns extracted text
2. Upload an image-based PDF → verify OCR fallback triggers → status becomes `completed`
3. Upload a corrupt file → verify status becomes `failed`
4. GET /list returns all uploaded documents
5. GET /text/{id} on non-existent ID returns 404
6. Verify outbox: check outbox table after upload, confirm publication
7. Verify idempotency: simulate duplicate message delivery, confirm no double processing

[Implementation Order]
Build from infrastructure up, following the 5-day roadmap sequence: Docker/DB foundation → Gateway → Worker core → Retry/OCR → Final integration.

1. **Infrastructure foundation** — Create `docker-compose.yml`, `db/init.sql`, `.env.example`; create all 3 .NET projects and add to solution; install all NuGet packages; create `Shared` project contracts (enums, DTOs, interfaces)
2. **Database layer** — Implement `AppDbContext` in both ApiGateway and Worker; implement `DocumentRepository` with all CRUD operations; run migrations/init.sql
3. **File storage abstraction** — Implement `IFileStorage` with `LocalFileStorage` and `MinioFileStorage` in both ApiGateway and Worker
4. **API Gateway upload flow** — Implement `DocumentsController.Upload`; implement `DocumentService` for upload logic with transactional outbox; create `OutboxPublisher` background service
5. **API Gateway read endpoints** — Implement `GET /list` and `GET /text/{id}`
6. **Worker consumer** — Implement `PdfProcessingConsumer` with MassTransit; implement `PdfTextExtractor` with PdfPig; implement `DocumentProcessingService` with idempotency check and optimistic locking
7. **Retry and error handling** — Configure MassTransit retry policies (immediate 3 retries, then move to error queue); implement status `failed` on unrecoverable errors
8. **Azure OCR fallback** — Implement `AzureOcrService`; integrate into `PdfTextExtractor` as fallback when PdfPig returns empty text
9. **Dockerize services** — Create Dockerfiles for ApiGateway and Worker; wire into docker-compose.yml; test full stack startup
10. **Integration verification** — Run full cycle tests with sample PDFs from `artifacts/` directory