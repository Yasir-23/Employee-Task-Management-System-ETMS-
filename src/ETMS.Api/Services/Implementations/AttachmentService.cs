using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Mappings;
using ETMS.Api.Data;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace ETMS.Api.Services.Implementations;

public class AttachmentService : IAttachmentService
{
    // BR-06: 10 MB cap.
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    public const int MaxFileNameLength = 255;

    // BR-06: extension -> the only MIME type accepted for it, plus the file signature it must start with.
    private static readonly IReadOnlyDictionary<string, AllowedType> AllowedTypes = new Dictionary<string, AllowedType>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new("application/pdf", [0x25, 0x50, 0x44, 0x46]), // %PDF
        [".png"] = new("image/png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        [".jpg"] = new("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = new("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]) // ZIP container
    };

    private static readonly int MaxSignatureLength = AllowedTypes.Values.Max(t => t.Signature.Length);

    private static readonly char[] UnsafeFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private readonly AppDbContext _db;
    private readonly IStorageService _storage;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(AppDbContext db, IStorageService storage, ILogger<AttachmentService> logger)
    {
        _db = db;
        _storage = storage;
        _logger = logger;
    }

    public async Task<TaskAttachmentResponseDto> UploadAsync(Guid currentUserId, string role, Guid taskId, IFormFile file, CancellationToken cancellationToken = default)
    {
        await EnsureTaskVisibleAsync(currentUserId, role, taskId, cancellationToken);

        if (file.Length == 0)
        {
            throw new BadRequestException("The uploaded file is empty.");
        }

        if (file.Length > MaxFileSizeBytes)
        {
            throw new PayloadTooLargeException($"BR-06: attachments are limited to {MaxFileSizeBytes:N0} bytes (10 MB).");
        }

        var clientFileName = LastPathSegment(file.FileName);
        var extension = Path.GetExtension(clientFileName);
        if (!AllowedTypes.TryGetValue(extension, out var allowedType))
        {
            throw new UnsupportedMediaTypeException(
                $"BR-06: '{(extension.Length == 0 ? "(none)" : extension)}' is not an allowed attachment type. Allowed: {string.Join(", ", AllowedTypes.Keys)}.");
        }

        if (!MediaTypeHeaderValue.TryParse(file.ContentType, out var declaredType)
            || !string.Equals(declaredType.MediaType.Value, allowedType.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedMediaTypeException(
                $"BR-06: Content-Type '{file.ContentType}' does not match '{extension}' (expected '{allowedType.ContentType}').");
        }

        await using var content = await OpenVerifiedStreamAsync(file, extension, allowedType, cancellationToken);

        var fileName = SanitizeFileName(clientFileName, extension);
        var (storagePath, fileSize) = await _storage.SaveFileAsync(content, fileName, allowedType.ContentType, cancellationToken);

        var attachment = new TaskAttachment
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            FileName = fileName,
            StoragePath = storagePath,
            ContentType = allowedType.ContentType,
            FileSizeBytes = fileSize,
            UploadedByUserId = currentUserId,
            UploadedAtUtc = DateTime.UtcNow
        };

        try
        {
            _db.TaskAttachments.Add(attachment);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Compensate so a failed insert never leaves an unreferenced file behind.
            await _storage.DeleteFileAsync(storagePath, CancellationToken.None);
            throw;
        }

        return await _db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachment.Id)
            .Select(TaskMappings.AttachmentToDtoExpression)
            .SingleAsync(cancellationToken);
    }

    public async Task<AttachmentDownload> DownloadAsync(Guid currentUserId, string role, Guid taskId, Guid attachmentId, CancellationToken cancellationToken = default)
    {
        await EnsureTaskVisibleAsync(currentUserId, role, taskId, cancellationToken);

        // Matching on TaskId too: an attachment requested through another task's route is not found.
        var attachment = await _db.TaskAttachments
            .AsNoTracking()
            .Where(a => a.Id == attachmentId && a.TaskId == taskId)
            .Select(a => new { a.StoragePath, a.ContentType, a.FileName })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"Attachment '{attachmentId}' was not found on task '{taskId}'.");

        try
        {
            var (stream, _, _) = await _storage.GetFileAsync(attachment.StoragePath, cancellationToken);
            return new AttachmentDownload(stream, attachment.ContentType, attachment.FileName);
        }
        catch (FileNotFoundException)
        {
            _logger.LogError("Attachment {AttachmentId} references missing file {StoragePath}.", attachmentId, attachment.StoragePath);
            throw new NotFoundException($"Attachment '{attachmentId}' was not found on task '{taskId}'.");
        }
    }

    // Same visibility as GET /api/tasks/{id}; out-of-scope tasks are reported as 404 so their existence is not disclosed.
    private async Task EnsureTaskVisibleAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken)
    {
        var visible = await _db.Tasks
            .AsNoTracking()
            .ScopeToUser(currentUserId, role)
            .AnyAsync(t => t.Id == taskId, cancellationToken);

        if (!visible)
        {
            throw new NotFoundException($"Task '{taskId}' was not found.");
        }
    }

    /// <summary>BR-06: the leading bytes must match the declared type. Returns the stream rewound to the start.</summary>
    private static async Task<Stream> OpenVerifiedStreamAsync(IFormFile file, string extension, AllowedType allowedType, CancellationToken cancellationToken)
    {
        var stream = file.OpenReadStream();
        try
        {
            var header = new byte[MaxSignatureLength];
            var read = await stream.ReadAtLeastAsync(header, MaxSignatureLength, throwOnEndOfStream: false, cancellationToken);

            if (!header.AsSpan(0, read).StartsWith(allowedType.Signature))
            {
                throw new UnsupportedMediaTypeException($"BR-06: the file content is not a valid '{extension}' file.");
            }

            if (stream.CanSeek)
            {
                stream.Position = 0;
                return stream;
            }

            // Form file streams are seekable in practice; fall back to buffering (bounded by the 10 MB cap).
            var buffered = new MemoryStream();
            await buffered.WriteAsync(header.AsMemory(0, read), cancellationToken);
            await stream.CopyToAsync(buffered, cancellationToken);
            await stream.DisposeAsync();
            buffered.Position = 0;
            return buffered;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // Some clients send the full client path; keep only the final segment regardless of separator style.
    private static string LastPathSegment(string fileName)
    {
        var lastSeparator = fileName.LastIndexOfAny(['/', '\\']);
        return lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;
    }

    private static string SanitizeFileName(string fileName, string extension)
    {
        var cleaned = new string(fileName
            .Where(c => !char.IsControl(c) && Array.IndexOf(UnsafeFileNameChars, c) < 0)
            .ToArray());

        var stem = Path.GetFileNameWithoutExtension(cleaned).Trim().Trim('.');
        if (stem.Length == 0)
        {
            stem = "attachment";
        }

        var normalizedExtension = extension.ToLowerInvariant();
        var maxStemLength = MaxFileNameLength - normalizedExtension.Length;
        return (stem.Length > maxStemLength ? stem[..maxStemLength] : stem) + normalizedExtension;
    }

    private sealed record AllowedType(string ContentType, byte[] Signature);
}
