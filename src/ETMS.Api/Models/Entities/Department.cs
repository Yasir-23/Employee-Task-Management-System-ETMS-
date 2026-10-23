namespace ETMS.Api.Models.Entities;

public class Department
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    public ICollection<User> Users { get; set; } = new List<User>();
}
