namespace ETMS.Api.DTOs.Responses;

public record AuditLogResponseDto
{
    public long Id { get; init; }
    public Guid ChangedByUserId { get; init; }
    public string ChangedByName { get; init; } = string.Empty;
    public string PropertyName { get; init; } = string.Empty;
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
    public DateTime TimestampUtc { get; init; }
}
