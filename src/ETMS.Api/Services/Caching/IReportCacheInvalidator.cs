namespace ETMS.Api.Services.Caching;

/// <summary>FR-RESIL-02: called after every committed task change so cached analytics are never served stale.</summary>
public interface IReportCacheInvalidator
{
    /// <summary>Invalidates every cached report. Never throws when the cache is unavailable.</summary>
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
