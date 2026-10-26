using ETMS.Api.Models.Entities;

namespace ETMS.Api.Services.Webhooks;

/// <summary>FR-HOOK-04: exponential backoff between attempts, then dead-lettering.</summary>
public static class WebhookRetryPolicy
{
    /// <summary>Delay after the 1st, 2nd, ... failed attempt.</summary>
    public static readonly IReadOnlyList<TimeSpan> Backoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(6)
    ];

    public static TimeSpan GetDelayAfterAttempt(int attemptCount) =>
        Backoff[Math.Clamp(attemptCount, 1, Backoff.Count) - 1];

    /// <summary>
    /// Applies the outcome of one attempt to <paramref name="delivery"/>. A 2xx status is a success; anything
    /// else (including a null status for a network error or timeout) schedules a retry or dead-letters the row.
    /// </summary>
    /// <returns>True when the delivery has just been dead-lettered.</returns>
    public static bool RecordAttempt(WebhookDeliveryLog delivery, int? statusCode, DateTime nowUtc, int maxAttempts)
    {
        delivery.AttemptCount++;
        delivery.StatusCode = statusCode;
        delivery.IsSuccess = statusCode is >= 200 and < 300;
        delivery.TimestampUtc = nowUtc;

        if (delivery.IsSuccess || delivery.AttemptCount >= maxAttempts)
        {
            delivery.NextRetryUtc = null;
            return !delivery.IsSuccess;
        }

        delivery.NextRetryUtc = nowUtc + GetDelayAfterAttempt(delivery.AttemptCount);
        return false;
    }
}
