namespace ETMS.Api.DTOs.Responses;

public record DepartmentDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
}
