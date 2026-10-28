using System;
using System.Collections.Generic;
using System.Linq;
using LightMediaRenderer.Contracts;

namespace LightStudio.LightPlayer.Services;

/// <summary>A named set of HDR tone mapping options.</summary>
public sealed record ToneMappingProfile
{
    public string Name { get; init; } = string.Empty;

    public VideoSdrToneMappingOptions Options { get; init; } = new();
}

/// <summary>
/// The user's tone mapping profiles and the ones applied by default. A null or unknown default
/// leaves tone mapping to the renderer's automatic choice.
/// </summary>
public sealed record ToneMappingSettings
{
    public static ToneMappingSettings Default { get; } = new();

    /// <summary>Profile names are unique without regard to case.</summary>
    public static StringComparer NameComparer => StringComparer.OrdinalIgnoreCase;

    public IReadOnlyList<ToneMappingProfile> Profiles { get; init; } = [];

    /// <summary>Profile applied to images that carry absolute luminance.</summary>
    public string? ImageProfile { get; init; }

    /// <summary>Profile applied to HDR videos.</summary>
    public string? VideoProfile { get; init; }

    public ToneMappingProfile? Find(string? name) =>
        string.IsNullOrEmpty(name) ? null : Profiles.FirstOrDefault(profile => NameComparer.Equals(profile.Name, name));

    public VideoSdrToneMappingOptions? GetImageOptions() => Find(ImageProfile)?.Options;

    public VideoSdrToneMappingOptions? GetVideoOptions() => Find(VideoProfile)?.Options;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Profiles);
        var names = new HashSet<string>(NameComparer);
        foreach (ToneMappingProfile profile in Profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim() != profile.Name)
                throw new ArgumentException("Tone mapping profile names must be non-empty and trimmed.", nameof(Profiles));
            if (!names.Add(profile.Name))
                throw new ArgumentException($"Duplicate tone mapping profile name '{profile.Name}'.", nameof(Profiles));
            ArgumentNullException.ThrowIfNull(profile.Options);
            profile.Options.Validate();
        }
    }
}
