using LightMediaRenderer.Avalonia.Audio;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Models;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Services.Playback;

public sealed class PlaybackQueueController : IDisposable
{
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromMilliseconds(15);
    private static readonly TimeSpan BackendErrorRepeatInterval = TimeSpan.FromSeconds(5);

    private readonly Random random = new();
    private readonly ObservableCollection<PlaybackQueueItem> items = new();
    private readonly object playbackGate = new();
    private readonly object statusGate = new();
    private readonly object backendErrorGate = new();
    private readonly IClientMediaResolver mediaResolver;
    private readonly IAudioSeparationService? audioSeparation;
    private bool separationActive;
    private List<PlaybackQueueItem>? sequentialBackup;
    private Channel<PlaybackControlCommand>? activeControls;
    private CancellationTokenSource? activeTrackInitialization;
    private PlaybackMode mode = PlaybackMode.Sequential;
    private PlaybackQueuePlaybackState playbackState = PlaybackQueuePlaybackState.Stopped;
    private bool isInitializing;
    private int currentIndex = -1;
    private float volume = 1.0f;
    private bool paused;
    private TimeSpan sourcePosition;
    private TimeSpan playedDuration;
    private TimeSpan bufferedDuration;
    private TimeSpan pendingStartPosition;
    private string? lastBackendErrorMessage;
    private long lastBackendErrorTimestamp;
    private bool disposed;

    public PlaybackQueueController(IClientMediaResolver mediaResolver, IAudioSeparationService? audioSeparation = null)
    {
        this.mediaResolver = mediaResolver ?? throw new ArgumentNullException(nameof(mediaResolver));
        this.audioSeparation = audioSeparation;
        if (PlatformServices.PcmPlaybackSourceFactory is { } factory)
            PcmSourceFactory = request => factory(request, mediaResolver);
    }

    public event EventHandler<PlaybackQueueStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PlaybackQueueStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<PlaybackQueueTrackStartedEventArgs>? TrackStarted;

    public event EventHandler<PlaybackQueueTrackEndedEventArgs>? TrackEnded;

    public event EventHandler<PlaybackQueueErrorEventArgs>? ErrorOccurred;

    public event EventHandler<string>? BackendError;

    public event EventHandler<string>? Underrun;

    /// <summary>Raised when the audio backend adopts a volume set outside the app (e.g. the system mixer).</summary>
    public event EventHandler<float>? VolumeChanged;

    public ObservableCollection<PlaybackQueueItem> Items => items;

    public PlaybackQueueItem? Current => IsValidIndex(currentIndex) ? items[currentIndex] : null;

    public int CurrentIndex => IsValidIndex(currentIndex) ? currentIndex : -1;

    public bool HasItems => items.Count > 0;

    public PlaybackQueueStatus Status => CreateStatus();

    public float Volume
    {
        get
        {
            lock (statusGate)
            {
                return volume;
            }
        }
    }

    /// <summary>
    /// Resolves the live system mixer rate. When set, the reused audio output device is recreated
    /// at a track boundary once the system output device starts running at a different rate, so
    /// playback follows the new device instead of leaving the audio server to resample a stale rate.
    /// </summary>
    public ISystemSampleRateProvider? SystemSampleRateProvider { get; set; }

    public Func<PcmPlaybackRequest, IPcmPlaybackSource>? PcmSourceFactory { get; set; }

    private bool alwaysResample;
    private int preferredSampleRate;

    /// <summary>
    /// Live master switch for resampling, re-read at every track boundary so toggling it mid-playback
    /// takes effect on the next track. Mirrors <c>PlaybackController.AlwaysResample</c>.
    /// </summary>
    public bool AlwaysResample
    {
        get => Volatile.Read(ref alwaysResample);
        set => Volatile.Write(ref alwaysResample, value);
    }

    /// <summary>
    /// Live preferred output rate in Hz (0 follows the system mixer rate), re-read at every track
    /// boundary. Mirrors <c>PlaybackController.PreferredSampleRate</c>.
    /// </summary>
    public int PreferredSampleRate
    {
        get => Volatile.Read(ref preferredSampleRate);
        set => Volatile.Write(ref preferredSampleRate, Math.Max(0, value));
    }

    public PlaybackMode Mode
    {
        get => mode;
        set
        {
            ThrowIfDisposed();
            if (SetModeCore(value))
            {
                OnStatusChanged("Mode changed");
            }
        }
    }

    public static QueuePlaybackOptions CreateOptions(TimeSpan? durationPerTrack, float volume, int preferredSampleRate = 0, bool alwaysResample = false)
    {
        return new QueuePlaybackOptions(durationPerTrack, Math.Clamp(volume, 0.0f, 1.0f), DefaultBufferTarget, Math.Max(0, preferredSampleRate), alwaysResample);
    }

    public void AddRange(IEnumerable<string> filePaths)
    {
        AddRange(filePaths.Select(path => new PlaybackQueueItem(path)));
    }

    public void AddRange(IEnumerable<PlaybackQueueItem> queueItems)
    {
        ThrowIfDisposed();

        var newItems = queueItems.ToList();
        if (newItems.Count == 0)
        {
            return;
        }

        if (mode == PlaybackMode.Random)
        {
            sequentialBackup ??= items.ToList();
            sequentialBackup.AddRange(newItems);
            foreach (var item in Shuffle(newItems))
            {
                items.Add(item);
            }
        }
        else
        {
            foreach (var item in newItems)
            {
                items.Add(item);
            }
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        OnStatusChanged("Queue changed");
    }

    public void InsertRange(int index, IEnumerable<string> filePaths)
    {
        InsertRange(index, filePaths.Select(path => new PlaybackQueueItem(path)));
    }

    public void InsertRange(int index, IEnumerable<PlaybackQueueItem> queueItems) =>
        InsertRange(index, queueItems, preserveOrder: false);

    public void InsertRange(int index, IEnumerable<PlaybackQueueItem> queueItems, bool preserveOrder)
    {
        ThrowIfDisposed();

        var newItems = queueItems.ToList();
        if (newItems.Count == 0)
        {
            return;
        }

        var insertAt = Math.Clamp(index, 0, items.Count);

        if (mode == PlaybackMode.Random)
        {
            sequentialBackup ??= items.ToList();
            sequentialBackup.AddRange(newItems);
            var insertedItems = preserveOrder ? newItems : Shuffle(newItems).ToList();
            for (var itemIndex = 0; itemIndex < insertedItems.Count; itemIndex++)
            {
                items.Insert(insertAt + itemIndex, insertedItems[itemIndex]);
            }
        }
        else
        {
            for (var i = 0; i < newItems.Count; i++)
            {
                items.Insert(insertAt + i, newItems[i]);
            }
        }

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }
        else if (insertAt <= currentIndex)
        {
            currentIndex += newItems.Count;
        }

        OnStatusChanged("Queue changed");
    }

    public void Clear()
    {
        ThrowIfDisposed();

        Stop();
        items.Clear();
        sequentialBackup?.Clear();
        sequentialBackup = null;
        currentIndex = -1;
        pendingStartPosition = TimeSpan.Zero;
        OnStatusChanged("Queue cleared");
    }

    public bool SetIndex(int index)
    {
        ThrowIfDisposed();

        if (!SetIndexCore(index, clearPendingPosition: true))
        {
            return false;
        }

        OnStatusChanged("Track selected");
        return true;
    }

    public bool PlayAt(int index)
    {
        ThrowIfDisposed();

        if (!IsValidIndex(index))
        {
            return false;
        }

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.PlayAt, Index: index)) || SetIndex(index);
    }

    public bool MoveNext()
    {
        ThrowIfDisposed();

        if (!MoveNextCore())
        {
            return false;
        }

        OnStatusChanged("Track selected");
        return true;
    }

    public bool MovePrevious()
    {
        ThrowIfDisposed();

        if (!MovePreviousCore())
        {
            return false;
        }

        OnStatusChanged("Track selected");
        return true;
    }

    public bool Next()
    {
        ThrowIfDisposed();

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Next)) || MoveNext();
    }

    public bool Previous()
    {
        ThrowIfDisposed();

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Previous)) || MovePrevious();
    }

    public bool TogglePause()
    {
        ThrowIfDisposed();

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.TogglePause));
    }

    public bool Pause()
    {
        ThrowIfDisposed();

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Pause));
    }

    public bool Resume()
    {
        ThrowIfDisposed();

        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Resume));
    }

    public bool Stop()
    {
        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Stop));
    }

    public bool RestartAudio()
    {
        if (!Volatile.Read(ref separationActive) &&
            audioSeparation is not { Status.IsAvailable: true, Track: not AudioSeparationTrack.Original })
            return false;
        var current = Status;
        var position = current.SourcePosition - current.BufferedDuration;
        return TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.RestartAudio,
            Position: position > TimeSpan.Zero ? position : TimeSpan.Zero));
    }

    public bool Seek(TimeSpan position)
    {
        ThrowIfDisposed();

        if (position < TimeSpan.Zero)
        {
            return false;
        }

        if (TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.Seek, Position: position)))
        {
            return true;
        }

        if (!HasItems)
        {
            return false;
        }

        pendingStartPosition = position;
        UpdateRuntimeStatus(position, TimeSpan.Zero, TimeSpan.Zero);
        OnStatusChanged($"Position set: {position}");
        return true;
    }

    public bool SetVolume(float value)
    {
        ThrowIfDisposed();

        SetVolumeCore(value);
        TryPostControl(new PlaybackControlCommand(PlaybackControlCommandType.SetVolume, Volume: Volume));
        OnStatusChanged($"Volume: {Volume.ToString(CultureInfo.InvariantCulture)}");
        return true;
    }

    public PlaybackMode CycleMode()
    {
        Mode = mode switch
        {
            PlaybackMode.Sequential => PlaybackMode.ListLoop,
            PlaybackMode.ListLoop => PlaybackMode.SingleTrackLoop,
            PlaybackMode.SingleTrackLoop => PlaybackMode.Random,
            PlaybackMode.Random => PlaybackMode.Sequential,
            _ => PlaybackMode.Sequential
        };
        return Mode;
    }

    public bool RemoveAt(int index)
    {
        ThrowIfDisposed();

        if (!IsValidIndex(index))
        {
            return false;
        }

        var item = items[index];
        items.RemoveAt(index);
        sequentialBackup?.Remove(item);

        if (items.Count == 0)
        {
            currentIndex = -1;
        }
        else if (index < currentIndex || currentIndex >= items.Count)
        {
            currentIndex = Math.Max(0, currentIndex - 1);
        }

        OnStatusChanged("Queue changed");
        return true;
    }

    /// <summary>
    /// Reorders the queue, moving the items at <paramref name="sourceIndices"/> so they sit
    /// (in their existing relative order) at <paramref name="targetIndex"/> in the current
    /// list. The currently playing track is preserved by reference, so playback continues
    /// uninterrupted -- this only rewrites the queue order and never posts a control command.
    /// </summary>
    public bool Move(IReadOnlyList<int> sourceIndices, int targetIndex)
    {
        ThrowIfDisposed();

        if (sourceIndices is null || sourceIndices.Count == 0)
        {
            return false;
        }

        var moveSet = new HashSet<int>();
        foreach (var index in sourceIndices)
        {
            if (IsValidIndex(index))
            {
                moveSet.Add(index);
            }
        }

        if (moveSet.Count == 0 || moveSet.Count == items.Count)
        {
            // Nothing to move, or every row selected (order would be unchanged).
            return false;
        }

        var current = Current;

        // The drop lands before the first not-moved row at/after the target; if every row
        // from there on is being moved, the block goes to the end.
        PlaybackQueueItem? anchor = null;
        for (var k = Math.Clamp(targetIndex, 0, items.Count); k < items.Count; k++)
        {
            if (!moveSet.Contains(k))
            {
                anchor = items[k];
                break;
            }
        }

        var moved = new List<PlaybackQueueItem>(moveSet.Count);
        var remaining = new List<PlaybackQueueItem>(items.Count - moveSet.Count);
        for (var i = 0; i < items.Count; i++)
        {
            (moveSet.Contains(i) ? moved : remaining).Add(items[i]);
        }

        var insertPos = anchor is null ? remaining.Count : remaining.IndexOf(anchor);
        if (insertPos < 0)
        {
            insertPos = remaining.Count;
        }

        items.Clear();
        for (var i = 0; i < insertPos; i++)
        {
            items.Add(remaining[i]);
        }

        foreach (var item in moved)
        {
            items.Add(item);
        }

        for (var i = insertPos; i < remaining.Count; i++)
        {
            items.Add(remaining[i]);
        }

        // Keep the index pinned to the same playing item so the decode loop reads the same track.
        if (current is not null)
        {
            currentIndex = items.IndexOf(current);
        }

        OnStatusChanged("Queue reordered");
        return true;
    }

    public async Task<QueuePlaybackSummary> PlayAsync(QueuePlaybackOptions options, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var controls = Channel.CreateUnbounded<PlaybackControlCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (playbackGate)
        {
            if (activeControls is not null)
            {
                throw new InvalidOperationException("Playback is already running.");
            }

            activeControls = controls;
        }

        SetVolumeCore(options.Volume);
        // Seed the live resample settings from the options this run was started with; the controller
        // keeps them in sync afterwards so a change applies at the next track boundary.
        Volatile.Write(ref alwaysResample, options.AlwaysResample);
        Volatile.Write(ref preferredSampleRate, Math.Max(0, options.PreferredSampleRate));
        ResetRuntimeStatus();
        paused = false;
        var summary = new QueuePlaybackSummary();

        try
        {
            OnStatusChanged("Queue loaded");
            return await RunPlaybackLoopAsync(options, controls.Reader, summary, cancellationToken);
        }
        finally
        {
            controls.Writer.TryComplete();
            lock (playbackGate)
            {
                if (ReferenceEquals(activeControls, controls))
                {
                    activeControls = null;
                }
            }
        }
    }

    public PlaybackQueueSnapshot CreateSnapshot()
    {
        return CreateSnapshot(Volume, sourcePosition);
    }

    public PlaybackQueueSnapshot CreateSnapshot(float snapshotVolume, TimeSpan position)
    {
        var orderedItems = sequentialBackup ?? items.ToList();
        var current = Current;
        var snapshotIndex = current is null ? -1 : orderedItems.IndexOf(current);
        return new PlaybackQueueSnapshot(
            orderedItems.ToArray(),
            snapshotIndex,
            mode,
            snapshotVolume,
            position);
    }

    public void Restore(PlaybackQueueSnapshot snapshot)
    {
        ThrowIfDisposed();

        Clear();
        mode = PlaybackMode.Sequential;
        AddRange(snapshot.Items);
        if (snapshot.CurrentIndex >= 0 && snapshot.CurrentIndex < items.Count)
        {
            currentIndex = snapshot.CurrentIndex;
        }

        pendingStartPosition = snapshot.Position;
        SetVolume(snapshot.Volume);
        Mode = snapshot.Mode;

        Console.WriteLine(
            $"[Playback] Core.Restore: files={snapshot.Items.Count} requestedIndex={snapshot.CurrentIndex} " +
            $"resolvedIndex={currentIndex} pendingStartPosition={pendingStartPosition} mode={Mode}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        Stop();
        lock (playbackGate)
        {
            activeControls?.Writer.TryComplete();
            activeControls = null;
        }

        disposed = true;
    }

    private async Task<QueuePlaybackSummary> RunPlaybackLoopAsync(
        QueuePlaybackOptions options,
        ChannelReader<PlaybackControlCommand> controls,
        QueuePlaybackSummary summary,
        CancellationToken cancellationToken)
    {
        await using var outputContext = new PlaybackOutputContext();

        // Index where the current run of tracks that produced no audio began. When a track
        // fails to open/decode (throws) or decodes nothing, it is treated as ended and the loop
        // advances. In looping modes (SingleTrackLoop / ListLoop / Random) MoveNextCore never
        // reports the end of the queue, so an entirely unplayable queue would otherwise re-open
        // the same failing track indefinitely, spinning the CPU and flooding the UI with the same
        // error. Tracking this lets the loop detect when the queue has cycled a full turn without
        // playing anything and stop instead of looping forever. -1 means "no failing run active".
        var failureStreakStart = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = Current;
            if (current is null)
            {
                OnStatusChanged("Queue is empty.");
                break;
            }

            var decodedBytesBefore = summary.DecodedBytes;
            PlaybackTrackOutcome outcome;
            using var initialization = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetActiveTrackInitialization(initialization);
            try
            {
                SetInitializing(true);
                outcome = await HandleControlsBeforeTrackAsync(controls, outputContext.Output)
                    ?? await PlayCurrentTrackAsync(current, options, controls, summary, outputContext, initialization);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception) when (
                !cancellationToken.IsCancellationRequested && initialization.IsCancellationRequested)
            {
                outcome = await HandleControlsBeforeTrackAsync(controls, outputContext.Output)
                    ?? PlaybackTrackOutcome.Restart;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.Failures++;
                AppLog.Error("Audio.Playback", $"Could not play {current.FilePath}.", ex);
                OnError(current.FilePath, ex.GetBaseException().Message);
                outcome = PlaybackTrackOutcome.TrackEnded;
            }
            finally
            {
                ClearActiveTrackInitialization(initialization);
                SetInitializing(false);
            }

            // The track made forward progress only if it actually decoded some audio; both a
            // thrown open/decode failure and a track that yields no samples leave this unchanged.
            var madeProgress = summary.DecodedBytes > decodedBytesBefore;

            switch (outcome)
            {
                case PlaybackTrackOutcome.Quit:
                    SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Queue playback stopped.");
                    return summary;
                case PlaybackTrackOutcome.Previous:
                    failureStreakStart = -1;
                    if (!MovePreviousCore())
                    {
                        OnStatusChanged("Already at the first track.");
                    }
                    continue;
                case PlaybackTrackOutcome.Restart:
                    failureStreakStart = -1;
                    continue;
                case PlaybackTrackOutcome.Next:
                case PlaybackTrackOutcome.TrackEnded:
                    // A user-issued Next is deliberate queue navigation, never a stuck auto-loop,
                    // so it clears the failing run. Otherwise remember the first track of a run of
                    // non-producing tracks so a full cycle back to it can be detected below.
                    if (outcome == PlaybackTrackOutcome.Next || madeProgress)
                    {
                        failureStreakStart = -1;
                    }
                    else if (failureStreakStart < 0)
                    {
                        failureStreakStart = currentIndex;
                    }

                    if (!MoveNextCore())
                    {
                        OnStatusChanged("End of queue.");
                        SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Queue playback stopped.");
                        return summary;
                    }

                    if (failureStreakStart >= 0 && !madeProgress && currentIndex == failureStreakStart)
                    {
                        // Cycled all the way around a looping queue without decoding any audio.
                        SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Playback stopped: no playable tracks in the queue.");
                        return summary;
                    }
                    continue;
            }
        }

        SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Queue playback stopped.");
        return summary;
    }

    private async Task<PlaybackTrackOutcome> PlayCurrentTrackAsync(
        PlaybackQueueItem item,
        QueuePlaybackOptions options,
        ChannelReader<PlaybackControlCommand> controls,
        QueuePlaybackSummary summary,
        PlaybackOutputContext outputContext,
        CancellationTokenSource initialization)
    {
        var cancellationToken = initialization.Token;
        var requestedTrackPosition = ConsumePendingStartPosition();

        // For a CUE track the file is decoded from the track's start offset; the requested
        // position (seek/restore) is relative to the track, so it is added on top. Plain files
        // treat the requested position as an absolute file offset.
        var isCue = item.IsCueTrack;
        var trackStart = isCue ? item.StartTime : TimeSpan.Zero;
        var fileStartPosition = trackStart + requestedTrackPosition;

        // FilePath is a platform-owned media identifier: a local path, a remote server path, or a
        // content URI. Pass it through unchanged so the active platform I/O implementation can
        // interpret it in the correct filesystem context.
        // Resolve the decode/output target live for this track: a fixed rate when "Always resample"
        // is on, otherwise the track's own native rate (a null target lets the decoder keep the
        // source rate). EnsureOutput then recreates the device only when this rate actually changes.
        var sourceFactory = PcmSourceFactory;
        var platform = sourceFactory is null
            ? await mediaResolver.ResolvePlatformAsync(new MediaReference(item.SourceId, item.FilePath), cancellationToken).ConfigureAwait(false)
            : null;
        using var stream = platform is not null
            ? await platform.OpenMediaFileAsync(item.FilePath, cancellationToken).ConfigureAwait(false)
            : null;
        var hasCueEnd = isCue && item.Duration > TimeSpan.Zero;
        var cueEndPosition = trackStart + item.Duration;
        await using IPcmPlaybackSource source = sourceFactory?.Invoke(new PcmPlaybackRequest(
            item, fileStartPosition, hasCueEnd ? cueEndPosition : null, AlwaysResample, PreferredSampleRate))
            ?? InProcessPcmPlaybackSource.ForStream(stream!, fileStartPosition, hasCueEnd ? cueEndPosition : null,
                ResolveOutputSampleRate, audioSeparation);
        await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref separationActive, source.UsesAudioSeparation);
        ClearActiveTrackInitialization(initialization);
        var output = await EnsureOutputAsync(outputContext, source.Format, options.BufferTarget, cancellationToken);
        SetInitializing(false);

        // A CUE track's playable length is its declared duration; the final track of a sheet
        // may have an unknown (zero) duration, meaning "play to the end of the file".
        var trackDuration = isCue
            ? (item.Duration > TimeSpan.Zero
                ? item.Duration
                : (source.Duration > trackStart ? source.Duration - trackStart : TimeSpan.Zero))
            : source.Duration;

        // Track-relative position for status/UI so a CUE track's progress runs 0..trackDuration.
        TimeSpan ToTrackRelative(TimeSpan filePosition) =>
            isCue ? (filePosition > trackStart ? filePosition - trackStart : TimeSpan.Zero) : filePosition;

        var state = new TrackPlaybackState();
        var sourceGate = new object();
        var stopwatch = Stopwatch.StartNew();
        var durationLimit = options.DurationPerTrack ?? TimeSpan.MaxValue;

        AudioOutputReadResult CompleteOutput() =>
            CanContinueAfterCurrent()
                ? AudioOutputReadResult.CompleteWithNextSource
                : AudioOutputReadResult.Complete;

        AudioOutputReadResult PullAudio(TimeSpan _)
        {
            lock (sourceGate)
            {
                if (state.SourceEnded || state.DecodedDuration >= durationLimit)
                {
                    state.SourceEnded = true;
                    return CompleteOutput();
                }

                // Only track and CUE boundaries may truncate a decoded frame. The backend's
                // requested duration is a fill hint; using it here would discard the rest of
                // every codec frame that crosses the backend's buffer target.
                var maxFrame = durationLimit - state.DecodedDuration;
                if (hasCueEnd)
                {
                    var remainingToCueEnd = cueEndPosition - source.Position;
                    if (remainingToCueEnd < maxFrame)
                    {
                        maxFrame = remainingToCueEnd;
                    }
                }

                AudioOutputReadResult frame;
                try
                {
                    if (!source.TryRead(maxFrame, out frame))
                        return AudioOutputReadResult.FromBuffer(ReadOnlyMemory<byte>.Empty, TimeSpan.Zero);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return AudioOutputReadResult.Complete;
                }
                if (frame.EndOfStream)
                {
                    state.SourceEnded = true;
                    Console.WriteLine(
                        $"[Playback] Source ended: '{item.DisplayName}' started={state.Started} " +
                        $"decoded={state.DecodedDuration} sourcePosition={source.Position} duration={source.Duration} " +
                        $"frames={state.FrameCount} lastError={source.LastError}");
                    return CompleteOutput();
                }

                state.DecodedDuration += frame.Duration;
                state.DecodedBytes += frame.ByteLength;
                state.FrameCount++;
                return frame;
            }
        }

        summary.TracksStarted++;
        Console.WriteLine(
            $"[Playback] Track start: index={CurrentIndex} '{item.DisplayName}' isCue={isCue} " +
            $"trackStart={trackStart} requestedTrackPos={requestedTrackPosition} actualPosition={source.Position} " +
            $"trackDuration={trackDuration} fileDuration={source.Duration}");
        if (fileStartPosition > source.Duration && source.Duration > TimeSpan.Zero)
        {
            Console.WriteLine(
                $"[Playback] WARNING: requested start {fileStartPosition} is past track duration {source.Duration}; track may end immediately.");
        }

        UpdateRuntimeStatus(ToTrackRelative(source.Position), TimeSpan.Zero, TimeSpan.Zero);
        var startedTitle = isCue && !string.IsNullOrWhiteSpace(item.Title)
            ? item.Title!
            : source.Title ?? item.DisplayName;
        TrackStarted?.Invoke(this, new PlaybackQueueTrackStartedEventArgs(item, CurrentIndex, items.Count, startedTitle, trackDuration, Mode, Volume));

        bool keepOutputQueued = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (controls.TryRead(out var control))
                {
                    var outcome = await HandleControlAsync(control, source, output, state, sourceGate, trackStart, cancellationToken);
                    if (outcome.HasValue)
                    {
                        stopwatch.Stop();
                        return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, outcome.Value, keepOutputQueued: false);
                    }
                }

                output.Volume = Volume;

                if (!state.OutputStarted && !paused)
                {
                    await output.StartAsync(PullAudio, cancellationToken);
                    state.OutputStarted = true;
                }

                if (paused)
                {
                    lock (sourceGate)
                    {
                        UpdateRuntimeStatus(ToTrackRelative(source.Position), output.PlayedDuration, output.BufferedDuration);
                    }
                    await Task.Delay(LoopDelay, cancellationToken);
                    continue;
                }

                bool sourceEnded;
                TimeSpan decodedDuration;
                lock (sourceGate)
                {
                    sourceEnded = state.SourceEnded || output.SourceExhausted;
                    decodedDuration = state.DecodedDuration;
                    UpdateRuntimeStatus(ToTrackRelative(source.Position), output.PlayedDuration, output.BufferedDuration);
                }

                if (!state.Started && state.DecodedBytes > 0)
                {
                    state.Started = true;
                    SetPlaybackState(PlaybackQueuePlaybackState.Playing, "Playback started.");
                }

                if (sourceEnded || decodedDuration >= durationLimit)
                {
                    var canContinue = CanContinueAfterCurrent();
                    if (canContinue || output.IsDrained)
                    {
                        keepOutputQueued = canContinue;
                        stopwatch.Stop();
                        return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, PlaybackTrackOutcome.TrackEnded, keepOutputQueued: canContinue);
                    }
                }

                await Task.Delay(LoopDelay, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (!keepOutputQueued)
            {
                await output.ResetAfterSeekAsync();
                lock (sourceGate)
                    state.SourceEnded = true;
            }
        }

        stopwatch.Stop();
        return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, PlaybackTrackOutcome.Quit, keepOutputQueued: false);
    }

    private async Task<IAudioOutputDevice> EnsureOutputAsync(
        PlaybackOutputContext outputContext, AudioFormat format, TimeSpan bufferTarget, CancellationToken cancellationToken)
    {
        var existing = outputContext.Output;
        if (existing is not null)
        {
            // Reuse the open device across tracks so same-rate transitions stay gapless. Recreate it
            // once, at this track boundary, when the target output rate changed: a resample-setting
            // change, a system mixer rate change in follow mode, or (in follow-source mode) a new
            // track whose native rate differs from the current one.
            if (outputContext.Format is not { } currentFormat || currentFormat.SampleRate == format.SampleRate)
            {
                return existing;
            }

            Console.WriteLine(
                $"[Playback] Output rate changed {currentFormat.SampleRate} Hz -> {format.SampleRate} Hz; " +
                "recreating audio output device to renegotiate.");
            await existing.DisposeAsync();
            outputContext.Output = null;
        }

        var output = await AudioOutputDeviceFactory.CreateAsync(format, bufferTarget, cancellationToken);
        output.Volume = Volume;
        output.BackendError += (_, error) => ThreadPool.QueueUserWorkItem(
            static state => state.Controller.PublishBackendError(state.Error),
            (Controller: this, Error: error),
            preferLocal: false);
        output.Underrun += (_, message) => Underrun?.Invoke(this, message);
        output.VolumeChanged += OnOutputVolumeChanged;
        outputContext.Output = output;
        outputContext.Format = format;
        return output;
    }

    private void PublishBackendError(AudioOutputError error)
    {
        lock (backendErrorGate)
        {
            long now = Environment.TickCount64;
            if (string.Equals(lastBackendErrorMessage, error.Message, StringComparison.Ordinal) &&
                now - lastBackendErrorTimestamp < BackendErrorRepeatInterval.TotalMilliseconds)
            {
                return;
            }

            lastBackendErrorMessage = error.Message;
            lastBackendErrorTimestamp = now;
        }

        AppLog.Warning("Audio.Output", error.Message, error.Cause);
        BackendError?.Invoke(this, error.Message);
    }

    private int ResolveOutputSampleRate()
    {
        if (!AlwaysResample)
        {
            return 0;
        }

        var preferred = PreferredSampleRate;
        return preferred > 0
            ? preferred
            : SystemSampleRateProvider?.GetSystemSampleRate() ?? 0;
    }

    private PlaybackTrackOutcome CompleteTrack(
        QueuePlaybackSummary summary,
        PlaybackQueueItem item,
        TrackPlaybackState state,
        IAudioOutputDevice output,
        TimeSpan elapsed,
        PlaybackTrackOutcome outcome,
        bool keepOutputQueued)
    {
        var played = outcome == PlaybackTrackOutcome.TrackEnded
            ? state.DecodedDuration
            : output.PlayedDuration;
        var underrunCount = output.UnderrunCount;
        UpdateRuntimeStatus(TimeSpan.Zero, played, TimeSpan.Zero);
        AddTrackSummary(summary, state, played, elapsed, underrunCount, outcome);
        Console.WriteLine(
            $"[Playback] Track end: '{item.DisplayName}' outcome={outcome} started={state.Started} " +
            $"played={played} decoded={state.DecodedDuration} elapsed={elapsed} keepOutputQueued={keepOutputQueued}");
        if (outcome == PlaybackTrackOutcome.TrackEnded && !state.Started)
        {
            Console.WriteLine(
                $"[Playback] NOTE: '{item.DisplayName}' ended before playback ever started (immediate skip to next track).");
        }
        TrackEnded?.Invoke(this, new PlaybackQueueTrackEndedEventArgs(item, CurrentIndex, items.Count, ToTrackEndReason(outcome), state.DecodedBytes, state.FrameCount, state.DecodedDuration, played, elapsed, underrunCount));
        return outcome;
    }

    private async Task<PlaybackTrackOutcome?> HandleControlsBeforeTrackAsync(
        ChannelReader<PlaybackControlCommand> controls,
        IAudioOutputDevice? output)
    {
        while (controls.TryRead(out var control))
        {
            switch (control.Type)
            {
                case PlaybackControlCommandType.TogglePause:
                    await SetPausedBeforeTrackAsync(!paused, output);
                    break;
                case PlaybackControlCommandType.Pause:
                    await SetPausedBeforeTrackAsync(true, output);
                    break;
                case PlaybackControlCommandType.Resume:
                    await SetPausedBeforeTrackAsync(false, output);
                    break;
                case PlaybackControlCommandType.Next:
                    if (output is not null)
                        await output.ResetAfterSeekAsync();
                    return PlaybackTrackOutcome.Next;
                case PlaybackControlCommandType.Previous:
                    if (output is not null)
                        await output.ResetAfterSeekAsync();
                    return PlaybackTrackOutcome.Previous;
                case PlaybackControlCommandType.Stop:
                    if (output is not null)
                        await output.ResetAfterSeekAsync();
                    return PlaybackTrackOutcome.Quit;
                case PlaybackControlCommandType.PlayAt:
                    if (!SetIndexCore(control.Index, clearPendingPosition: true))
                    {
                        OnStatusChanged($"Track number out of range: {control.Index + 1}");
                        break;
                    }

                    paused = false;
                    if (output is not null)
                        await output.ResetAfterSeekAsync();
                    OnStatusChanged("Track selected");
                    return PlaybackTrackOutcome.Restart;
                case PlaybackControlCommandType.Seek:
                case PlaybackControlCommandType.RestartAudio:
                    pendingStartPosition = control.Position < TimeSpan.Zero
                        ? TimeSpan.Zero
                        : control.Position;
                    UpdateRuntimeStatus(pendingStartPosition, TimeSpan.Zero, TimeSpan.Zero);
                    OnStatusChanged($"Position set: {pendingStartPosition}");
                    if (output is not null)
                        await output.ResetAfterSeekAsync();
                    return PlaybackTrackOutcome.Restart;
                case PlaybackControlCommandType.SetVolume:
                    SetVolumeCore(control.Volume);
                    if (output is not null)
                    {
                        output.Volume = Volume;
                    }
                    break;
            }
        }

        return null;
    }

    private async Task SetPausedBeforeTrackAsync(bool value, IAudioOutputDevice? output)
    {
        if (output is not null)
        {
            if (value)
            {
                await PauseOutputAsync(output);
            }
            else
            {
                await ResumeOutputAsync(output);
            }
            return;
        }

        if (paused == value)
        {
            return;
        }

        paused = value;
        SetPlaybackState(
            value ? PlaybackQueuePlaybackState.Paused : PlaybackQueuePlaybackState.Playing,
            value ? "Paused." : "Resumed.");
    }

    private async Task<PlaybackTrackOutcome?> HandleControlAsync(
        PlaybackControlCommand control,
        IPcmPlaybackSource source,
        IAudioOutputDevice output,
        TrackPlaybackState state,
        object sourceGate,
        TimeSpan trackStart,
        CancellationToken cancellationToken)
    {
        switch (control.Type)
        {
            case PlaybackControlCommandType.TogglePause:
                await TogglePauseOutputAsync(output);
                return null;
            case PlaybackControlCommandType.Pause:
                await PauseOutputAsync(output);
                return null;
            case PlaybackControlCommandType.Resume:
                await ResumeOutputAsync(output);
                return null;
            case PlaybackControlCommandType.Next:
                return PlaybackTrackOutcome.Next;
            case PlaybackControlCommandType.Previous:
                return PlaybackTrackOutcome.Previous;
            case PlaybackControlCommandType.Stop:
                return PlaybackTrackOutcome.Quit;
            case PlaybackControlCommandType.PlayAt:
                if (!SetIndexCore(control.Index, clearPendingPosition: true))
                {
                    OnStatusChanged($"Track number out of range: {control.Index + 1}");
                    return null;
                }

                // Explicitly choosing a track to play must start playback even when
                // currently paused; otherwise the new track is selected but stays silent.
                paused = false;
                OnStatusChanged("Track selected");
                return PlaybackTrackOutcome.Restart;
            case PlaybackControlCommandType.Seek:
                if (source.RequiresRestartOnSeek)
                {
                    pendingStartPosition = control.Position < TimeSpan.Zero ? TimeSpan.Zero : control.Position;
                    return PlaybackTrackOutcome.Restart;
                }
                // The requested position is relative to the (possibly CUE) track; translate it
                // to an absolute file offset before seeking, and report it back track-relative.
                var seekTarget = trackStart + (control.Position < TimeSpan.Zero ? TimeSpan.Zero : control.Position);
                await output.ResetAfterSeekAsync();
                TimeSpan actual = await source.SeekAsync(seekTarget, cancellationToken);
                TimeSpan relative;
                lock (sourceGate)
                {
                    state.ResetAfterSeek();
                    relative = source.Position > trackStart ? source.Position - trackStart : TimeSpan.Zero;
                }
                UpdateRuntimeStatus(relative, output.PlayedDuration, output.BufferedDuration);
                OnStatusChanged($"Seek requested {control.Position}, actual {actual}.");
                return null;
            case PlaybackControlCommandType.RestartAudio:
                pendingStartPosition = control.Position;
                return PlaybackTrackOutcome.Restart;
            case PlaybackControlCommandType.SetVolume:
                SetVolumeCore(control.Volume);
                output.Volume = Volume;
                return null;
            default:
                return null;
        }
    }

    private async Task TogglePauseOutputAsync(IAudioOutputDevice output)
    {
        if (paused)
        {
            await ResumeOutputAsync(output);
        }
        else
        {
            await PauseOutputAsync(output);
        }
    }

    private async Task PauseOutputAsync(IAudioOutputDevice output)
    {
        if (paused)
        {
            return;
        }

        paused = true;
        if (output.State == AudioOutputState.Playing)
        {
            await output.PauseAsync();
        }

        SetPlaybackState(PlaybackQueuePlaybackState.Paused, "Paused.");
    }

    private async Task ResumeOutputAsync(IAudioOutputDevice output)
    {
        if (!paused)
        {
            return;
        }

        paused = false;
        if (output.State == AudioOutputState.Paused)
        {
            await output.ResumeAsync();
        }

        SetPlaybackState(PlaybackQueuePlaybackState.Playing, "Resumed.");
    }

    private void AddTrackSummary(QueuePlaybackSummary summary, TrackPlaybackState state, TimeSpan played, TimeSpan elapsed, int underrunCount, PlaybackTrackOutcome outcome)
    {
        summary.DecodedBytes += state.DecodedBytes;
        summary.FrameCount += state.FrameCount;
        summary.DecodedDuration += state.DecodedDuration;
        summary.PlayedDuration += played;
        summary.Elapsed += elapsed;
        summary.UnderrunCount += underrunCount;

        switch (outcome)
        {
            case PlaybackTrackOutcome.TrackEnded when state.DecodedBytes > 0:
                summary.TracksCompleted++;
                break;
            case PlaybackTrackOutcome.TrackEnded:
                summary.Failures++;
                break;
            case PlaybackTrackOutcome.Next:
            case PlaybackTrackOutcome.Previous:
            case PlaybackTrackOutcome.Restart:
                summary.TracksSkipped++;
                break;
        }
    }

    private bool SetModeCore(PlaybackMode value)
    {
        if (mode == value)
        {
            return false;
        }

        if (value == PlaybackMode.Random)
        {
            EnableRandom();
        }
        else if (mode == PlaybackMode.Random)
        {
            DisableRandom(value);
        }
        else
        {
            mode = value;
        }

        return true;
    }

    private void EnableRandom()
    {
        sequentialBackup = items.ToList();
        var current = Current;
        var shuffled = Shuffle(sequentialBackup).ToList();

        items.Clear();
        if (current is not null)
        {
            shuffled.Remove(current);
            items.Add(current);
            currentIndex = 0;
        }

        foreach (var item in shuffled)
        {
            items.Add(item);
        }

        if (current is null && items.Count > 0)
        {
            currentIndex = 0;
        }

        mode = PlaybackMode.Random;
    }

    private void DisableRandom(PlaybackMode newMode)
    {
        var current = Current;
        var orderedItems = sequentialBackup ?? items.ToList();

        items.Clear();
        foreach (var item in orderedItems)
        {
            items.Add(item);
        }

        sequentialBackup = null;
        currentIndex = current is null ? (items.Count == 0 ? -1 : 0) : items.IndexOf(current);
        mode = newMode;
    }

    private bool SetIndexCore(int index, bool clearPendingPosition)
    {
        if (!IsValidIndex(index))
        {
            return false;
        }

        currentIndex = index;
        if (clearPendingPosition)
        {
            pendingStartPosition = TimeSpan.Zero;
        }

        return true;
    }

    private bool MoveNextCore()
    {
        pendingStartPosition = TimeSpan.Zero;
        if (items.Count == 0)
        {
            return false;
        }

        if (!IsValidIndex(currentIndex))
        {
            currentIndex = 0;
            return true;
        }

        switch (mode)
        {
            case PlaybackMode.SingleTrackLoop:
                return true;
            case PlaybackMode.ListLoop:
            case PlaybackMode.Random:
                currentIndex = currentIndex == items.Count - 1 ? 0 : currentIndex + 1;
                return true;
            case PlaybackMode.Sequential:
                if (currentIndex == items.Count - 1)
                {
                    return false;
                }

                currentIndex++;
                return true;
            default:
                return false;
        }
    }

    private bool MovePreviousCore()
    {
        pendingStartPosition = TimeSpan.Zero;
        if (items.Count == 0)
        {
            return false;
        }

        if (!IsValidIndex(currentIndex))
        {
            currentIndex = 0;
            return true;
        }

        switch (mode)
        {
            case PlaybackMode.SingleTrackLoop:
                return true;
            case PlaybackMode.ListLoop:
            case PlaybackMode.Random:
                currentIndex = currentIndex == 0 ? items.Count - 1 : currentIndex - 1;
                return true;
            case PlaybackMode.Sequential:
                if (currentIndex == 0)
                {
                    return false;
                }

                currentIndex--;
                return true;
            default:
                return false;
        }
    }

    private bool CanContinueAfterCurrent()
    {
        if (items.Count == 0 || !IsValidIndex(currentIndex))
        {
            return false;
        }

        return mode switch
        {
            PlaybackMode.SingleTrackLoop => true,
            PlaybackMode.ListLoop or PlaybackMode.Random => true,
            PlaybackMode.Sequential => currentIndex < items.Count - 1,
            _ => false
        };
    }

    private bool TryPostControl(PlaybackControlCommand command)
    {
        lock (playbackGate)
        {
            if (activeControls?.Writer.TryWrite(command) != true)
            {
                return false;
            }

            if (InterruptsTrackInitialization(command.Type))
            {
                activeTrackInitialization?.Cancel();
            }

            return true;
        }
    }

    private void SetActiveTrackInitialization(CancellationTokenSource cancellation)
    {
        lock (playbackGate)
        {
            activeTrackInitialization = cancellation;
        }
    }

    private void ClearActiveTrackInitialization(CancellationTokenSource cancellation)
    {
        lock (playbackGate)
        {
            if (ReferenceEquals(activeTrackInitialization, cancellation))
            {
                activeTrackInitialization = null;
            }
        }
    }

    private static bool InterruptsTrackInitialization(PlaybackControlCommandType type) =>
        type is PlaybackControlCommandType.Stop
            or PlaybackControlCommandType.Next
            or PlaybackControlCommandType.Previous
            or PlaybackControlCommandType.PlayAt
            or PlaybackControlCommandType.Seek
            or PlaybackControlCommandType.RestartAudio;

    private void SetVolumeCore(float value)
    {
        if (float.IsNaN(value))
        {
            throw new ArgumentException("Volume cannot be NaN.", nameof(value));
        }

        lock (statusGate)
        {
            volume = Math.Clamp(value, 0.0f, 1.0f);
        }
    }

    private void OnOutputVolumeChanged(object? sender, float level)
    {
        // The backend already holds this level (the user set it in the system mixer), so adopt it as
        // the controller's volume without posting a control command back to the output, and surface
        // it so the UI slider follows. The play loop's periodic "output.Volume = Volume" re-assert is
        // then a no-op because the device already has the value.
        SetVolumeCore(level);
        VolumeChanged?.Invoke(this, Volume);
    }

    private TimeSpan ConsumePendingStartPosition()
    {
        var position = pendingStartPosition;
        pendingStartPosition = TimeSpan.Zero;
        return position;
    }

    private void ResetRuntimeStatus()
    {
        lock (statusGate)
        {
            playbackState = PlaybackQueuePlaybackState.Stopped;
            sourcePosition = pendingStartPosition;
            playedDuration = TimeSpan.Zero;
            bufferedDuration = TimeSpan.Zero;
        }
    }

    private void UpdateRuntimeStatus(TimeSpan source, TimeSpan played, TimeSpan buffered)
    {
        lock (statusGate)
        {
            sourcePosition = source;
            playedDuration = played;
            bufferedDuration = buffered;
        }
    }

    private void SetPlaybackState(PlaybackQueuePlaybackState state, string? message)
    {
        var changed = false;
        lock (statusGate)
        {
            changed = playbackState != state;
            playbackState = state;
        }

        if (changed || !string.IsNullOrWhiteSpace(message))
        {
            PlaybackStateChanged?.Invoke(this, new PlaybackQueueStateChangedEventArgs(state, message ?? string.Empty, CreateStatus()));
        }
    }

    private void SetInitializing(bool value)
    {
        lock (statusGate)
        {
            if (isInitializing == value)
                return;
            isInitializing = value;
        }
        OnStatusChanged(string.Empty);
    }

    private PlaybackQueueStatus CreateStatus()
    {
        lock (statusGate)
        {
            return new PlaybackQueueStatus(Current, CurrentIndex, items.Count, mode, playbackState, sourcePosition, playedDuration, bufferedDuration, volume)
            {
                IsInitializing = isInitializing,
            };
        }
    }

    private void OnStatusChanged(string message)
    {
        StatusChanged?.Invoke(this, new PlaybackQueueStatusChangedEventArgs(message, CreateStatus()));
    }

    private void OnError(string filePath, string message)
    {
        ErrorOccurred?.Invoke(this, new PlaybackQueueErrorEventArgs(filePath, message));
    }

    private IEnumerable<PlaybackQueueItem> Shuffle(IEnumerable<PlaybackQueueItem> source)
    {
        var buffer = source.ToList();
        for (var i = 0; i < buffer.Count; i++)
        {
            var j = random.Next(i, buffer.Count);
            yield return buffer[j];
            buffer[j] = buffer[i];
        }
    }

    private bool IsValidIndex(int index)
    {
        return index >= 0 && index < items.Count;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static PlaybackQueueTrackEndReason ToTrackEndReason(PlaybackTrackOutcome outcome)
    {
        return outcome switch
        {
            PlaybackTrackOutcome.TrackEnded => PlaybackQueueTrackEndReason.Ended,
            PlaybackTrackOutcome.Quit => PlaybackQueueTrackEndReason.Stopped,
            PlaybackTrackOutcome.Next or PlaybackTrackOutcome.Previous or PlaybackTrackOutcome.Restart => PlaybackQueueTrackEndReason.Skipped,
            _ => PlaybackQueueTrackEndReason.Ended
        };
    }

    private sealed class TrackPlaybackState
    {
        public long DecodedBytes { get; set; }

        public int FrameCount { get; set; }

        public TimeSpan DecodedDuration { get; set; }

        public bool SourceEnded { get; set; }

        public bool Started { get; set; }

        public bool OutputStarted { get; set; }

        public void ResetAfterSeek()
        {
            DecodedBytes = 0;
            FrameCount = 0;
            DecodedDuration = TimeSpan.Zero;
            SourceEnded = false;
            Started = false;
            OutputStarted = false;
        }
    }

    private sealed class PlaybackOutputContext : IAsyncDisposable
    {
        public IAudioOutputDevice? Output { get; set; }

        public AudioFormat? Format { get; set; }

        public ValueTask DisposeAsync() => Output?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed record PlaybackControlCommand(
        PlaybackControlCommandType Type,
        int Index = -1,
        float Volume = 1.0f,
        TimeSpan Position = default);

    private enum PlaybackControlCommandType
    {
        TogglePause,
        Pause,
        Resume,
        Stop,
        Next,
        Previous,
        PlayAt,
        Seek,
        RestartAudio,
        SetVolume
    }

    private enum PlaybackTrackOutcome
    {
        TrackEnded,
        Next,
        Previous,
        Restart,
        Quit
    }
}
