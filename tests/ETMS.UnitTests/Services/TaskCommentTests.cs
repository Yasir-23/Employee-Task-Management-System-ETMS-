using ETMS.Api.Common.Exceptions;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.Models.Enums;
using Microsoft.AspNetCore.Http;

namespace ETMS.UnitTests.Services;

public sealed class TaskCommentTests : IDisposable
{
    private readonly TaskServiceTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task AddComment_ByAssignee_PersistsAndReturnsComment()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        var result = await _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
            new AddCommentRequest { Content = "  Staging webhook tested successfully.  " });

        Assert.Equal(task.Id, result.TaskId);
        Assert.Equal(_ctx.Employee.Id, result.UserId);
        Assert.Equal(_ctx.Employee.FullName, result.AuthorName);
        Assert.Equal("Staging webhook tested successfully.", result.Content);
        Assert.Single(_ctx.Db.Comments);
    }

    [Fact]
    public async Task AddComment_ByCreator_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);

        var result = await _ctx.Service.AddCommentAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id,
            new AddCommentRequest { Content = "Please prioritise this." });

        Assert.Equal(_ctx.Manager.Id, result.UserId);
    }

    [Fact]
    public async Task AddComment_ByAdmin_OnAnyTask_Succeeds()
    {
        var task = _ctx.AddTask(_ctx.ForeignEmployee.Id, creatorId: _ctx.OtherManager.Id);
        var adminId = Guid.NewGuid();
        _ctx.Db.Users.Add(new Api.Models.Entities.User
        {
            Id = adminId,
            FullName = "System Admin",
            Email = "admin@company.com",
            PasswordHash = "n/a",
            Role = Role.Admin,
            CreatedAtUtc = DateTime.UtcNow
        });
        await _ctx.Db.SaveChangesAsync();

        var result = await _ctx.Service.AddCommentAsync(adminId, nameof(Role.Admin), task.Id,
            new AddCommentRequest { Content = "Audit note." });

        Assert.Equal("System Admin", result.AuthorName);
    }

    [Fact]
    public async Task AddComment_ByEmployeeWhoIsNotAssignee_ThrowsForbidden()
    {
        var task = _ctx.AddTask(_ctx.Teammate.Id);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
                new AddCommentRequest { Content = "Can I help?" }));

        Assert.Equal(StatusCodes.Status403Forbidden, ex.StatusCode);
        Assert.Empty(_ctx.Db.Comments);
    }

    [Fact]
    public async Task AddComment_ByUnrelatedManager_ThrowsForbidden()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id, creatorId: _ctx.Manager.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            _ctx.Service.AddCommentAsync(_ctx.OtherManager.Id, nameof(Role.Manager), task.Id,
                new AddCommentRequest { Content = "Drive-by comment." }));
    }

    [Fact]
    public async Task AddComment_OnMissingTask_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), Guid.NewGuid(),
                new AddCommentRequest { Content = "Hello?" }));
    }

    [Fact]
    public async Task AddComment_WithWhitespaceContent_ThrowsBadRequest()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id,
                new AddCommentRequest { Content = "   " }));
    }

    [Fact]
    public async Task GetTaskById_ReturnsCommentsInChronologicalOrder()
    {
        var task = _ctx.AddTask(_ctx.Employee.Id);

        await _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id, new AddCommentRequest { Content = "first" });
        await _ctx.Service.AddCommentAsync(_ctx.Manager.Id, nameof(Role.Manager), task.Id, new AddCommentRequest { Content = "second" });
        await _ctx.Service.AddCommentAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id, new AddCommentRequest { Content = "third" });

        var details = await _ctx.Service.GetTaskByIdAsync(_ctx.Employee.Id, nameof(Role.Employee), task.Id);

        Assert.Equal(new[] { "first", "second", "third" }, details.Comments.Select(c => c.Content));
        Assert.Equal(new[] { _ctx.Employee.FullName, _ctx.Manager.FullName, _ctx.Employee.FullName },
            details.Comments.Select(c => c.AuthorName));
    }
}
