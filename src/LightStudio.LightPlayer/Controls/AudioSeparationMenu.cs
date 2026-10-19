using System.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using LightStudio.LightPlayer.Services;
using LightStudio.LightPlayer.Strings;
using LightStudio.LightPlayer.ViewModels;

namespace LightStudio.LightPlayer.Controls;

internal static class AudioSeparationMenu
{
    private static readonly FuncDataTemplate<string> ModelNameTemplate = new((name, scope) => new TextBlock
    {
        Text = name,
        TextTrimming = TextTrimming.CharacterEllipsis,
    });

    public static void Populate(IList items, PlaybackBarViewModel? playback)
    {
        items.Clear();
        if (playback is null)
            return;

        foreach (var track in new[]
        {
            (Name: Resources.AudioSeparation_Original, Track: AudioSeparationTrack.Original, IsSelected: playback.IsOriginalAudio),
            (Name: Resources.AudioSeparation_Track1, Track: AudioSeparationTrack.First, IsSelected: playback.IsSeparatedTrack1),
            (Name: Resources.AudioSeparation_Track2, Track: AudioSeparationTrack.Second, IsSelected: playback.IsSeparatedTrack2),
        })
        {
            items.Add(new MenuItem
            {
                Header = track.Name,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "AudioSeparationTracks",
                IsChecked = track.IsSelected,
                Command = playback.SelectAudioTrackCommand,
                CommandParameter = track.Track,
            });
        }

        if (playback.AudioSeparationModels.Count == 0)
            return;

        items.Add(new Separator());
        foreach (var model in playback.AudioSeparationModels)
        {
            var item = new MenuItem
            {
                Header = model.Name,
                HeaderTemplate = ModelNameTemplate,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "AudioSeparationModels",
                IsChecked = model.Id == playback.AudioSeparationModelId,
                Command = playback.SelectAudioSeparationModelCommand,
                CommandParameter = model.Id,
            };
            ToolTip.SetTip(item, model.Name);
            items.Add(item);
        }
    }
}