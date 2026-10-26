using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class WebhookSettings
{
    public const string SectionName = "Webhooks";

    /// <summary>Runs the outbound delivery worker. Events are still written to the outbox when disabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>FR-HOOK-04: total attempts per delivery before it is dead-lettered.</summary>
    [Range(1, 6)]
    public int MaxAttempts { get; init; } = 6;

    // SDD §10.3
    [Range(1, 3600)]
    public int PollIntervalSeconds { get; init; } = 10;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 50;
}
