namespace ETMS.Api.Services.Storage;

/// <summary>Binary file storage used for task attachments (SDD §7).</summary>
public interface IStorageService
{
    /// <summary>
    /// Stores the stream under a server-generated name (<c>{guid}{extension}</c>). The original file name
    /// only contributes its extension. Returns the path relative to the storage root and the bytes written.
    /// </summary>
    Task<(string StoragePath, long FileSize)> SaveFileAsync(Stream fileStream, string originalFileName, string contentType, CancellationToken ct = default);

    /// <summary>Opens a stored file for reading. The caller owns the returned stream. Throws <see cref="FileNotFoundException"/> if missing.</summary>
    Task<(Stream FileStream, string ContentType, string FileName)> GetFileAsync(string storagePath, CancellationToken ct = default);

    /// <summary>Deletes a stored file. Missing files are ignored.</summary>
    Task DeleteFileAsync(string storagePath, CancellationToken ct = default);
}
