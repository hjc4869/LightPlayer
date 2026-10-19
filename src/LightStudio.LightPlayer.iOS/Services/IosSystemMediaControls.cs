using System;
using System.Collections.Generic;
using AVFoundation;
using Avalonia.Threading;
using Foundation;
using LightStudio.LightPlayer.Services.Playback;
using MediaPlayer;
using UIKit;

namespace LightStudio.LightPlayer.iOS.Services;

internal sealed class IosSystemMediaControls : ISystemMediaControls, IDisposable
{
    private readonly MPNowPlayingInfo info = new();
    private readonly List<(MPRemoteCommand Command, NSObject Target)> targets = [];
    private readonly NSObject interruption;
    private readonly NSObject routeChange;
    private UIImage? artwork;
    private MPMediaItemArtwork? mediaArtwork;
    private bool playing;
    private bool hasItem;
    private bool disposed;

    public IosSystemMediaControls()
    {
        var commands = MPRemoteCommandCenter.Shared;
        Add(commands.TogglePlayPauseCommand, _ => PlayPauseRequested?.Invoke());
        Add(commands.PlayCommand, _ => { if (!playing) PlayPauseRequested?.Invoke(); });
        Add(commands.PauseCommand, _ => { if (playing) PlayPauseRequested?.Invoke(); });
        Add(commands.NextTrackCommand, _ => NextRequested?.Invoke());
        Add(commands.PreviousTrackCommand, _ => PreviousRequested?.Invoke());
        Add(commands.ChangePlaybackPositionCommand, command =>
        {
            if (command is MPChangePlaybackPositionCommandEvent position)
                SeekRequested?.Invoke(TimeSpan.FromSeconds(position.PositionTime));
        });
        interruption = AVAudioSession.Notifications.ObserveInterruption((_, args) =>
        {
            if (args.InterruptionType == AVAudioSessionInterruptionType.Began)
                Dispatch(() => { if (playing) PlayPauseRequested?.Invoke(); });
        });
        routeChange = AVAudioSession.Notifications.ObserveRouteChange((_, args) =>
        {
            if (args.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
                Dispatch(() => { if (playing) PlayPauseRequested?.Invoke(); });
        });
    }

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action<TimeSpan>? SeekRequested;

    public void SetNowPlaying(string title, string artist, string album) => Dispatch(() =>
    {
        info.Title = title;
        info.Artist = artist;
        info.AlbumTitle = album;
        Update();
    });

    public void SetPlaybackState(bool isPlaying, bool hasItem) => Dispatch(() =>
    {
        playing = isPlaying;
        this.hasItem = hasItem;
        info.PlaybackRate = isPlaying ? 1 : 0;
        foreach (var (command, _) in targets) command.Enabled = hasItem;
        Update();
    });

    public void SetArtwork(byte[]? artworkContent) => Dispatch(() =>
    {
        info.Artwork = null;
        mediaArtwork?.Dispose();
        artwork?.Dispose();
        mediaArtwork = null;
        artwork = null;
        if (artworkContent is { Length: > 0 })
        {
            using var data = NSData.FromArray(artworkContent);
            artwork = UIImage.LoadFromData(data);
            if (artwork is { } image)
                info.Artwork = mediaArtwork = new MPMediaItemArtwork(image.Size, _ => image);
        }
        Update();
    });

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration) => Dispatch(() =>
    {
        info.ElapsedPlaybackTime = position.TotalSeconds;
        info.PlaybackDuration = duration.TotalSeconds;
        MPRemoteCommandCenter.Shared.ChangePlaybackPositionCommand.Enabled = hasItem && duration > TimeSpan.Zero;
        Update();
    });

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        interruption.Dispose();
        routeChange.Dispose();
        foreach (var (command, target) in targets)
        {
            command.RemoveTarget(target);
            target.Dispose();
        }
        targets.Clear();
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = null!;
        mediaArtwork?.Dispose();
        artwork?.Dispose();
    }

    private void Add(MPRemoteCommand command, Action<MPRemoteCommandEvent> action)
    {
        var target = command.AddTarget(args =>
        {
            Dispatch(() => action(args));
            return MPRemoteCommandHandlerStatus.Success;
        });
        targets.Add((command, target));
    }

    private void Dispatch(Action action) => Dispatcher.UIThread.Post(() =>
    {
        if (!disposed) action();
    });

    private void Update() => MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = hasItem ? info : null!;
}