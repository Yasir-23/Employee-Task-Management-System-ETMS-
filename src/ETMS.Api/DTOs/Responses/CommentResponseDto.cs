namespace ETMS.Api.DTOs.Responses;

public record CommentResponseDto
{
    public Guid Id { get; init; }
    public Guid TaskId { get; init; }
    public Guid UserId { get; init; }
    public string AuthorName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}
