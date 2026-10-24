using ETMS.Api.Common.Extensions;
using ETMS.Api.Common.Filters;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ETMS.Api.Controllers;

[ApiController]
[Route("api/tasks/{id:guid}/attachments")]
[Authorize]
[Produces("application/json")]
public class TaskAttachmentsController : ControllerBase
{
    // Deliberately well above the 10 MB BR-06 cap. When Kestrel aborts a body mid-upload it closes the
    // socket, and browsers (fetch, Swagger UI) see a connection reset instead of the 413. Reading bodies
    // up to 35 MB lets common oversized files (11-30 MB) reach the service's BR-06 check, which returns
    // a clean ProblemDetails 413. Larger bodies are still cut off by Kestrel (RejectOversizedForm -> 413).
    private const long MaxRequestBodyBytes = 35_000_000;

    private readonly IAttachmentService _attachmentService;

    public TaskAttachmentsController(IAttachmentService attachmentService)
    {
        _attachmentService = attachmentService;
    }

    /// <summary>Uploads a file to a task the caller can see (BR-06: max 10 MB; .pdf, .png, .jpg, .jpeg, .docx).</summary>
    /// <param name="id">Task ID.</param>
    /// <param name="file">The file, sent as the multipart form field "file".</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBodyBytes)]
    [RejectOversizedForm]
    [ProducesResponseType(typeof(TaskAttachmentResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<TaskAttachmentResponseDto>> Upload(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        var attachment = await _attachmentService.UploadAsync(User.GetUserId(), User.GetRole(), id, file, cancellationToken);
        return CreatedAtAction(nameof(Download), new { id, attachmentId = attachment.Id }, attachment);
    }

    /// <summary>Streams an attachment as a download. Supports HTTP range requests.</summary>
    [HttpGet("{attachmentId:guid}")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, Guid attachmentId, CancellationToken cancellationToken)
    {
        var download = await _attachmentService.DownloadAsync(User.GetUserId(), User.GetRole(), id, attachmentId, cancellationToken);

        // Uploaded content is never rendered inline or MIME-sniffed by browsers.
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(download.Content, download.ContentType, download.FileName, enableRangeProcessing: true);
    }
}
