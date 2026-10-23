using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class NotificationWorkerSettings
{
    public const string SectionName = "NotificationWorker";

    public bool Enabled { get; init; } = true;

    // SDD §6: hourly in production; appsettings.Development.json shortens this for testing.
    [Range(1, 86400)]
    public int IntervalSeconds { get; init; } = 3600;
}
