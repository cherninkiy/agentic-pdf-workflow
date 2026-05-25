using Shared.Models;

namespace Shared.Interfaces;

/// <summary>
/// Base document repository interface with common operations.
/// Implemented by both ApiGateway and Worker repositories.
/// </summary>
public interface IDocumentRepository
{
    Task<DocumentDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<DocumentDto>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<bool> TryUpdateStatusAsync(Guid id, DocumentStatus fromStatus, DocumentStatus toStatus, string? errorMessage = null, CancellationToken cancellationToken = default);
    Task UpdateTextAsync(Guid id, string? extractedText, DocumentStatus status, CancellationToken cancellationToken = default);
    Task MarkMessageProcessedAsync(Guid messageId, Guid documentId, CancellationToken cancellationToken = default);
    Task<bool> IsMessageProcessedAsync(Guid messageId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outbox-specific repository interface.
/// Only implemented by ApiGateway repository.
/// </summary>
public interface IOutboxRepository
{
    Task CreateAsync(DocumentDto document, OutboxMessage outboxMessage, CancellationToken cancellationToken = default);
    Task<List<OutboxMessage>> GetOutboxPendingAsync(CancellationToken cancellationToken = default);
    Task MarkOutboxProcessedAsync(Guid id, CancellationToken cancellationToken = default);
}