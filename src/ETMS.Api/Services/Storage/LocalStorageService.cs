using ETMS.Api.Common.Settings;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace ETMS.Api.Services.Storage;

/// <summary>
/// Stores files on the local file system (a persistent Docker volume in containers). Every path is
/// resolved under the configured root, and anything that would escape it is rejected.
/// </summary>
public class LocalStorageService : IStorageService
{
    private const int CopyBufferSize = 81920;

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly string _rootPath;

    public LocalStorageService(IOptions<StorageSettings> settings, IHostEnvironment environment)
    {
        // Path.Combine returns the configured path unchanged when it is already absolute.
        _rootPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, settings.Value.LocalStoragePath));
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<(string StoragePath, long FileSize)> SaveFileAsync(Stream fileStream, string originalFileName, string contentType, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);

        var storagePath = $"{Guid.NewGuid():N}{Path.GetExtension(originalFileName).ToLowerInvariant()}";
        var finalPath = ResolvePath(storagePath);
        var tempPath = finalPath + ".tmp";

        try
        {
            long fileSize;
            await using (var target = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous))
            {
                await fileStream.CopyToAsync(target, CopyBufferSize, ct);
                fileSize = target.Length;
            }

            // Readers never observe a partially written file.
            File.Move(tempPath, finalPath);
            return (storagePath, fileSize);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task<(Stream FileStream, string ContentType, string FileName)> GetFileAsync(string storagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var fullPath = ResolvePath(storagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The stored file does not exist.", storagePath);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var contentType = ContentTypes.TryGetContentType(fullPath, out var type) ? type : "application/octet-stream";

        return Task.FromResult((stream, contentType, Path.GetFileName(fullPath)));
    }

    public Task DeleteFileAsync(string storagePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        File.Delete(ResolvePath(storagePath)); // No-op when the file does not exist.
        return Task.CompletedTask;
    }

    private string ResolvePath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("Storage path is required.", nameof(storagePath));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, storagePath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(_rootPath) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;

        // Path-traversal guard: "../x", absolute paths, etc. must not leave the storage root.
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException("Storage path resolves outside the storage root.", nameof(storagePath));
        }

        return fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: an orphaned .tmp file is harmless.
        }
    }
}
