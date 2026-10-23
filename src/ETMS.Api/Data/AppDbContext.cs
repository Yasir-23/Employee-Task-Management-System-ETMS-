using ETMS.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace ETMS.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Department>(entity =>
        {
            entity.ToTable("Departments");
            entity.HasKey(d => d.Id);

            entity.Property(d => d.Name).HasMaxLength(100).IsRequired();
            entity.Property(d => d.CreatedAtUtc).IsRequired();

            entity.HasIndex(d => d.Name).IsUnique();
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(u => u.Id);

            entity.Property(u => u.FullName).HasMaxLength(150).IsRequired();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.PasswordHash).HasColumnType("text").IsRequired();
            entity.Property(u => u.Role).HasConversion<int>().IsRequired();
            entity.Property(u => u.CreatedAtUtc).IsRequired();

            entity.HasIndex(u => u.Email).IsUnique().HasDatabaseName("IX_Users_Email");
            entity.HasIndex(u => u.ManagerId).HasDatabaseName("IX_Users_ManagerId");

            entity.HasOne(u => u.Department)
                .WithMany(d => d.Users)
                .HasForeignKey(u => u.DepartmentId)
                .OnDelete(DeleteBehavior.SetNull);

            // Self-referencing manager hierarchy; a manager with reports cannot be hard-deleted.
            entity.HasOne(u => u.Manager)
                .WithMany(m => m.Subordinates)
                .HasForeignKey(u => u.ManagerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("RefreshTokens");
            entity.HasKey(rt => rt.Id);

            entity.Property(rt => rt.TokenHash).HasMaxLength(256).IsRequired();
            entity.Property(rt => rt.ExpiresAtUtc).IsRequired();
            entity.Property(rt => rt.IsRevoked).IsRequired().HasDefaultValue(false);

            entity.HasIndex(rt => rt.TokenHash);

            entity.HasOne(rt => rt.User)
                .WithMany()
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.ToTable("Tasks");
            entity.HasKey(t => t.Id);

            entity.Property(t => t.Title).HasMaxLength(200).IsRequired();
            entity.Property(t => t.Description).HasColumnType("text").IsRequired();
            entity.Property(t => t.Priority).HasConversion<int>().IsRequired();
            entity.Property(t => t.Status).HasConversion<int>().IsRequired();
            entity.Property(t => t.DueDateUtc).IsRequired();
            entity.Property(t => t.ReminderSent).IsRequired().HasDefaultValue(false);
            entity.Property(t => t.OverdueAlertSent).IsRequired().HasDefaultValue(false);
            entity.Property(t => t.CreatedAtUtc).IsRequired();
            entity.Property(t => t.UpdatedAtUtc).IsRequired();

            entity.HasIndex(t => new { t.AssigneeId, t.Status }).HasDatabaseName("IX_Tasks_Assignee_Status");
            entity.HasIndex(t => new { t.DueDateUtc, t.Status }).HasDatabaseName("IX_Tasks_DueDate_Status");

            entity.HasOne(t => t.Creator)
                .WithMany(u => u.CreatedTasks)
                .HasForeignKey(t => t.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.Assignee)
                .WithMany(u => u.AssignedTasks)
                .HasForeignKey(t => t.AssigneeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Comment>(entity =>
        {
            entity.ToTable("Comments");
            entity.HasKey(c => c.Id);

            entity.Property(c => c.Content).HasColumnType("text").IsRequired();
            entity.Property(c => c.CreatedAtUtc).IsRequired();

            entity.HasOne(c => c.Task)
                .WithMany(t => t.Comments)
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(c => c.User)
                .WithMany(u => u.Comments)
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.ToTable("AuditLogs");
            entity.HasKey(a => a.Id);

            entity.Property(a => a.Id).UseIdentityAlwaysColumn();
            entity.Property(a => a.PropertyName).HasMaxLength(100).IsRequired();
            entity.Property(a => a.OldValue).HasColumnType("text");
            entity.Property(a => a.NewValue).HasColumnType("text");
            entity.Property(a => a.TimestampUtc).IsRequired();

            entity.HasIndex(a => a.TaskId).HasDatabaseName("IX_AuditLogs_TaskId");

            entity.HasOne(a => a.Task)
                .WithMany(t => t.AuditLogs)
                .HasForeignKey(a => a.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            // Audit history is immutable; block deleting a user who still has audit entries.
            entity.HasOne(a => a.ChangedByUser)
                .WithMany()
                .HasForeignKey(a => a.ChangedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
