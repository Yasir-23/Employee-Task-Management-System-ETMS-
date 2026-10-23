namespace ETMS.Api.DTOs.Responses;

public record DashboardSummaryDto
{
    public int TotalTasks { get; init; }
    public int PendingTasks { get; init; }
    public int InProgressTasks { get; init; }
    public int InReviewTasks { get; init; }
    public int CompletedTasks { get; init; }

    // Not Completed and past DueDateUtc; these tasks are also counted in their status bucket.
    public int OverdueTasks { get; init; }
}
