using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record ReassignTaskRequest
{
    [Required]
    public Guid? NewAssigneeId { get; init; }
}
