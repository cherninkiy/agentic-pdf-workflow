using Shared.Interfaces;

namespace ApiGateway.Storage;

public class LocalFileStorage : IFileStorage
{
    private readonly string _basePath;

    public LocalFileStorage(string basePath)
    {
        // Ensure the base path exists. If creation fails (e.g., due to permission issues),
        // fall back to a temporary directory that is always writable.
        // Ensure the base path exists. If creation fails, log the error and rethrow to fail fast.
        _basePath = basePath;
        try
        {
            Directory.CreateDirectory(_basePath);
        }
        catch (Exception ex)
        {
            // Log the exception if a logger is available; otherwise, write to console.
            Console.Error.WriteLine($"Failed to create storage directory '{_basePath}': {ex.Message}");
            throw;
        }
    }

    public async Task<string> SaveAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_basePath, fileName);
        var directory = Path.GetDirectoryName(filePath);
        if (directory != null) Directory.CreateDirectory(directory);

        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(fileStream, 81920, cancellationToken);
        return filePath;
    }

    public async Task<Stream> GetAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
    }

    public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}