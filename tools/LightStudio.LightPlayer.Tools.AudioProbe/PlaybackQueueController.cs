using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using LightStudio.FfmpegShim;

namespace LightStudio.LightPlayer.Tools.AudioProbe;

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

    public static QueuePlaybackOptions CreateOptions(TimeSpan? durationPerTrack, float volume)
    {
        return new QueuePlaybackOptions(durationPerTrack, Math.Clamp(volume, 0.0f, 1.0f), DefaultBufferTarget);
    }

    public void AddRange(IEnumerable<string> filePaths)
    {
        ThrowIfDisposed();

        var newItems = filePaths.Select(path => new PlaybackQueueItem(Path.GetFullPath(path))).ToList();
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
            orderedItems.Select(item => item.FilePath).ToArray(),
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
        AddRange(snapshot.FilePaths);
        if (snapshot.CurrentIndex >= 0 && snapshot.CurrentIndex < items.Count)
        {
            currentIndex = snapshot.CurrentIndex;
        }

        pendingStartPosition = snapshot.Position;
        SetVolume(snapshot.Volume);
        Mode = snapshot.Mode;
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
        using var source = new FfmpegPcmFrameSource(item.FilePath, ConsumePendingStartPosition(), outputContext.Format);
        var output = EnsureOutput(outputContext, source.Format);

        var state = new TrackPlaybackState();
        var stopwatch = Stopwatch.StartNew();
        var durationLimit = options.DurationPerTrack ?? TimeSpan.MaxValue;

        summary.TracksStarted++;
        UpdateRuntimeStatus(source.Position, TimeSpan.Zero, TimeSpan.Zero);
        TrackStarted?.Invoke(this, new PlaybackQueueTrackStartedEventArgs(item, CurrentIndex, items.Count, source.Metadata.Title ?? item.DisplayName, source.Duration, Mode, Volume));

        while (!cancellationToken.IsCancellationRequested)
        {
            while (controls.TryRead(out var control))
            {
                var outcome = HandleControl(control, source, output, state);
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
                UpdateRuntimeStatus(source.Position, output.PlayedDuration, output.BufferedDuration);
                await Task.Delay(LoopDelay, cancellationToken);
                continue;
            }

            output.Update();
            UpdateRuntimeStatus(source.Position, output.PlayedDuration, output.BufferedDuration);

            while (!state.SourceEnded && state.DecodedDuration < durationLimit && output.BufferedDuration < options.BufferTarget)
            {
                var frame = source.ReadFrame(durationLimit - state.DecodedDuration);
                if (frame is null)
                {
                    state.SourceEnded = true;
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
            return outputContext.Output;
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
        TrackEnded?.Invoke(this, new PlaybackQueueTrackEndedEventArgs(item, CurrentIndex, items.Count, ToTrackEndReason(outcome), state.DecodedBytes, state.FrameCount, state.DecodedDuration, played, elapsed, underrunCount));
        return outcome;
    }

    private PlaybackTrackOutcome? HandleControl(PlaybackControlCommand control, FfmpegPcmFrameSource source, IAudioOutputDevice output, TrackPlaybackState state)
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

                OnStatusChanged("Track selected");
                return PlaybackTrackOutcome.Restart;
            case PlaybackControlCommandType.Seek:
                output.ResetAfterSeek();
                var actual = source.Seek(control.Position);
                state.ResetAfterSeek();
                UpdateRuntimeStatus(source.Position, output.PlayedDuration, output.BufferedDuration);
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
