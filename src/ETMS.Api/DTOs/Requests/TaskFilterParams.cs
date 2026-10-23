using System.ComponentModel.DataAnnotations;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Requests;

public record TaskFilterParams
{
    [EnumDataType(typeof(TaskLifecycleStatus))]
    public TaskLifecycleStatus? Status { get; init; }

    [EnumDataType(typeof(TaskPriority))]
    public TaskPriority? Priority { get; init; }

    [Range(1, int.MaxValue)]
    public int PageNumber { get; init; } = 1;

    // NFR 5.2: list endpoints enforce a hard upper limit of 100.
    [Range(1, 100)]
    public int PageSize { get; init; } = 20;
}
