using System.Collections.Generic;
using LightStudio.LightPlayer.Services;
using LightStudio.MediaLibraryCore.Database.Entities;

namespace LightStudio.LightPlayer.Models;

/// <summary>
/// Navigation parameter for the video playback page: the video to play plus the list it was picked
/// from, which becomes that page's own list. Never touches the music queue.
/// </summary>
public sealed record VideoPlaybackRequest(
	Sourced<DbVideoFile> Video,
	IReadOnlyList<Sourced<DbVideoFile>> Siblings);
