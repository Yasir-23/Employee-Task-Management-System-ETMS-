using System.Text.Json;
using ETMS.Api.Common.Settings;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ETMS.Api.Services.Caching;

/// <summary>
/// SDD §12.2 cache-aside over <see cref="IDistributedCache"/> (Redis). Keys embed a generation token read from
/// <see cref="GenerationKey"/>; invalidating writes a new token, so every older entry stops being read at once
/// (IDistributedCache cannot delete by prefix) and simply ages out. The cache is fail-open: any cache error
/// is logged and the caller's factory runs against PostgreSQL instead.
/// </summary>
public class ReportCache : IReportCacheInvalidator
{
    public const string GenerationKey = "reports:gen";
    private const string InitialGeneration = "0";

    private readonly IDistributedCache _cache;
    private readonly CacheSettings _settings;
    private readonly ILogger<ReportCache> _logger;

    public ReportCache(IDistributedCache cache, IOptions<CacheSettings> settings, ILogger<ReportCache> logger)
    {
        _cache = cache;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>Builds the key <c>reports:{generation}:{endpoint}:{scope}:{parameters}</c>.</summary>
    public static string BuildKey(string generation, string endpoint, string scope, string parameters) =>
        $"reports:{generation}:{endpoint}:{scope}:{parameters}";

    /// <param name="endpoint">Report name, e.g. "summary".</param>
    /// <param name="scope">The caller's data scope: "all" for system-wide data, otherwise a per-user scope.</param>
    /// <param name="parameters">Query parameters that change the result, or "-".</param>
    /// <param name="factory">Computes the report on a cache miss.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    public async Task<T> GetOrCreateAsync<T>(string endpoint, string scope, string parameters, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
    {
        string key;
        try
        {
            var generation = await _cache.GetStringAsync(GenerationKey, cancellationToken) ?? InitialGeneration;
            key = BuildKey(generation, endpoint, scope, parameters);

            var cached = await _cache.GetStringAsync(key, cancellationToken);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<T>(cached)!;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Report cache read failed for {Endpoint}; querying the database.", endpoint);
            return await factory(cancellationToken);
        }

        var value = await factory(cancellationToken);

        try
        {
            await _cache.SetStringAsync(key, JsonSerializer.Serialize(value), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_settings.AnalyticsTtlSeconds)
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Report cache write failed for {Endpoint}; the response is served uncached.", endpoint);
        }

        return value;
    }

    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // No expiry: the token must outlive every entry written under it.
            await _cache.SetStringAsync(GenerationKey, Guid.NewGuid().ToString("N"), new DistributedCacheEntryOptions(), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The task change is already committed; stale entries expire within the TTL at worst.
            _logger.LogWarning(ex, "Report cache invalidation failed; cached analytics may be stale for up to {Ttl} seconds.",
                _settings.AnalyticsTtlSeconds);
        }
    }
}
