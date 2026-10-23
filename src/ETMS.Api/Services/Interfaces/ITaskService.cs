using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;

namespace ETMS.Api.Services.Interfaces;

public interface ITaskService
{
    Task<TaskResponseDto> CreateTaskAsync(Guid creatorUserId, CreateTaskRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<TaskResponseDto>> GetTasksAsync(Guid currentUserId, string role, TaskFilterParams filter, CancellationToken cancellationToken = default);

    Task<TaskDetailsDto> GetTaskByIdAsync(Guid currentUserId, string role, Guid taskId, CancellationToken cancellationToken = default);

    Task<CommentResponseDto> AddCommentAsync(Guid currentUserId, string role, Guid taskId, AddCommentRequest request, CancellationToken cancellationToken = default);

    Task<TaskResponseDto> UpdateTaskStatusAsync(Guid currentUserId, Guid taskId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default);

    Task<TaskResponseDto> ReassignTaskAsync(Guid currentManagerId, Guid taskId, ReassignTaskRequest request, CancellationToken cancellationToken = default);
}
