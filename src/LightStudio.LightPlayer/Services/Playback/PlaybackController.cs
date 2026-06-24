using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LightStudio.LightPlayer.Services;
using MusicPlaybackItemModel = LightStudio.LightPlayer.Models.MusicPlaybackItemModel;
using UiPlaybackMode = LightStudio.LightPlayer.Models.PlaybackMode;
using UiPlaybackState = LightStudio.LightPlayer.Models.PlaybackState;

namespace LightStudio.LightPlayer.Services.Playback;

/// <summary>
/// UI-facing playback engine. Wraps the validated single-thread-affinity
/// <see cref="PlaybackQueueController"/> and the cross-platform FFmpeg decoder /
/// OpenAL output pipeline, exposing an observable queue, transport state, and
/// commands for the playback bar and now-playing pane.
/// </summary>
/// <remarks>
/// The core controller's queue and decode loop must never be touched
/// concurrently, so every interaction with it is marshalled onto a single
/// <see cref="PlaybackThread"/>. Core events fire on that pump thread and are
/// posted back to the Avalonia UI thread. The UI-owned <see cref="QueueItems"/>
/// is a projection rebuilt from queue snapshots the pump publishes.
/// </remarks>
public sealed partial class PlaybackController : ObservableObject, IDisposable
{
    private static readonly TimeSpan PositionPollInterval = TimeSpan.FromMilliseconds(200);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private readonly PlaybackQueueController? core;
    private readonly PlaybackThread? pump;
    private readonly ISystemMediaControls systemMedia;
    private readonly Dictionary<string, MusicMetaTemplate> templates;
    private readonly DispatcherTimer persistTimer;

    // UI-thread state.
    private IReadOnlyList<PlaybackQueueItem> lastQueueItems = Array.Empty<PlaybackQueueItem>();
    private bool isScrubbing;
    private bool disposed;
    private double pendingRestorePositionMs;
    private double pendingRestoreDurationMs;
    private bool hasPendingRestore;
    private bool suppressEmptyDuringReplace;

    // Pump-thread state.
    private Task? loopTask;
    private CancellationTokenSource? loopCts;
    private double currentDurationMs;

    [ObservableProperty]
    private MusicPlaybackItemModel? currentItem;

    [ObservableProperty]
    private UiPlaybackState state = UiPlaybackState.Stopped;

    [ObservableProperty]
    private UiPlaybackMode mode = UiPlaybackMode.Sequential;

    [ObservableProperty]
    private double volume = 1.0;

    [ObservableProperty]
    private double positionMs;

    [ObservableProperty]
    private double durationMs;

    public PlaybackController(ISystemMediaControls? systemMediaControls = null)
    {
        systemMedia = systemMediaControls ?? new NoopSystemMediaControls();
        templates = new Dictionary<string, MusicMetaTemplate>(PathComparer);
        QueueItems = new Models.BulkObservableCollection<MusicPlaybackItemModel>();

        persistTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        persistTimer.Tick += OnPersistTimerTick;

        systemMedia.PlayPauseRequested += () => PostToUi(TogglePlayPause);
        systemMedia.NextRequested += () => PostToUi(Next);
        systemMedia.PreviousRequested += () => PostToUi(Previous);

        pump = new PlaybackThread("LightPlayer.Playback");
        core = new PlaybackQueueController();
        core.StatusChanged += OnCoreStatusChanged;
        core.PlaybackStateChanged += OnCorePlaybackStateChanged;
        core.TrackStarted += OnCoreTrackStarted;
        core.ErrorOccurred += OnCoreError;
        core.BackendError += OnCoreBackendError;
        PostCore(() => core.SetVolume((float)Volume));
    }

    /// <summary>Raised (on the UI thread) when playback produces an error message.</summary>
    public event Action<string>? PlaybackError;

    /// <summary>Raised (on the UI thread, debounced) when persisted state should be saved.</summary>
    public event Action? PersistRequested;

    /// <summary>
    /// Raised (on the UI thread) after a queued item's tags are enriched in place, e.g. when
    /// drag-and-drop / file-picker metadata finishes loading asynchronously. Views that snapshot
    /// queue metadata into their own models (the home and now-playing "Upcoming" lists) refresh
    /// in response, since a property change on an existing item raises no collection notification.
    /// </summary>
    public event Action? QueueItemsMetadataChanged;

    /// <summary>The now-playing queue projected for display. UI-thread owned.</summary>
    public Models.BulkObservableCollection<MusicPlaybackItemModel> QueueItems { get; }

    public bool HasItem => CurrentItem is not null;

    /// <summary>
    /// Preferred output sample rate in Hz, or 0 to follow the source. Applied to the next track.
    /// </summary>
    public int PreferredSampleRate { get; set; }

    /// <summary>
    /// Master switch for resampling. When false, audio follows the source rate (no resample).
    /// When true, audio is resampled to <see cref="PreferredSampleRate"/>, or to the system
    /// mixer rate when that is 0. Applied to the next track.
    /// </summary>
    public bool AlwaysResample { get; set; }

    /// <summary>Resolves the live system mixer rate; null when not configured.</summary>
    public ISystemSampleRateProvider? SystemSampleRateProvider { get; set; }

    /// <summary>
    /// Resolves a local artwork file path for a track's full path, used to feed
    /// "now playing" artwork to OS media controls. Best-effort and asynchronous;
    /// returns null when the track has no artwork.
    /// </summary>
    public Func<string, Task<string?>>? AlbumArtPathResolver { get; set; }

    // ---- Public commands (UI thread) -----------------------------------

    /// <summary>Appends <paramref name="item"/> and immediately plays it.</summary>
    public void PlayNow(MusicPlaybackItemModel item)
    {
        var queueItem = Register(item);
        if (queueItem is null)
        {
            return;
        }

        PostCore(() =>
        {
            core!.AddRange(new[] { queueItem });
            core.PlayAt(core.Items.Count - 1);
            EnsureLoopRunning();
        });
    }

    /// <summary>Appends an item to the end of the queue without changing playback.</summary>
    public void Enqueue(MusicPlaybackItemModel item)
    {
        var queueItem = Register(item);
        if (queueItem is not null)
        {
            PostCore(() => core!.AddRange(new[] { queueItem }));
        }
    }

    /// <summary>Appends several items to the end of the queue.</summary>
    public void EnqueueRange(IReadOnlyList<MusicPlaybackItemModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var queueItems = RegisterMany(items);
        if (queueItems.Length > 0)
        {
            PostCore(() => core!.AddRange(queueItems));
        }
    }

    /// <summary>Inserts an item right after the current track.</summary>
    public void InsertNext(MusicPlaybackItemModel item)
    {
        var queueItem = Register(item);
        if (queueItem is not null)
        {
            PostCore(() =>
            {
                var at = core!.CurrentIndex < 0 ? core.Items.Count : core.CurrentIndex + 1;
                core.InsertRange(at, new[] { queueItem });
            });
        }
    }

    /// <summary>Replaces the queue with <paramref name="items"/> and starts playing.</summary>
    public void PlayAll(IReadOnlyList<MusicPlaybackItemModel> items, bool shuffle)
    {
        if (items.Count == 0)
        {
            return;
        }

        // "Play" forces ordered play; "Shuffle" forces random. Either way the
        // UI mode and the core mode are resolved together so they never diverge.
        var targetMode = shuffle
            ? UiPlaybackMode.Random
            : Mode == UiPlaybackMode.Random ? UiPlaybackMode.Sequential : Mode;
        Mode = targetMode;

        var pathArray = RegisterMany(items);
        if (pathArray.Length == 0)
        {
            return;
        }

        var coreMode = ToCoreMode(targetMode);
        // The upcoming Clear() emits a transient empty queue snapshot before the
        // new tracks are added; suppress it so the playback bar (bound to HasItem)
        // stays visible across the switch instead of flickering hidden.
        suppressEmptyDuringReplace = true;
        PostCoreAsync(async () =>
        {
            await StopLoopAsync();
            core!.Clear();
            // Setting Random before AddRange makes the core shuffle the new
            // items (keeping the sequential order as the un-shuffle backup).
            core.Mode = coreMode;
            core.AddRange(pathArray);
            EnsureLoopRunning();
        });
    }

    /// <summary>
    /// Replaces the queue with <paramref name="items"/> in the given order and
    /// starts playing from <paramref name="startIndex"/>. Used by the home
    /// all-music grid where the list is already shuffled and clicking a tile
    /// plays the whole library from that track.
    /// </summary>
    public void PlayAllFrom(IReadOnlyList<MusicPlaybackItemModel> items, int startIndex)
    {
        if (items.Count == 0)
        {
            return;
        }

        var start = Math.Clamp(startIndex, 0, items.Count - 1);

        // Keep the supplied order: switch away from random so the index lines up.
        Mode = Mode == UiPlaybackMode.Random ? UiPlaybackMode.Sequential : Mode;

        var pathArray = RegisterMany(items);
        if (pathArray.Length == 0)
        {
            return;
        }

        var coreMode = ToCoreMode(Mode);
        var coreStart = Math.Min(start, pathArray.Length - 1);
        // See PlayAll: hide the transient empty snapshot from Clear() so the
        // playback bar doesn't flicker while replacing the queue.
        suppressEmptyDuringReplace = true;
        PostCoreAsync(async () =>
        {
            await StopLoopAsync();
            core!.Clear();
            core.Mode = coreMode;
            core.AddRange(pathArray);
            core.PlayAt(coreStart);
            EnsureLoopRunning();
        });
    }

    /// <summary>Plays an item already present in the queue.</summary>
    public void PlayQueueItem(MusicPlaybackItemModel item)
    {
        var index = QueueItems.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        PostCore(() =>
        {
            core!.PlayAt(index);
            EnsureLoopRunning();
        });
    }

    /// <summary>Removes an item from the queue.</summary>
    public void RemoveItem(MusicPlaybackItemModel item)
    {
        var index = QueueItems.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        PostCore(() =>
        {
            var removingCurrent = core!.CurrentIndex == index;
            core.RemoveAt(index);
            ResyncPlaybackAfterRemoval(removingCurrent);
        });
    }

    /// <summary>Removes several items from the queue in a single batch (edit mode).</summary>
    public void RemoveItems(IReadOnlyList<MusicPlaybackItemModel> items)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        // Resolve each item to its current queue position. The projection and the
        // core queue share ordering, so indices computed here line up with the core.
        var indices = new List<int>(items.Count);
        foreach (var item in items)
        {
            var index = QueueItems.IndexOf(item);
            if (index >= 0)
            {
                indices.Add(index);
            }
        }

        if (indices.Count == 0)
        {
            return;
        }

        indices.Sort();

        if (indices.Count == QueueItems.Count)
        {
            // Whole-queue clear (select-all then delete): one core mutation instead
            // of a RemoveAt storm, each of which would publish a queue snapshot.
            PostCore(() => core!.Clear());
            return;
        }

        // Descending so each RemoveAt leaves the not-yet-removed indices valid.
        PostCore(() =>
        {
            var removingCurrent = indices.Contains(core!.CurrentIndex);
            for (var i = indices.Count - 1; i >= 0; i--)
            {
                core!.RemoveAt(indices[i]);
            }

            ResyncPlaybackAfterRemoval(removingCurrent);
        });
    }

    /// <summary>
    /// Runs on the pump thread after queue rows were removed. When the track that was
    /// playing is among them, the decode loop is still bound to a track no longer in the
    /// queue, so resynchronize playback with the queue selection: restart at the track
    /// that took its place, or stop when nothing is left. The current index is read after
    /// every removal has been applied, so batch deletes resolve to the correct track.
    /// </summary>
    private void ResyncPlaybackAfterRemoval(bool removingCurrent)
    {
        if (!removingCurrent)
        {
            return;
        }

        if (core!.HasItems)
        {
            core.PlayAt(core.CurrentIndex);
        }
        else
        {
            core.Stop();
        }
    }

    /// <summary>
    /// Reorders the queue so that <paramref name="items"/> (drawn from the current queue) sit,
    /// in their existing relative order, at <paramref name="targetIndex"/>. Used by the
    /// now-playing drag reorder. Playback is never interrupted: the currently playing track
    /// stays current even when it is one of the moved rows.
    /// </summary>
    public void MoveItems(IReadOnlyList<MusicPlaybackItemModel> items, int targetIndex)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        // Resolve each dragged model to its position in the projection (which mirrors the core
        // order). Reference identity disambiguates duplicate file paths in the queue.
        var indices = new List<int>(items.Count);
        foreach (var item in items)
        {
            var index = QueueItems.IndexOf(item);
            if (index >= 0)
            {
                indices.Add(index);
            }
        }

        if (indices.Count == 0)
        {
            return;
        }

        indices.Sort();
        var clampedTarget = Math.Clamp(targetIndex, 0, QueueItems.Count);

        PostCore(() => core!.Move(indices, clampedTarget));
    }

    /// <summary>
    /// Inserts <paramref name="items"/> into the queue at <paramref name="index"/> without
    /// disturbing playback. Used by drops of library items or external files onto the queue.
    /// </summary>
    public void InsertItems(IReadOnlyList<MusicPlaybackItemModel> items, int index)
    {
        if (items is null || items.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(index, 0, QueueItems.Count);

        var paths = RegisterMany(items);
        if (paths.Length == 0)
        {
            return;
        }

        PostCore(() => core!.InsertRange(clamped, paths));
    }

    /// <summary>
    /// Applies real tag metadata to the queued item(s) backed by <paramref name="filePath"/>,
    /// updating both the projected <see cref="QueueItems"/> entry and the template used to
    /// rebuild the queue. Enriches items added by file path (drag-and-drop, file picker) once
    /// their tags have been read. A blank <paramref name="title"/> is ignored so the file-name
    /// fallback shown until now is kept. No-op when the path is not in the queue.
    /// </summary>
    public void UpdateItemMetadata(string filePath, string? title, string artist, string album, TimeSpan duration)
    {
        PostToUi(() =>
        {
            var key = NormalizePath(filePath);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var hasTitle = !string.IsNullOrWhiteSpace(title);
            var templateTitle = hasTitle
                ? title!
                : templates.TryGetValue(key, out var existing)
                    ? existing.Title
                    : Path.GetFileNameWithoutExtension(key);

            templates[key] = new MusicMetaTemplate(templateTitle, artist, album, duration);

            var changed = false;
            foreach (var item in QueueItems)
            {
                // CUE tracks carry their own tags from the sheet; never overwrite them with
                // the shared audio file's metadata (several tracks map to the same path).
                if (item.IsCueTrack)
                {
                    continue;
                }

                if (!PathComparer.Equals(item.FilePath, key) &&
                    !PathComparer.Equals(NormalizePath(item.FilePath), key))
                {
                    continue;
                }

                if (hasTitle)
                {
                    item.Title = title!;
                }

                item.Artist = artist;
                item.Album = album;
                if (duration > TimeSpan.Zero)
                {
                    item.Duration = duration;
                }

                changed = true;
            }

            if (changed)
            {
                QueueItemsMetadataChanged?.Invoke();
            }
        });
    }

    public void TogglePlayPause()
    {
        PostCore(() =>
        {
            if (!LoopRunning && core!.HasItems)
            {
                EnsureLoopRunning();
            }
            else
            {
                core!.TogglePause();
            }
        });
    }

    public void Next()
    {
        PostCore(() =>
        {
            core!.Next();
            EnsureLoopRunning();
        });
    }

    public void Previous()
    {
        PostCore(() =>
        {
            core!.Previous();
            EnsureLoopRunning();
        });
    }

    public void BeginScrub() => isScrubbing = true;

    public void EndScrub(double positionMs)
    {
        isScrubbing = false;
        SeekTo(positionMs);
    }

    public void SeekTo(double positionMilliseconds)
    {
        var clamped = Math.Max(0, positionMilliseconds);
        PositionMs = clamped;

        PostCore(() => core!.Seek(TimeSpan.FromMilliseconds(clamped)));
    }

    public void SetVolume(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        if (Math.Abs(Volume - clamped) > 0.0001)
        {
            Volume = clamped;
        }

        PostCore(() => core!.SetVolume((float)clamped));

        RequestPersist();
    }

    public void CycleMode() => SetMode((UiPlaybackMode)(((int)Mode + 1) % 4));

    public void SetMode(UiPlaybackMode value)
    {
        Mode = value;
        var coreMode = ToCoreMode(value);
        PostCore(() => core!.Mode = coreMode);

        RequestPersist();
    }

    /// <summary>
    /// Replaces the queue with the supplied (already randomized) items and plays
    /// from the first track, leaving the playback mode untouched. Reorders the
    /// whole library into the queue rather than toggling shuffle playback.
    /// </summary>
    public void ShuffleAll(IReadOnlyList<MusicPlaybackItemModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var pathArray = RegisterMany(items);
        if (pathArray.Length == 0)
        {
            return;
        }

        var coreMode = ToCoreMode(Mode);
        // The list arrives pre-shuffled, so keep the current mode (don't flip the
        // shuffle toggle) and just replace the queue. Suppress the transient empty
        // snapshot from Clear() so the playback bar doesn't flicker during the swap.
        suppressEmptyDuringReplace = true;
        PostCoreAsync(async () =>
        {
            await StopLoopAsync();
            core!.Clear();
            core.Mode = coreMode;
            core.AddRange(pathArray);
            core.PlayAt(0);
            EnsureLoopRunning();
        });
    }

    // ---- Persistence ---------------------------------------------------

    public PlaybackQueuePersistedState? CreatePersistedState()
    {
        var items = QueueItems
            .Where(item => !string.IsNullOrEmpty(item.FilePath))
            .Select(item => new PlaybackQueueItemState(
                item.FilePath,
                item.Title,
                item.Artist,
                item.Album,
                item.Duration.Ticks,
                item.StartTimeMs))
            .ToArray();
        if (items.Length == 0)
        {
            return null;
        }

        var index = CurrentItem is null
            ? -1
            : Array.FindIndex(items, item =>
                PathComparer.Equals(item.FilePath, CurrentItem.FilePath) && item.StartTimeMs == CurrentItem.StartTimeMs);
        return new PlaybackQueuePersistedState(items, index, PositionMs, DurationMs, (int)Mode, Volume);
    }

    public void RestorePersistedState(PlaybackQueuePersistedState saved)
    {
        if (saved.Items.Count == 0)
        {
            return;
        }

        // Seed the metadata cache so the projected queue items show real tags
        // (title/artist/album/duration) instead of bare file names before any
        // track is decoded. The engine snapshot rebuild reads templates through
        // BuildModel. Keyed by CUE identity (path + start) so several tracks of
        // one split-album file keep distinct tags.
        foreach (var item in saved.Items)
        {
            var path = NormalizePath(item.FilePath);
            if (!string.IsNullOrEmpty(path))
            {
                templates[TemplateKey(path, item.StartTimeMs)] = new MusicMetaTemplate(item.Title, item.Artist, item.Album, TimeSpan.FromTicks(item.DurationTicks));
            }
        }

        Volume = Math.Clamp(saved.Volume, 0.0, 1.0);
        Mode = NormalizeMode(saved.Mode);

        var restoredPosition = Math.Max(0, saved.PositionMs);
        var restoredDuration = Math.Max(0, saved.DurationMs);

        var restoreCurrentFile = saved.CurrentIndex >= 0 && saved.CurrentIndex < saved.Items.Count
            ? saved.Items[saved.CurrentIndex].FilePath
            : "(none)";
        Console.WriteLine(
            $"[Playback] RestorePersistedState: items={saved.Items.Count} index={saved.CurrentIndex} " +
            $"position={restoredPosition:F0}ms duration={restoredDuration:F0}ms mode={Mode} volume={Volume:F2} " +
            $"current={restoreCurrentFile}");

        // The core's Restore() clears the queue before repopulating it, which emits a
        // transient empty snapshot; ApplyQueueSnapshot resets position/duration to zero
        // for that empty state, which would wipe the values we restore here. Defer
        // applying the recovered position/duration until the restored current item
        // reappears in the queue snapshot so the progress bar reflects it at rest.
        pendingRestorePositionMs = restoredPosition;
        pendingRestoreDurationMs = restoredDuration;
        hasPendingRestore = true;

        var snapshot = new PlaybackQueueSnapshot(
            saved.Items.Select(ToQueueItem).ToArray(),
            saved.CurrentIndex,
            ToCoreMode(Mode),
            (float)Volume,
            TimeSpan.FromMilliseconds(restoredPosition));
        PostCore(() => core!.Restore(snapshot));
    }

    // ---- Core event handlers (pump thread) -----------------------------

    private void OnCoreStatusChanged(object? sender, PlaybackQueueStatusChangedEventArgs e)
    {
        var items = core!.Items.ToArray();
        var index = core.CurrentIndex;
        PostToUi(() => ApplyQueueSnapshot(items, index));
    }

    private void OnCorePlaybackStateChanged(object? sender, PlaybackQueueStateChangedEventArgs e)
    {
        var uiState = ToUiState(e.State);
        PostToUi(() => State = uiState);
    }

    private void OnCoreTrackStarted(object? sender, PlaybackQueueTrackStartedEventArgs e)
    {
        currentDurationMs = e.Duration.TotalMilliseconds;
        var path = e.Item.FilePath;
        var startMs = e.Item.IsCueTrack ? (int)e.Item.StartTime.TotalMilliseconds : (int?)null;
        var title = e.Title;
        var duration = e.Duration;
        var index = e.Index;
        PostToUi(() => ApplyTrackStarted(path, startMs, title, duration, index));
    }

    private void OnCoreError(object? sender, PlaybackQueueErrorEventArgs e)
    {
        var message = $"{Path.GetFileName(e.FilePath)}: {e.Message}";
        PostToUi(() => PlaybackError?.Invoke(message));
    }

    private void OnCoreBackendError(object? sender, string message) =>
        PostToUi(() => PlaybackError?.Invoke(message));

    // ---- UI-thread projection ------------------------------------------

    private void ApplyQueueSnapshot(IReadOnlyList<PlaybackQueueItem> items, int currentIndex)
    {
        if (items.Count == 0 && suppressEmptyDuringReplace)
        {
            // PlayAll/PlayAllFrom clear the core queue before repopulating it. Skip the
            // resulting empty snapshot so CurrentItem (and the playback bar's HasItem
            // visibility) holds the previous track until the new queue arrives in the
            // following non-empty snapshot, which applies the replacement in one step.
            return;
        }

        // Any real snapshot (the new queue, or a genuine user-initiated clear) ends the
        // replace window so a later emptied queue can still hide the bar.
        suppressEmptyDuringReplace = false;

        if (!QueueItemsEqual(lastQueueItems, items))
        {
            // Replace wholesale with a single reset notification: a per-item add
            // storm froze the UI when the queue held thousands of tracks (restore,
            // reshuffle).
            QueueItems.ReplaceAll(items.Select(BuildModel));
            lastQueueItems = items;
        }

        var item = currentIndex >= 0 && currentIndex < QueueItems.Count ? QueueItems[currentIndex] : null;
        SetCurrentHighlight(item, currentIndex);
        if (item is null)
        {
            DurationMs = 0;
            PositionMs = 0;
        }
        else if (hasPendingRestore)
        {
            // The restored track is now present: apply the position/duration recovered
            // from the previous session (duration first so the slider's Maximum is valid
            // before the position is applied).
            hasPendingRestore = false;
            DurationMs = pendingRestoreDurationMs;
            PositionMs = pendingRestorePositionMs;
        }

        RequestPersist();
    }

    private void ApplyTrackStarted(string path, int? startMs, string title, TimeSpan duration, int index)
    {
        var key = TemplateKey(NormalizePath(path), startMs);
        var artist = string.Empty;
        var album = string.Empty;
        if (templates.TryGetValue(key, out var existing))
        {
            artist = existing.Artist;
            album = existing.Album;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = existing.Title;
            }
        }

        templates[key] = new MusicMetaTemplate(title, artist, album, duration);

        if (index >= 0 && index < QueueItems.Count)
        {
            var item = QueueItems[index];
            if (!string.IsNullOrWhiteSpace(title))
            {
                item.Title = title;
            }

            if (duration > TimeSpan.Zero)
            {
                item.Duration = duration;
            }

            SetCurrentHighlight(item, index);
        }

        DurationMs = duration.TotalMilliseconds;
    }

    private void SetCurrentHighlight(MusicPlaybackItemModel? item, int index)
    {
        for (var i = 0; i < QueueItems.Count; i++)
        {
            QueueItems[i].IsPlaying = i == index;
        }

        CurrentItem = item;
    }

    partial void OnCurrentItemChanged(MusicPlaybackItemModel? value)
    {
        systemMedia.SetNowPlaying(value?.Title ?? string.Empty, value?.Artist ?? string.Empty, value?.Album ?? string.Empty);
        UpdateSystemArtwork(value);
    }

    partial void OnStateChanged(UiPlaybackState value) =>
        systemMedia.SetPlaybackState(value == UiPlaybackState.Playing, CurrentItem is not null);

    private void UpdateSystemArtwork(MusicPlaybackItemModel? value)
    {
        var resolver = AlbumArtPathResolver;
        if (resolver is null || value is null || string.IsNullOrEmpty(value.FilePath))
        {
            return;
        }

        _ = ResolveSystemArtworkAsync(resolver, value);
    }

    private async Task ResolveSystemArtworkAsync(Func<string, Task<string?>> resolver, MusicPlaybackItemModel item)
    {
        try
        {
            var path = await resolver(item.FilePath).ConfigureAwait(false);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var uri = new Uri(path).AbsoluteUri;
            PostToUi(() =>
            {
                // Apply only if this track is still current. Compare by path, not by
                // reference: the queue projection rebuilds item instances, so the
                // "current" model may be a different object for the same track.
                if (!disposed && CurrentItem is { } current && PathComparer.Equals(current.FilePath, item.FilePath))
                {
                    systemMedia.SetArtwork(uri);
                }
            });
        }
        catch
        {
            // Artwork is best-effort; never disrupt playback.
        }
    }

    private void UpdateProgress(double posMs)
    {
        if (!isScrubbing)
        {
            PositionMs = posMs;
        }
    }

    // ---- Pump lifecycle (pump thread) ----------------------------------

    private bool LoopRunning => loopTask is { IsCompleted: false };

    private int ResolveEffectiveSampleRate()
    {
        // AlwaysResample is the master switch: when off, follow the source rate (no resample).
        if (!AlwaysResample)
        {
            return 0;
        }

        if (PreferredSampleRate > 0)
        {
            return PreferredSampleRate;
        }

        // "Use system sample rate": resolve the live mixer rate so FFmpeg resamples to
        // it (high quality) instead of leaving it to the OS mixer. 0 falls back to source.
        return SystemSampleRateProvider?.GetSystemSampleRate() ?? 0;
    }

    private void EnsureLoopRunning()
    {
        if (LoopRunning || core is null || !core.HasItems)
        {
            return;
        }

        loopCts = new CancellationTokenSource();
        // Give the queue engine the live rate provider so it can renegotiate the OpenAL output
        // device at a track boundary when the user switches to an output device with a different rate.
        core.SystemSampleRateProvider = SystemSampleRateProvider;
        var options = PlaybackQueueController.CreateOptions(null, core.Volume, ResolveEffectiveSampleRate(), AlwaysResample);
        loopTask = RunLoopAsync(options, loopCts.Token);
        _ = RunPositionPublisherAsync(loopCts.Token);
    }

    private async Task RunLoopAsync(QueuePlaybackOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await core!.PlayAsync(options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PostToUi(() => PlaybackError?.Invoke(ex.Message));
        }
        finally
        {
            loopCts?.Cancel();
            PostToUi(() => State = UiPlaybackState.Stopped);
        }
    }

    private async Task StopLoopAsync()
    {
        if (!LoopRunning)
        {
            loopTask = null;
            return;
        }

        core!.Stop();
        var task = loopTask!;
        try
        {
            await task;
        }
        catch
        {
            // Already surfaced through RunLoopAsync.
        }

        loopCts?.Cancel();
        loopCts = null;
        loopTask = null;
    }

    private async Task RunPositionPublisherAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = core!.Status;
                var posMs = Math.Max(0, (status.SourcePosition - status.BufferedDuration).TotalMilliseconds);
                PostToUi(() => UpdateProgress(posMs));
                await Task.Delay(PositionPollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- Helpers -------------------------------------------------------

    private PlaybackQueueItem? Register(MusicPlaybackItemModel item)
    {
        var path = NormalizePath(item.FilePath);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        templates[TemplateKey(path, item.StartTimeMs)] = new MusicMetaTemplate(item.Title, item.Artist, item.Album, item.Duration);
        return ToQueueItem(item, path);
    }

    private PlaybackQueueItem[] RegisterMany(IReadOnlyList<MusicPlaybackItemModel> items)
    {
        var queueItems = new List<PlaybackQueueItem>(items.Count);
        foreach (var item in items)
        {
            var queueItem = Register(item);
            if (queueItem is not null)
            {
                queueItems.Add(queueItem);
            }
        }

        return queueItems.ToArray();
    }

    /// <summary>Projects a UI queue model to the core queue entry, carrying CUE segment info.</summary>
    private static PlaybackQueueItem ToQueueItem(MusicPlaybackItemModel item, string normalizedPath) =>
        item.StartTimeMs is { } startMs
            ? new PlaybackQueueItem(normalizedPath)
            {
                StartTime = TimeSpan.FromMilliseconds(startMs),
                Duration = item.Duration,
                Title = item.Title,
                IsCueTrack = true,
            }
            : new PlaybackQueueItem(normalizedPath);

    /// <summary>Projects a persisted queue entry to the core queue entry, carrying CUE segment info.</summary>
    private static PlaybackQueueItem ToQueueItem(PlaybackQueueItemState state)
    {
        var path = NormalizePath(state.FilePath);
        return state.StartTimeMs is { } startMs
            ? new PlaybackQueueItem(path)
            {
                StartTime = TimeSpan.FromMilliseconds(startMs),
                Duration = TimeSpan.FromTicks(state.DurationTicks),
                Title = state.Title,
                IsCueTrack = true,
            }
            : new PlaybackQueueItem(path);
    }

    private MusicPlaybackItemModel BuildModel(PlaybackQueueItem item)
    {
        var fullPath = item.FilePath;
        var name = string.IsNullOrEmpty(fullPath) ? "Unknown" : Path.GetFileNameWithoutExtension(fullPath);
        var startMs = item.IsCueTrack ? (int)item.StartTime.TotalMilliseconds : (int?)null;

        if (templates.TryGetValue(TemplateKey(fullPath, startMs), out var template))
        {
            var title = string.IsNullOrWhiteSpace(template.Title) ? name : template.Title;
            return new MusicPlaybackItemModel(title, template.Artist, template.Album, template.Duration, fullPath, startMs);
        }

        // No cached tags yet (e.g. a bare-path item): use whatever the queue entry carries.
        var fallbackTitle = string.IsNullOrWhiteSpace(item.Title) ? name : item.Title!;
        return new MusicPlaybackItemModel(fallbackTitle, string.Empty, string.Empty, item.Duration, fullPath, startMs);
    }

    /// <summary>
    /// Composite metadata-cache key. Plain files key on the normalized path; CUE tracks add
    /// their start offset so several tracks sharing one audio file keep distinct metadata
    /// (otherwise every track collapses onto the last one registered for that path).
    /// </summary>
    private static string TemplateKey(string normalizedPath, int? startTimeMs) =>
        startTimeMs.HasValue ? $"{normalizedPath}|{startTimeMs.Value}" : normalizedPath;

    private void PostCore(Action action) => pump!.Post(action);

    private void PostCoreAsync(Func<Task> work) => pump!.Post(() => _ = RunGuardedAsync(work));

    private async Task RunGuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            PostToUi(() => PlaybackError?.Invoke(ex.Message));
        }
    }

    private static void PostToUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private void RequestPersist()
    {
        if (PersistRequested is null)
        {
            return;
        }

        persistTimer.Stop();
        persistTimer.Start();
    }

    private void OnPersistTimerTick(object? sender, EventArgs e)
    {
        persistTimer.Stop();
        PersistRequested?.Invoke();
    }

    private static string NormalizePath(string path) =>
        string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path);

    private static bool QueueItemsEqual(IReadOnlyList<PlaybackQueueItem> a, IReadOnlyList<PlaybackQueueItem> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            // Compare by CUE identity (path + start offset), not path alone: an album that is a
            // single file split into CUE tracks has every row sharing the same path, so a path-only
            // check would treat a reorder of those tracks as "unchanged" and leave the UI stale.
            if (!PathComparer.Equals(a[i].FilePath, b[i].FilePath) || a[i].StartTime != b[i].StartTime)
            {
                return false;
            }
        }

        return true;
    }

    private static UiPlaybackMode NormalizeMode(int value) =>
        value is >= 0 and <= 3 ? (UiPlaybackMode)value : UiPlaybackMode.Sequential;

    private static PlaybackMode ToCoreMode(UiPlaybackMode mode) => mode switch
    {
        UiPlaybackMode.ListLoop => PlaybackMode.ListLoop,
        UiPlaybackMode.SingleTrackLoop => PlaybackMode.SingleTrackLoop,
        UiPlaybackMode.Random => PlaybackMode.Random,
        _ => PlaybackMode.Sequential,
    };

    private static UiPlaybackState ToUiState(PlaybackQueuePlaybackState state) => state switch
    {
        PlaybackQueuePlaybackState.Playing => UiPlaybackState.Playing,
        PlaybackQueuePlaybackState.Paused => UiPlaybackState.Paused,
        _ => UiPlaybackState.Stopped,
    };

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        persistTimer.Stop();
        persistTimer.Tick -= OnPersistTimerTick;
        (systemMedia as IDisposable)?.Dispose();

        if (pump is null)
        {
            return;
        }

        using var done = new ManualResetEventSlim(false);
        pump.Post(() => _ = ShutdownOnPumpAsync(done));
        done.Wait(TimeSpan.FromSeconds(2));
        pump.Dispose();
    }

    private async Task ShutdownOnPumpAsync(ManualResetEventSlim done)
    {
        try
        {
            await StopLoopAsync();
            core?.Dispose();
        }
        catch
        {
            // Best-effort shutdown.
        }
        finally
        {
            done.Set();
        }
    }

    private sealed record MusicMetaTemplate(string Title, string Artist, string Album, TimeSpan Duration);
}
