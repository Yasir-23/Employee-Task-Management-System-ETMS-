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
    public DbSet<TaskAttachment> TaskAttachments => Set<TaskAttachment>();
    public DbSet<TaskBilling> TaskBillings => Set<TaskBilling>();

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

        // SDD §3.7
        modelBuilder.Entity<TaskAttachment>(entity =>
        {
            entity.ToTable("TaskAttachments", t => t.HasCheckConstraint(
                "CK_TaskAttachments_FileSizeBytes", "\"FileSizeBytes\" BETWEEN 1 AND 10485760"));
            entity.HasKey(a => a.Id);

            entity.Property(a => a.FileName).HasMaxLength(255).IsRequired();
            entity.Property(a => a.StoragePath).HasMaxLength(500).IsRequired();
            entity.Property(a => a.ContentType).HasMaxLength(127).IsRequired();
            entity.Property(a => a.FileSizeBytes).IsRequired();
            entity.Property(a => a.UploadedAtUtc).IsRequired();

            entity.HasIndex(a => a.TaskId).HasDatabaseName("IX_TaskAttachments_TaskId");
            entity.HasIndex(a => a.StoragePath).IsUnique().HasDatabaseName("UX_TaskAttachments_StoragePath");

            entity.HasOne(a => a.Task)
                .WithMany(t => t.Attachments)
                .HasForeignKey(a => a.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(a => a.UploadedByUser)
                .WithMany()
                .HasForeignKey(a => a.UploadedByUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // SDD §3.8
        modelBuilder.Entity<TaskBilling>(entity =>
        {
            entity.ToTable("TaskBillings", t => t.HasCheckConstraint(
                "CK_TaskBillings_AmountCents", "\"AmountCents\" BETWEEN 50 AND 99999999"));
            entity.HasKey(b => b.Id);

            entity.Property(b => b.AmountCents).IsRequired();
            entity.Property(b => b.Currency).HasMaxLength(3).IsFixedLength().IsRequired().HasDefaultValue("usd");
            entity.Property(b => b.Status).HasConversion<int>().IsRequired();
            entity.Property(b => b.StripePaymentIntentId).HasMaxLength(255);
            entity.Property(b => b.StripeSessionId).HasMaxLength(255);
            entity.Property(b => b.CreatedAtUtc).IsRequired();

            // FR-PAY-04: maps to PostgreSQL's xmin system column (no physical column), so concurrent
            // checkouts on the same billing fail with DbUpdateConcurrencyException instead of both winning.
            entity.Property<uint>("Version").IsRowVersion();

            entity.HasIndex(b => b.TaskId).IsUnique().HasDatabaseName("UX_TaskBillings_TaskId");
            entity.HasIndex(b => b.StripeSessionId)
                .IsUnique()
                .HasFilter("\"StripeSessionId\" IS NOT NULL")
                .HasDatabaseName("UX_TaskBillings_StripeSessionId");

            // Financial records are never cascade-deleted with their task.
            entity.HasOne(b => b.Task)
                .WithOne(t => t.Billing)
                .HasForeignKey<TaskBilling>(b => b.TaskId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
