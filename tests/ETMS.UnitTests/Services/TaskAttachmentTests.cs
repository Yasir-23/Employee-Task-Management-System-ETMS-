using System.Security.Claims;
using System.Text;
using ETMS.Api.Common.Exceptions;
using ETMS.Api.Common.Settings;
using ETMS.Api.Controllers;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;
using ETMS.Api.Models.Enums;
using ETMS.Api.Services.Implementations;
using ETMS.Api.Services.Interfaces;
using ETMS.Api.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ETMS.UnitTests.Services;

public sealed class TaskAttachmentTests : IDisposable
{
    private const string PdfType = "application/pdf";
    private const string PngType = "image/png";
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n%%EOF");
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D];
    private static readonly byte[] DocxBytes = [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00];

    private readonly TaskServiceTestContext _ctx = new();
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), $"etms-storage-tests-{Guid.NewGuid():N}");
    private readonly LocalStorageService _storage;
    private readonly AttachmentService _service;

    public TaskAttachmentTests()
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());

        _storage = new LocalStorageService(
            Options.Create(new StorageSettings { LocalStoragePath = _storageRoot }),
            environment.Object);
        _service = new AttachmentService(_ctx.Db, _storage, NullLogger<AttachmentService>.Instance);
    }

    public void Dispose()
    {
        _ctx.Dispose();
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }
    }

    // ---------------------------------------------------------------------
    // Upload: BR-06 validation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Upload_ValidPdfByAssignee_StoresFileUnderGeneratedNameAndPersistsMetadata()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(PdfBytes, "stripe-flow.pdf", PdfType));

        Assert.Equal(task.Id, result.TaskId);
        Assert.Equal("stripe-flow.pdf", result.FileName);
        Assert.Equal(PdfType, result.ContentType);
        Assert.Equal(PdfBytes.Length, result.FileSizeBytes);
        Assert.Equal(_ctx.Employee.Id, result.UploadedByUserId);
        Assert.Equal(_ctx.Employee.FullName, result.UploaderName);

        var entity = Assert.Single(_ctx.Db.TaskAttachments);
        Assert.Matches("^[0-9a-f]{32}\\.pdf$", entity.StoragePath); // never derived from the client name
        Assert.Equal(PdfBytes, await File.ReadAllBytesAsync(Path.Combine(_storageRoot, entity.StoragePath)));
    }

    [Theory]
    [InlineData("diagram.png", PngType)]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpeg")]
    [InlineData("spec.docx", DocxType)]
    public async Task Upload_EachAllowedType_Succeeds(string fileName, string contentType)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var bytes = contentType switch
        {
            PngType => PngBytes,
            DocxType => DocxBytes,
            _ => new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }
        };

        var result = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(bytes, fileName, contentType));

        Assert.Equal(contentType, result.ContentType);
    }

    [Fact]
    public async Task Upload_FileOverTenMegabytes_Returns413()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var bytes = new byte[AttachmentService.MaxFileSizeBytes + 1];
        PdfBytes.CopyTo(bytes, 0);

        var ex = await Assert.ThrowsAsync<PayloadTooLargeException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, CreateFile(bytes, "huge.pdf", PdfType)));

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ex.StatusCode);
        AssertNothingStored();
    }

    [Fact]
    public async Task Upload_FileExactlyTenMegabytes_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var bytes = new byte[AttachmentService.MaxFileSizeBytes];
        PdfBytes.CopyTo(bytes, 0);

        var result = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(bytes, "limit.pdf", PdfType));

        Assert.Equal(AttachmentService.MaxFileSizeBytes, result.FileSizeBytes);
    }

    [Theory]
    [InlineData("installer.exe", "application/octet-stream")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("legacy.doc", "application/msword")]
    [InlineData("no-extension", PdfType)]
    public async Task Upload_DisallowedExtension_Returns415(string fileName, string contentType)
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var ex = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, CreateFile(PdfBytes, fileName, contentType)));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, ex.StatusCode);
        AssertNothingStored();
    }

    [Fact]
    public async Task Upload_ContentTypeNotMatchingExtension_Returns415()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var ex = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, CreateFile(PdfBytes, "report.pdf", PngType)));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, ex.StatusCode);
    }

    [Fact]
    public async Task Upload_SignatureNotMatchingDeclaredType_Returns415()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);
        var disguisedExecutable = Encoding.ASCII.GetBytes("MZ\x90\x00 this is not a png");

        var ex = await Assert.ThrowsAsync<UnsupportedMediaTypeException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, CreateFile(disguisedExecutable, "cat.png", PngType)));

        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, ex.StatusCode);
        AssertNothingStored();
    }

    [Fact]
    public async Task Upload_ContentTypeWithParameters_IsAccepted()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(PdfBytes, "report.PDF", "Application/PDF; charset=binary"));

        Assert.Equal(PdfType, result.ContentType);
        Assert.Equal("report.pdf", result.FileName);
    }

    [Fact]
    public async Task Upload_EmptyFile_Returns400()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, CreateFile([], "empty.pdf", PdfType)));
    }

    [Fact]
    public async Task Upload_ClientPathAndUnsafeCharacters_AreStrippedFromFileName()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(PdfBytes, @"C:\Users\alex\..\q3 <final>|""plan"".pdf", PdfType));

        Assert.Equal("q3 finalplan.pdf", result.FileName);
    }

    // ---------------------------------------------------------------------
    // Upload: access boundary
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Upload_ByCreatorAndTeamManager_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);

        var result = await _service.UploadAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            CreateFile(PdfBytes, "brief.pdf", PdfType));

        Assert.Equal(_ctx.Manager.Id, result.UploadedByUserId);
    }

    [Fact]
    public async Task Upload_ByEmployeeNotAssigned_Returns404AndStoresNothing()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.UploadAsync(
            _ctx.Teammate.Id, nameof(Role.Employee), task.Id, CreateFile(PdfBytes, "x.pdf", PdfType)));

        AssertNothingStored();
    }

    [Fact]
    public async Task Upload_ToUnknownTask_Returns404()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _service.UploadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), Guid.NewGuid(), CreateFile(PdfBytes, "x.pdf", PdfType)));
    }

    // ---------------------------------------------------------------------
    // Download: access boundary
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Download_ByAssignee_StreamsStoredContentWithMetadata()
    {
        var (task, attachment) = await UploadPdfAsync();

        var download = await _service.DownloadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id, attachment.Id);

        await using var content = download.Content;
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(PdfBytes, copy.ToArray());
        Assert.Equal(PdfType, download.ContentType);
        Assert.Equal("evidence.pdf", download.FileName);
    }

    [Fact]
    public async Task Download_ByTeamManagerAndAdmin_Succeeds()
    {
        var (task, attachment) = await UploadPdfAsync();
        var admin = AddAdmin();

        var managerDownload = await _service.DownloadAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, attachment.Id);
        await managerDownload.Content.DisposeAsync();
        var adminDownload = await _service.DownloadAsync(admin.Id, nameof(Role.Admin), task.Id, attachment.Id);
        await adminDownload.Content.DisposeAsync();

        Assert.Equal("evidence.pdf", managerDownload.FileName);
        Assert.Equal("evidence.pdf", adminDownload.FileName);
    }

    [Theory]
    [InlineData("Teammate", nameof(Role.Employee))]
    [InlineData("ForeignEmployee", nameof(Role.Employee))]
    [InlineData("OtherManager", nameof(Role.Manager))]
    public async Task Download_OutsideTaskScope_Returns404(string user, string role)
    {
        var (task, attachment) = await UploadPdfAsync();
        var userId = user switch
        {
            "Teammate" => _ctx.Teammate.Id,
            "ForeignEmployee" => _ctx.ForeignEmployee.Id,
            _ => _ctx.OtherManager.Id
        };

        await Assert.ThrowsAsync<NotFoundException>(() => _service.DownloadAsync(userId, role, task.Id, attachment.Id));
    }

    [Fact]
    public async Task Download_AttachmentRequestedThroughAnotherTask_Returns404()
    {
        var (_, attachment) = await UploadPdfAsync();
        var otherTask = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.DownloadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), otherTask.Id, attachment.Id));
    }

    [Fact]
    public async Task Download_WhenStoredFileIsMissing_Returns404()
    {
        var (task, attachment) = await UploadPdfAsync();
        File.Delete(Path.Combine(_storageRoot, _ctx.Db.TaskAttachments.Single().StoragePath));

        await Assert.ThrowsAsync<NotFoundException>(() => _service.DownloadAsync(
            _ctx.Employee.Id, nameof(Role.Employee), task.Id, attachment.Id));
    }

    // ---------------------------------------------------------------------
    // Task details, storage guard and controller responses
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetTaskById_IncludesAttachments()
    {
        var (task, attachment) = await UploadPdfAsync();

        var details = await _ctx.Service.GetTaskByIdAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id);

        var listed = Assert.Single(details.Attachments);
        Assert.Equal(attachment, listed);
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("..\\outside.pdf")]
    [InlineData("nested/../../outside.pdf")]
    public async Task Storage_PathOutsideRoot_IsRejected(string storagePath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.GetFileAsync(storagePath));
        await Assert.ThrowsAsync<ArgumentException>(() => _storage.DeleteFileAsync(storagePath));
    }

    [Fact]
    public async Task Controller_Upload_Returns201WithDownloadLocation()
    {
        var taskId = Guid.NewGuid();
        var dto = new TaskAttachmentResponseDto(Guid.NewGuid(), taskId, "a.pdf", PdfType, 10, _ctx.Employee.Id, "Alex", DateTime.UtcNow);
        var service = new Mock<IAttachmentService>();
        service.Setup(s => s.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), taskId, It.IsAny<IFormFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(dto);
        var controller = CreateController(service.Object, _ctx.Employee.Id, Role.Employee);

        var response = await controller.Upload(taskId, CreateFile(PdfBytes, "a.pdf", PdfType), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(response.Result);
        Assert.Equal(StatusCodes.Status201Created, created.StatusCode);
        Assert.Equal(nameof(TaskAttachmentsController.Download), created.ActionName);
        Assert.Equal(dto.Id, created.RouteValues!["attachmentId"]);
        Assert.Same(dto, created.Value);
    }

    [Fact]
    public async Task Controller_Download_ReturnsAttachmentStreamWithNoSniff()
    {
        var (task, attachment) = await UploadPdfAsync();
        var controller = CreateController(_service, _ctx.Employee.Id, Role.Employee);

        var response = await controller.Download(task.Id, attachment.Id, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(response);
        await file.FileStream.DisposeAsync();
        Assert.Equal(PdfType, file.ContentType);
        Assert.Equal("evidence.pdf", file.FileDownloadName);
        Assert.True(file.EnableRangeProcessing);
        Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions.ToString());
    }

    private async Task<(TaskItem Task, TaskAttachmentResponseDto Attachment)> UploadPdfAsync()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);
        var attachment = await _service.UploadAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            CreateFile(PdfBytes, "evidence.pdf", PdfType));
        return (task, attachment);
    }

    private User AddAdmin()
    {
        var admin = new User
        {
            Id = Guid.NewGuid(),
            FullName = "System Admin",
            Email = "admin@company.com",
            PasswordHash = "n/a",
            Role = Role.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        _ctx.Db.Users.Add(admin);
        _ctx.Db.SaveChanges();
        return admin;
    }

    private void AssertNothingStored()
    {
        Assert.Empty(_ctx.Db.TaskAttachments);
        Assert.Empty(Directory.EnumerateFiles(_storageRoot));
    }

    private static IFormFile CreateFile(byte[] content, string fileName, string contentType) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

    private static TaskAttachmentsController CreateController(IAttachmentService service, Guid userId, Role role) =>
        new(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Role, role.ToString())
                    ], "Test"))
                }
            }
        };
}
