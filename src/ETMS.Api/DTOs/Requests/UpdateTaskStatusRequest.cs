using System.ComponentModel.DataAnnotations;
using ETMS.Api.Models.Enums;

namespace ETMS.Api.DTOs.Requests;

public record UpdateTaskStatusRequest
{
    [Required, EnumDataType(typeof(TaskLifecycleStatus))]
    public TaskLifecycleStatus? NewStatus { get; init; }
}
