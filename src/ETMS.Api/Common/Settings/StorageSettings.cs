using System.ComponentModel.DataAnnotations;

namespace ETMS.Api.Common.Settings;

public class StorageSettings
{
    public const string SectionName = "Storage";

    /// <summary>Upload root. A relative path is resolved against the application's content root.</summary>
    [Required]
    public string LocalStoragePath { get; init; } = "App_Data/Uploads";
}
