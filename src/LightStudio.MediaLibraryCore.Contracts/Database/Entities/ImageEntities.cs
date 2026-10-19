using System;
using System.Text.Json.Serialization;

namespace LightStudio.MediaLibraryCore.Database.Entities;

public sealed class DbImageFile
{
    public int Id { get; set; }
    public string Path { get; set; }
    public string FolderPath { get; set; }
    public string LibraryRoot { get; set; }
    public string FileName { get; set; }
    public long FileSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset? DateTaken { get; set; }
    public DateTimeOffset? FileCreatedDate { get; set; }
    public DateTimeOffset FileLastModifiedDate { get; set; }
    public DateTimeOffset DatabaseItemAddedDate { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Country { get; set; }
    public string State { get; set; }
    public string City { get; set; }

    /// <summary>The GeoNames language tag the location names were resolved in.</summary>
    [JsonIgnore]
    public string LocationLanguage { get; set; }
}
