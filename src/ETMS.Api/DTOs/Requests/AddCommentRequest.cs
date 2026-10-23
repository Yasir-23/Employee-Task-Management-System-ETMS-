using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.DTOs.Requests;

public record AddCommentRequest
{
    [Required, MaxLength(4000)]
    public string Content { get; init; } = string.Empty;
}
