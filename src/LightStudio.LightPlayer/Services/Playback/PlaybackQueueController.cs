using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LightStudio.FfmpegShim;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Services.Playback;

public sealed class PlaybackQueueController : IDisposable
{
    private static readonly TimeSpan DefaultBufferTarget = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan LoopDelay = TimeSpan.FromMilliseconds(15);

    private readonly Random random = new();
    private readonly ObservableCollection<PlaybackQueueItem> items = new();
    private readonly object playbackGate = new();
    private readonly object statusGate = new();
    private List<PlaybackQueueItem>? sequentialBackup;
    private Channel<PlaybackControlCommand>? activeControls;
    private PlaybackMode mode = PlaybackMode.Sequential;
    private PlaybackQueuePlaybackState playbackState = PlaybackQueuePlaybackState.Stopped;
    private int currentIndex = -1;
    private float volume = 1.0f;
    private bool paused;
    private TimeSpan sourcePosition;
    private TimeSpan playedDuration;
    private TimeSpan bufferedDuration;
    private TimeSpan pendingStartPosition;
    private bool disposed;

    public event EventHandler<PlaybackQueueStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<PlaybackQueueStateChangedEventArgs>? PlaybackStateChanged;

    public event EventHandler<PlaybackQueueTrackStartedEventArgs>? TrackStarted;

    public event EventHandler<PlaybackQueueTrackEndedEventArgs>? TrackEnded;

    public event EventHandler<PlaybackQueueErrorEventArgs>? ErrorOccurred;

    public event EventHandler<string>? BackendError;

    public event EventHandler<string>? Underrun;

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
    /// Resolves the live system mixer rate. When set, the reused OpenAL output device is recreated
    /// at a track boundary once the system output device starts running at a different rate, so
    /// playback follows the new device instead of leaving the audio server to resample a stale rate.
    /// </summary>
    public ISystemSampleRateProvider? SystemSampleRateProvider { get; set; }

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
        AddRange(filePaths.Select(path => new PlaybackQueueItem(Path.GetFullPath(path))));
    }

    public void AddRange(IEnumerable<PlaybackQueueItem> queueItems)
    {
        ThrowIfDisposed();

        var newItems = queueItems.Select(NormalizeItem).ToList();
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
        InsertRange(index, filePaths.Select(path => new PlaybackQueueItem(Path.GetFullPath(path))));
    }

    public void InsertRange(int index, IEnumerable<PlaybackQueueItem> queueItems)
    {
        ThrowIfDisposed();

        var newItems = queueItems.Select(NormalizeItem).ToList();
        if (newItems.Count == 0)
        {
            return;
        }

        var insertAt = Math.Clamp(index, 0, items.Count);

        if (mode == PlaybackMode.Random)
        {
            sequentialBackup ??= items.ToList();
            sequentialBackup.AddRange(newItems);
            var shuffled = Shuffle(newItems).ToList();
            for (var i = 0; i < shuffled.Count; i++)
            {
                items.Insert(insertAt + i, shuffled[i]);
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
    /// uninterrupted — this only rewrites the queue order and never posts a control command.
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
        using var outputContext = new PlaybackOutputContext();
        if (options.PreferredSampleRate > 0)
        {
            outputContext.Format = new AudioFormat((uint)options.PreferredSampleRate, 2, 16);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var current = Current;
            if (current is null)
            {
                OnStatusChanged("Queue is empty.");
                break;
            }

            PlaybackTrackOutcome outcome;
            try
            {
                outcome = await PlayCurrentTrackAsync(current, options, controls, summary, outputContext, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                summary.Failures++;
                OnError(current.FilePath, ex.Message);
                outcome = PlaybackTrackOutcome.TrackEnded;
            }

            switch (outcome)
            {
                case PlaybackTrackOutcome.Quit:
                    SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Queue playback stopped.");
                    return summary;
                case PlaybackTrackOutcome.Previous:
                    if (!MovePreviousCore())
                    {
                        OnStatusChanged("Already at the first track.");
                    }
                    continue;
                case PlaybackTrackOutcome.Restart:
                    continue;
                case PlaybackTrackOutcome.Next:
                case PlaybackTrackOutcome.TrackEnded:
                    if (!MoveNextCore())
                    {
                        OnStatusChanged("End of queue.");
                        SetPlaybackState(PlaybackQueuePlaybackState.Stopped, "Queue playback stopped.");
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
        CancellationToken cancellationToken)
    {
        var requestedTrackPosition = ConsumePendingStartPosition();

        // For a CUE track the file is decoded from the track's start offset; the requested
        // position (seek/restore) is relative to the track, so it is added on top. Plain files
        // treat the requested position as an absolute file offset.
        var isCue = item.IsCueTrack;
        var trackStart = isCue ? item.StartTime : TimeSpan.Zero;
        var fileStartPosition = trackStart + requestedTrackPosition;

        using var source = new FfmpegPcmFrameSource(item.FilePath, fileStartPosition, outputContext.Format);
        var output = EnsureOutput(outputContext, source.Format);

        // A CUE track's playable length is its declared duration; the final track of a sheet
        // may have an unknown (zero) duration, meaning "play to the end of the file".
        var hasCueEnd = isCue && item.Duration > TimeSpan.Zero;
        var cueEndPosition = trackStart + item.Duration;
        var trackDuration = isCue
            ? (item.Duration > TimeSpan.Zero
                ? item.Duration
                : (source.Duration > trackStart ? source.Duration - trackStart : TimeSpan.Zero))
            : source.Duration;

        // Track-relative position for status/UI so a CUE track's progress runs 0..trackDuration.
        TimeSpan ToTrackRelative(TimeSpan filePosition) =>
            isCue ? (filePosition > trackStart ? filePosition - trackStart : TimeSpan.Zero) : filePosition;

        var state = new TrackPlaybackState();
        var stopwatch = Stopwatch.StartNew();
        var durationLimit = options.DurationPerTrack ?? TimeSpan.MaxValue;

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
            : source.Metadata.Title ?? item.DisplayName;
        TrackStarted?.Invoke(this, new PlaybackQueueTrackStartedEventArgs(item, CurrentIndex, items.Count, startedTitle, trackDuration, Mode, Volume));

        while (!cancellationToken.IsCancellationRequested)
        {
            while (controls.TryRead(out var control))
            {
                var outcome = HandleControl(control, source, output, state, trackStart);
                if (outcome.HasValue)
                {
                    stopwatch.Stop();
                    return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, outcome.Value, keepOutputQueued: false);
                }
            }

            output.Volume = Volume;

            if (paused)
            {
                output.Update();
                UpdateRuntimeStatus(ToTrackRelative(source.Position), output.PlayedDuration, output.BufferedDuration);
                await Task.Delay(LoopDelay, cancellationToken);
                continue;
            }

            output.Update();
            UpdateRuntimeStatus(ToTrackRelative(source.Position), output.PlayedDuration, output.BufferedDuration);

            while (!state.SourceEnded && state.DecodedDuration < durationLimit && output.BufferedDuration < options.BufferTarget)
            {
                // Bound the next frame so a CUE track never bleeds past its segment: read at
                // most up to the cue end. A zero/negative bound makes ReadFrame return null,
                // which the loop treats as end-of-track and advances to the next entry.
                var maxFrame = durationLimit - state.DecodedDuration;
                if (hasCueEnd)
                {
                    var remainingToCueEnd = cueEndPosition - source.Position;
                    if (remainingToCueEnd < maxFrame)
                    {
                        maxFrame = remainingToCueEnd;
                    }
                }

                var frame = source.ReadFrame(maxFrame);
                if (frame is null)
                {
                    state.SourceEnded = true;
                    Console.WriteLine(
                        $"[Playback] Source ended: '{item.DisplayName}' started={state.Started} " +
                        $"decoded={state.DecodedDuration} sourcePosition={source.Position} duration={source.Duration} " +
                        $"frames={state.FrameCount} queuedBuffers={output.QueuedBufferCount} lastFfmpegError={source.LastFfmpegError}");
                    break;
                }

                output.QueueBuffer(frame.Data);
                state.DecodedDuration += frame.Duration;
                state.DecodedBytes += frame.Data.Length;
                state.FrameCount++;
            }

            if (!state.Started && output.QueuedBufferCount > 0)
            {
                output.Start();
                state.Started = true;
                SetPlaybackState(PlaybackQueuePlaybackState.Playing, "Playback started.");
            }

            if (state.SourceEnded || state.DecodedDuration >= durationLimit)
            {
                var canContinue = CanContinueAfterCurrent();
                if (canContinue || output.QueuedBufferCount == 0)
                {
                    stopwatch.Stop();
                    return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, PlaybackTrackOutcome.TrackEnded, keepOutputQueued: canContinue);
                }
            }

            if (!state.Started && state.SourceEnded)
            {
                stopwatch.Stop();
                return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, PlaybackTrackOutcome.TrackEnded, keepOutputQueued: CanContinueAfterCurrent());
            }

            await Task.Delay(LoopDelay, cancellationToken);
        }

        stopwatch.Stop();
        return CompleteTrack(summary, item, state, output, stopwatch.Elapsed, PlaybackTrackOutcome.Quit, keepOutputQueued: false);
    }

    private OpenAlAudioOutputDevice EnsureOutput(PlaybackOutputContext outputContext, AudioFormat format)
    {
        if (outputContext.Output is not null)
        {
            // Reuse the open device/context across tracks so same-device transitions stay gapless.
            // But OpenAL fixes its mixer rate when the context is created: if the user switches to
            // an output device that runs at a different rate, OpenAL keeps rendering at the old rate
            // and the audio server resamples to the new device (visible as a stale rate in pw-top).
            // Recreate the device once, at this track boundary, so OpenAL renegotiates and outputs at
            // the new device's native rate. That costs a single gap on a device switch — an accepted
            // trade for output quality — while an unchanged device keeps playing gaplessly.
            if (!SystemOutputRateChanged(outputContext.Output))
            {
                return outputContext.Output;
            }

            outputContext.Output.Dispose();
            outputContext.Output = null;
        }

        var output = new OpenAlAudioOutputDevice(format)
        {
            Volume = Volume
        };
        output.BackendError += (_, message) => BackendError?.Invoke(this, message);
        output.Underrun += (_, message) => Underrun?.Invoke(this, message);
        outputContext.Output = output;
        outputContext.Format = format;
        return output;
    }

    private bool SystemOutputRateChanged(OpenAlAudioOutputDevice output)
    {
        var systemRate = SystemSampleRateProvider?.GetSystemSampleRate() ?? 0;
        if (systemRate <= 0)
        {
            // The live system rate is unknown; keep the current device rather than churn it.
            return false;
        }

        var currentRate = output.DeviceSampleRate;
        if (currentRate <= 0 || currentRate == systemRate)
        {
            return false;
        }

        Console.WriteLine(
            $"[Playback] System output rate changed {currentRate} Hz -> {systemRate} Hz; " +
            "recreating OpenAL device to renegotiate.");
        return true;
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
        if (!keepOutputQueued)
        {
            output.ResetAfterSeek();
        }

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

    private PlaybackTrackOutcome? HandleControl(PlaybackControlCommand control, FfmpegPcmFrameSource source, IAudioOutputDevice output, TrackPlaybackState state, TimeSpan trackStart)
    {
        switch (control.Type)
        {
            case PlaybackControlCommandType.TogglePause:
                TogglePause(output);
                return null;
            case PlaybackControlCommandType.Pause:
                Pause(output);
                return null;
            case PlaybackControlCommandType.Resume:
                Resume(output);
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
                // The requested position is relative to the (possibly CUE) track; translate it
                // to an absolute file offset before seeking, and report it back track-relative.
                var seekTarget = trackStart + (control.Position < TimeSpan.Zero ? TimeSpan.Zero : control.Position);
                output.ResetAfterSeek();
                var actual = source.Seek(seekTarget);
                state.ResetAfterSeek();
                var relative = source.Position > trackStart ? source.Position - trackStart : TimeSpan.Zero;
                UpdateRuntimeStatus(relative, output.PlayedDuration, output.BufferedDuration);
                OnStatusChanged($"Seek requested {control.Position}, actual {actual}.");
                return null;
            case PlaybackControlCommandType.SetVolume:
                SetVolumeCore(control.Volume);
                output.Volume = Volume;
                return null;
            default:
                return null;
        }
    }

    private void TogglePause(IAudioOutputDevice output)
    {
        if (paused)
        {
            Resume(output);
        }
        else
        {
            Pause(output);
        }
    }

    private void Pause(IAudioOutputDevice output)
    {
        if (paused)
        {
            return;
        }

        paused = true;
        if (output.State == AudioOutputState.Playing)
        {
            output.Pause();
        }

        SetPlaybackState(PlaybackQueuePlaybackState.Paused, "Paused.");
    }

    private void Resume(IAudioOutputDevice output)
    {
        if (!paused)
        {
            return;
        }

        paused = false;
        if (output.State == AudioOutputState.Paused)
        {
            output.Resume();
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
            return activeControls?.Writer.TryWrite(command) == true;
        }
    }

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

    private PlaybackQueueStatus CreateStatus()
    {
        lock (statusGate)
        {
            return new PlaybackQueueStatus(Current, CurrentIndex, items.Count, mode, playbackState, sourcePosition, playedDuration, bufferedDuration, volume);
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

    private static PlaybackQueueItem NormalizeItem(PlaybackQueueItem item)
    {
        var fullPath = Path.GetFullPath(item.FilePath);
        return string.Equals(fullPath, item.FilePath, StringComparison.Ordinal)
            ? item
            : item with { FilePath = fullPath };
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

        public void ResetAfterSeek()
        {
            DecodedBytes = 0;
            FrameCount = 0;
            DecodedDuration = TimeSpan.Zero;
            SourceEnded = false;
            Started = false;
        }
    }

    private sealed class PlaybackOutputContext : IDisposable
    {
        public OpenAlAudioOutputDevice? Output { get; set; }

        public AudioFormat? Format { get; set; }

        public void Dispose()
        {
            Output?.Dispose();
        }
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
