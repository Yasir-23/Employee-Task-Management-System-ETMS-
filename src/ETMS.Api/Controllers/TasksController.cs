using ETMS.Api.Common.Extensions;
using ETMS.Api.DTOs.Requests;
using ETMS.Api.DTOs.Responses;
using ETMS.Api.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ETMS.Api.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize]
[Produces("application/json")]
public class TasksController : ControllerBase
{
    private readonly ITaskService _taskService;

    public TasksController(ITaskService taskService)
    {
        _taskService = taskService;
    }

    [HttpPost]
    [Authorize(Roles = "Manager")]
    [ProducesResponseType(typeof(TaskResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TaskResponseDto>> Create(CreateTaskRequest request, CancellationToken cancellationToken)
    {
        var task = await _taskService.CreateTaskAsync(User.GetUserId(), request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = task.Id }, task);
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<TaskResponseDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<TaskResponseDto>>> GetAll([FromQuery] TaskFilterParams filter, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.GetTasksAsync(User.GetUserId(), User.GetRole(), filter, cancellationToken));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TaskDetailsDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.GetTaskByIdAsync(User.GetUserId(), User.GetRole(), id, cancellationToken));
    }

    [HttpPost("{id:guid}/comments")]
    [ProducesResponseType(typeof(CommentResponseDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CommentResponseDto>> AddComment(Guid id, AddCommentRequest request, CancellationToken cancellationToken)
    {
        var comment = await _taskService.AddCommentAsync(User.GetUserId(), User.GetRole(), id, request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id }, comment);
    }

    [HttpPatch("{id:guid}/status")]
    [Authorize(Roles = "Employee")]
    [ProducesResponseType(typeof(TaskResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TaskResponseDto>> UpdateStatus(Guid id, UpdateTaskStatusRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.UpdateTaskStatusAsync(User.GetUserId(), id, request, cancellationToken));
    }

    [HttpPut("{id:guid}/reassign")]
    [Authorize(Roles = "Manager")]
    [ProducesResponseType(typeof(TaskResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TaskResponseDto>> Reassign(Guid id, ReassignTaskRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _taskService.ReassignTaskAsync(User.GetUserId(), id, request, cancellationToken));
    }
}
