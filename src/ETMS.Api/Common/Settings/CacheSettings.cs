using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class CacheSettings
{
    public const string SectionName = "Cache";

    /// <summary>FR-RESIL-02: absolute lifetime of a cached analytics response.</summary>
    [Range(1, 86400)]
    public int AnalyticsTtlSeconds { get; init; } = 300;
}
