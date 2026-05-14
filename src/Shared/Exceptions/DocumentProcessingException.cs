namespace Shared.Exceptions;

/// <summary>
/// Exception thrown when document processing fails in the PdfProcessingConsumer.
///
/// Using a typed exception instead of a bare new Exception() enables:
///   - Catch filters in MassTransit retry configuration
///   - Structured logging with exception type context
///   - Clearer error diagnosis in dead-letter queues
/// </summary>
public class DocumentProcessingException : Exception
{
    /// <summary>
    /// The document ID that failed processing.
    /// </summary>
    public Guid DocumentId { get; }

    /// <summary>
    /// Creates a new DocumentProcessingException with the specified details.
    /// </summary>
    /// <param name="documentId">The document that failed processing.</param>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="inner">Optional inner exception for chaining.</param>
    public DocumentProcessingException(Guid documentId, string message, Exception? inner = null)
        : base($"Processing failed for document {documentId}: {message}", inner)
    {
        DocumentId = documentId;
    }
}