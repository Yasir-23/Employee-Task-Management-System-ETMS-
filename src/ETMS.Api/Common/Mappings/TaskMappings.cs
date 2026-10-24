using System.Linq.Expressions;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Models.Entities;

namespace ETMS.Api.Common.Mappings;

public static class TaskMappings
{
    // Translatable to SQL; null-safe for the optional assignee.
    public static readonly Expression<Func<TaskItem, TaskResponseDto>> ToDtoExpression = t => new TaskResponseDto
    {
        Id = t.Id,
        Title = t.Title,
        Description = t.Description,
        Priority = t.Priority,
        Status = t.Status,
        DueDateUtc = t.DueDateUtc,
        CreatedByUserId = t.CreatedByUserId,
        CreatorName = t.Creator.FullName,
        AssigneeId = t.AssigneeId,
        AssigneeName = t.Assignee != null ? t.Assignee.FullName : null,
        CreatedAtUtc = t.CreatedAtUtc,
        UpdatedAtUtc = t.UpdatedAtUtc
    };

    public static readonly Expression<Func<Comment, CommentResponseDto>> CommentToDtoExpression = c => new CommentResponseDto
    {
        Id = c.Id,
        TaskId = c.TaskId,
        UserId = c.UserId,
        AuthorName = c.User.FullName,
        Content = c.Content,
        CreatedAtUtc = c.CreatedAtUtc
    };

    public static readonly Expression<Func<TaskAttachment, TaskAttachmentResponseDto>> AttachmentToDtoExpression = a => new TaskAttachmentResponseDto(
        a.Id,
        a.TaskId,
        a.FileName,
        a.ContentType,
        a.FileSizeBytes,
        a.UploadedByUserId,
        a.UploadedByUser.FullName,
        a.UploadedAtUtc);
}
