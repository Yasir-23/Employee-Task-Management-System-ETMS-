using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Requests;

public record CreateTaskRequest
{
    [Required, MaxLength(200)]
    public string Title { get; init; } = string.Empty;

    [Required]
    public string Description { get; init; } = string.Empty;

    [EnumDataType(typeof(TaskPriority))]
    public TaskPriority Priority { get; init; }

    // API.md §3.1 names this field "dueDate" on the wire.
    [Required]
    [JsonPropertyName("dueDate")]
    public DateTime? DueDateUtc { get; init; }

    [Required]
    public Guid? AssigneeId { get; init; }
}
