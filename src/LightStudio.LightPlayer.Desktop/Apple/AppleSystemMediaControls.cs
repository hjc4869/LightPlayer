using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LightStudio.LightPlayer.Services.Playback;

namespace LightStudio.LightPlayer.Desktop.Apple;

/// <summary>
/// macOS implementation of <see cref="ISystemMediaControls"/> built on the MediaPlayer framework:
/// <c>MPNowPlayingInfoCenter</c> publishes "now playing" metadata / artwork / timeline to Control
/// Center, the lock screen, and the media keys, while <c>MPRemoteCommandCenter</c> delivers the
/// hardware / OS transport requests back to the shared playback controller.
/// </summary>
/// <remarks>
/// <para>
/// The framework is driven directly through the Objective-C runtime (see <see cref="ObjC"/>) so the
/// NativeAOT desktop head needs no managed Cocoa binding. Transport commands are dispatched via a
/// small dynamically-registered Objective-C class whose methods are backed by
/// <see cref="UnmanagedCallersOnlyAttribute"/> function pointers; artwork uses the block-based
/// <c>MPMediaItemArtwork</c> initializer with a shared, capture-free request handler block.
/// </para>
/// <para>
/// All <see cref="ISystemMediaControls"/> members are invoked on the UI thread (the
/// <c>NSApplication</c> main thread, which owns an autorelease pool). Remote command callbacks arrive
/// on the main run loop; they are forwarded to instance state guarded by <see cref="gate"/> and the
/// controller re-marshals the resulting transport requests. Every public method is best-effort: the
/// contract requires media-control failures never disrupt playback.
/// </para>
/// </remarks>
internal sealed unsafe class AppleSystemMediaControls : ISystemMediaControls, IDisposable
{
    private const string AppKit = "/System/Library/Frameworks/AppKit.framework/AppKit";
    private const string MediaPlayer = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";

    // MPNowPlayingPlaybackState.
    private const int PlaybackStatePlaying = 1;
    private const int PlaybackStatePaused = 2;
    private const int PlaybackStateStopped = 3;

    // MPRemoteCommandHandlerStatusSuccess.
    private const nint CommandSuccess = 0;

    // Objective-C type encoding for "- (NSInteger)handler:(id)event": NSInteger self _cmd id.
    private const string CommandTypeEncoding = "q@:@";

    // A square bounds hint for MPMediaItemArtwork. The request handler returns the full-resolution
    // image regardless; album art is square, so this avoids reading NSImage.size (a struct return).
    private const double ArtworkBounds = 600.0;

    // Selectors are process-global and framework-independent, so they resolve eagerly and are shared.
    private static readonly IntPtr SelAlloc = ObjC.Sel("alloc");
    private static readonly IntPtr SelInit = ObjC.Sel("init");
    private static readonly IntPtr SelRetain = ObjC.Sel("retain");
    private static readonly IntPtr SelRelease = ObjC.Sel("release");
    private static readonly IntPtr SelAutorelease = ObjC.Sel("autorelease");
    private static readonly IntPtr SelDefaultCenter = ObjC.Sel("defaultCenter");
    private static readonly IntPtr SelSharedCommandCenter = ObjC.Sel("sharedCommandCenter");
    private static readonly IntPtr SelSetNowPlayingInfo = ObjC.Sel("setNowPlayingInfo:");
    private static readonly IntPtr SelSetPlaybackState = ObjC.Sel("setPlaybackState:");
    private static readonly IntPtr SelNumberWithDouble = ObjC.Sel("numberWithDouble:");
    private static readonly IntPtr SelSetObjectForKey = ObjC.Sel("setObject:forKey:");
    private static readonly IntPtr SelRemoveObjectForKey = ObjC.Sel("removeObjectForKey:");
    private static readonly IntPtr SelRemoveAllObjects = ObjC.Sel("removeAllObjects");
    private static readonly IntPtr SelSetEnabled = ObjC.Sel("setEnabled:");
    private static readonly IntPtr SelAddTargetAction = ObjC.Sel("addTarget:action:");
    private static readonly IntPtr SelRemoveTarget = ObjC.Sel("removeTarget:");
    private static readonly IntPtr SelPositionTime = ObjC.Sel("positionTime");
    private static readonly IntPtr SelDataWithBytesLength = ObjC.Sel("dataWithBytes:length:");
    private static readonly IntPtr SelInitWithData = ObjC.Sel("initWithData:");
    private static readonly IntPtr SelInitWithBoundsRequestHandler = ObjC.Sel("initWithBoundsSize:requestHandler:");

    // MPRemoteCommandCenter command accessors.
    private static readonly IntPtr SelPlayCommand = ObjC.Sel("playCommand");
    private static readonly IntPtr SelPauseCommand = ObjC.Sel("pauseCommand");
    private static readonly IntPtr SelTogglePlayPauseCommand = ObjC.Sel("togglePlayPauseCommand");
    private static readonly IntPtr SelNextTrackCommand = ObjC.Sel("nextTrackCommand");
    private static readonly IntPtr SelPreviousTrackCommand = ObjC.Sel("previousTrackCommand");
    private static readonly IntPtr SelChangePlaybackPositionCommand = ObjC.Sel("changePlaybackPositionCommand");
    private static readonly IntPtr SelSeekForwardCommand = ObjC.Sel("seekForwardCommand");
    private static readonly IntPtr SelSeekBackwardCommand = ObjC.Sel("seekBackwardCommand");
    private static readonly IntPtr SelSkipForwardCommand = ObjC.Sel("skipForwardCommand");
    private static readonly IntPtr SelSkipBackwardCommand = ObjC.Sel("skipBackwardCommand");
    private static readonly IntPtr SelStopCommand = ObjC.Sel("stopCommand");

    // Selectors of the dynamically-registered command target class.
    private static readonly IntPtr SelOnPlay = ObjC.Sel("onPlay:");
    private static readonly IntPtr SelOnPause = ObjC.Sel("onPause:");
    private static readonly IntPtr SelOnTogglePlayPause = ObjC.Sel("onTogglePlayPause:");
    private static readonly IntPtr SelOnNextTrack = ObjC.Sel("onNextTrack:");
    private static readonly IntPtr SelOnPreviousTrack = ObjC.Sel("onPreviousTrack:");
    private static readonly IntPtr SelOnChangePlaybackPosition = ObjC.Sel("onChangePlaybackPosition:");

    private static readonly object ArtworkGate = new();

    private static IntPtr commandTargetClass;
    private static IntPtr sharedArtworkBlock;

    // The NSImage (retained) that the shared artwork request handler returns. Guarded by ArtworkGate
    // because the handler may be invoked off the UI thread while a track change swaps the image.
    private static IntPtr currentArtworkImage;

    // The live instance the static command callbacks forward to (one media-control object per app).
    private static volatile AppleSystemMediaControls? current;

    private readonly object gate = new();

    private readonly IntPtr infoCenter;
    private readonly IntPtr commandCenter;
    private readonly IntPtr nsNumberClass;
    private readonly IntPtr nsDataClass;
    private readonly IntPtr nsImageClass;
    private readonly IntPtr artworkClass;

    private readonly IntPtr keyTitle;
    private readonly IntPtr keyArtist;
    private readonly IntPtr keyAlbum;
    private readonly IntPtr keyDuration;
    private readonly IntPtr keyElapsed;
    private readonly IntPtr keyRate;
    private readonly IntPtr keyArtwork;

    private IntPtr nowPlayingInfo;
    private IntPtr commandTarget;

    private bool hasItem;
    private bool isPlaying;
    private bool disposed;

    public AppleSystemMediaControls()
    {
        // AppKit (NSImage) and MediaPlayer may not be linked this early; loading is idempotent and
        // makes their classes and exported string constants resolvable through the runtime.
        ObjC.LoadLibrary(AppKit);
        var mediaPlayer = ObjC.LoadLibrary(MediaPlayer);

        keyTitle = ObjC.ReadStringConstant(mediaPlayer, "MPMediaItemPropertyTitle");
        keyArtist = ObjC.ReadStringConstant(mediaPlayer, "MPMediaItemPropertyArtist");
        keyAlbum = ObjC.ReadStringConstant(mediaPlayer, "MPMediaItemPropertyAlbumTitle");
        keyDuration = ObjC.ReadStringConstant(mediaPlayer, "MPMediaItemPropertyPlaybackDuration");
        keyArtwork = ObjC.ReadStringConstant(mediaPlayer, "MPMediaItemPropertyArtwork");
        keyElapsed = ObjC.ReadStringConstant(mediaPlayer, "MPNowPlayingInfoPropertyElapsedPlaybackTime");
        keyRate = ObjC.ReadStringConstant(mediaPlayer, "MPNowPlayingInfoPropertyPlaybackRate");

        nsNumberClass = ObjC.GetClass("NSNumber");
    nsDataClass = ObjC.GetClass("NSData");
        nsImageClass = ObjC.GetClass("NSImage");
        artworkClass = ObjC.GetClass("MPMediaItemArtwork");

        infoCenter = ObjC.Send(ObjC.GetClass("MPNowPlayingInfoCenter"), SelDefaultCenter);
        commandCenter = ObjC.Send(ObjC.GetClass("MPRemoteCommandCenter"), SelSharedCommandCenter);
        if (infoCenter == IntPtr.Zero || commandCenter == IntPtr.Zero)
        {
            throw new InvalidOperationException("macOS now-playing centers are unavailable.");
        }

        nowPlayingInfo = ObjC.Send(ObjC.Send(ObjC.GetClass("NSMutableDictionary"), SelAlloc), SelInit);
        commandTarget = ObjC.Send(ObjC.Send(EnsureCommandTargetClass(), SelAlloc), SelInit);

        current = this;
        EnsureArtworkBlock();
        WireCommands();

        ObjC.Send(infoCenter, SelSetPlaybackState, (IntPtr)PlaybackStateStopped);
    }

    public event Action? PlayPauseRequested;

    public event Action? NextRequested;

    public event Action? PreviousRequested;

    public event Action<TimeSpan>? SeekRequested;

    public void SetNowPlaying(string title, string artist, string album)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            SetStringValue(keyTitle, title);
            SetStringValue(keyArtist, artist);
            SetStringValue(keyAlbum, album);

            // A fresh track invalidates any artwork resolved for the previous one; the async artwork
            // resolver re-adds it via SetArtwork once the new album art is available.
            ObjC.Send(nowPlayingInfo, SelRemoveObjectForKey, keyArtwork);
            SetArtworkImage(IntPtr.Zero);
            PushNowPlaying();
        }
        catch
        {
            // Optional integration: metadata failures must never affect playback.
        }
    }

    public void SetPlaybackState(bool isPlaying, bool hasItem)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            lock (gate)
            {
                this.hasItem = hasItem;
                this.isPlaying = isPlaying && hasItem;
            }

            if (!hasItem)
            {
                // Nothing queued: clear the now-playing panel entirely.
                ObjC.Send(nowPlayingInfo, SelRemoveAllObjects);
                SetArtworkImage(IntPtr.Zero);
                ObjC.Send(infoCenter, SelSetNowPlayingInfo, IntPtr.Zero);
                ObjC.Send(infoCenter, SelSetPlaybackState, (IntPtr)PlaybackStateStopped);
                return;
            }

            SetDoubleValue(keyRate, isPlaying ? 1.0 : 0.0);
            PushNowPlaying();
            ObjC.Send(
                infoCenter,
                SelSetPlaybackState,
                (IntPtr)(isPlaying ? PlaybackStatePlaying : PlaybackStatePaused));
        }
        catch
        {
            // Best effort.
        }
    }

    public void SetArtwork(byte[]? artworkContent)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            var image = LoadImage(artworkContent);
            if (image == IntPtr.Zero)
            {
                SetArtworkImage(IntPtr.Zero);
                ObjC.Send(nowPlayingInfo, SelRemoveObjectForKey, keyArtwork);
                PushNowPlaying();
                return;
            }

            SetArtworkImage(image);
            var artwork = CreateArtwork();
            if (artwork != IntPtr.Zero)
            {
                ObjC.Send(nowPlayingInfo, SelSetObjectForKey, artwork, keyArtwork);
                ObjC.Send(artwork, SelRelease);
            }

            PushNowPlaying();
        }
        catch
        {
            // Artwork is optional; invalid image content must never affect playback.
        }
    }

    public void SetPlaybackProgress(TimeSpan position, TimeSpan duration)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            bool playing;
            lock (gate)
            {
                if (!hasItem)
                {
                    return;
                }

                playing = isPlaying;
            }

            var seconds = position > TimeSpan.Zero ? position.TotalSeconds : 0.0;
            var total = duration > TimeSpan.Zero ? duration.TotalSeconds : 0.0;
            if (total > 0 && seconds > total)
            {
                seconds = total;
            }

            if (total > 0)
            {
                SetDoubleValue(keyDuration, total);
            }
            else
            {
                ObjC.Send(nowPlayingInfo, SelRemoveObjectForKey, keyDuration);
            }

            SetDoubleValue(keyElapsed, seconds);
            SetDoubleValue(keyRate, playing ? 1.0 : 0.0);
            PushNowPlaying();
        }
        catch
        {
            // Best effort.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        if (ReferenceEquals(current, this))
        {
            current = null;
        }

        try
        {
            RemoveTarget(SelPlayCommand);
            RemoveTarget(SelPauseCommand);
            RemoveTarget(SelTogglePlayPauseCommand);
            RemoveTarget(SelNextTrackCommand);
            RemoveTarget(SelPreviousTrackCommand);
            RemoveTarget(SelChangePlaybackPositionCommand);

            ObjC.Send(infoCenter, SelSetNowPlayingInfo, IntPtr.Zero);
            ObjC.Send(infoCenter, SelSetPlaybackState, (IntPtr)PlaybackStateStopped);
        }
        catch
        {
            // Disposal is best-effort during shutdown.
        }

        SetArtworkImage(IntPtr.Zero);

        if (nowPlayingInfo != IntPtr.Zero)
        {
            ObjC.Send(nowPlayingInfo, SelRelease);
            nowPlayingInfo = IntPtr.Zero;
        }

        if (commandTarget != IntPtr.Zero)
        {
            ObjC.Send(commandTarget, SelRelease);
            commandTarget = IntPtr.Zero;
        }
    }

    // ---- Now-playing dictionary helpers (UI thread) --------------------

    private void SetStringValue(IntPtr key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            ObjC.Send(nowPlayingInfo, SelRemoveObjectForKey, key);
            return;
        }

        var text = ObjC.CreateString(value);
        ObjC.Send(nowPlayingInfo, SelSetObjectForKey, text, key);
        ObjC.CFRelease(text);
    }

    private void SetDoubleValue(IntPtr key, double value)
    {
        var number = ObjC.Send(nsNumberClass, SelNumberWithDouble, value);
        ObjC.Send(nowPlayingInfo, SelSetObjectForKey, number, key);
    }

    private void PushNowPlaying() => ObjC.Send(infoCenter, SelSetNowPlayingInfo, nowPlayingInfo);

    private IntPtr LoadImage(byte[]? artworkContent)
    {
        if (artworkContent is not { Length: > 0 }
            || nsDataClass == IntPtr.Zero
            || nsImageClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        fixed (byte* content = artworkContent)
        {
            var data = ObjC.Send(
                nsDataClass, SelDataWithBytesLength, (IntPtr)content, (IntPtr)artworkContent.Length);
            if (data == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            // -[NSImage initWithData:] returns a +1 image, or nil (freeing the allocation) when
            // the encoded image cannot be read.
            var allocated = ObjC.Send(nsImageClass, SelAlloc);
            return ObjC.Send(allocated, SelInitWithData, data);
        }
    }

    private IntPtr CreateArtwork()
    {
        if (artworkClass == IntPtr.Zero || sharedArtworkBlock == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var allocated = ObjC.Send(artworkClass, SelAlloc);
        return ObjC.Send(
            allocated, SelInitWithBoundsRequestHandler, ArtworkBounds, ArtworkBounds, sharedArtworkBlock);
    }

    // ---- Remote command wiring -----------------------------------------

    private void WireCommands()
    {
        EnableCommand(SelPlayCommand, SelOnPlay);
        EnableCommand(SelPauseCommand, SelOnPause);
        EnableCommand(SelTogglePlayPauseCommand, SelOnTogglePlayPause);
        EnableCommand(SelNextTrackCommand, SelOnNextTrack);
        EnableCommand(SelPreviousTrackCommand, SelOnPreviousTrack);
        EnableCommand(SelChangePlaybackPositionCommand, SelOnChangePlaybackPosition);

        // Disable transport commands the player does not implement so the OS does not surface
        // inert controls.
        DisableCommand(SelSeekForwardCommand);
        DisableCommand(SelSeekBackwardCommand);
        DisableCommand(SelSkipForwardCommand);
        DisableCommand(SelSkipBackwardCommand);
        DisableCommand(SelStopCommand);
    }

    private void EnableCommand(IntPtr commandSelector, IntPtr actionSelector)
    {
        var command = ObjC.Send(commandCenter, commandSelector);
        if (command == IntPtr.Zero)
        {
            return;
        }

        ObjC.Send(command, SelSetEnabled, ObjC.Yes);
        ObjC.Send(command, SelAddTargetAction, commandTarget, actionSelector);
    }

    private void DisableCommand(IntPtr commandSelector)
    {
        var command = ObjC.Send(commandCenter, commandSelector);
        if (command != IntPtr.Zero)
        {
            ObjC.Send(command, SelSetEnabled, ObjC.No);
        }
    }

    private void RemoveTarget(IntPtr commandSelector)
    {
        var command = ObjC.Send(commandCenter, commandSelector);
        if (command != IntPtr.Zero)
        {
            ObjC.Send(command, SelRemoveTarget, commandTarget);
        }
    }

    private static IntPtr EnsureCommandTargetClass()
    {
        if (commandTargetClass != IntPtr.Zero)
        {
            return commandTargetClass;
        }

        var cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "LPMediaCommandTarget");
        ObjC.AddMethod(
            cls, SelOnPlay, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnPlay, CommandTypeEncoding);
        ObjC.AddMethod(
            cls, SelOnPause, (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnPause, CommandTypeEncoding);
        ObjC.AddMethod(
            cls,
            SelOnTogglePlayPause,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnTogglePlayPause,
            CommandTypeEncoding);
        ObjC.AddMethod(
            cls,
            SelOnNextTrack,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnNextTrack,
            CommandTypeEncoding);
        ObjC.AddMethod(
            cls,
            SelOnPreviousTrack,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnPreviousTrack,
            CommandTypeEncoding);
        ObjC.AddMethod(
            cls,
            SelOnChangePlaybackPosition,
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nint>)&OnChangePlaybackPosition,
            CommandTypeEncoding);
        ObjC.RegisterClassPair(cls);

        commandTargetClass = cls;
        return cls;
    }

    private static void EnsureArtworkBlock()
    {
        if (sharedArtworkBlock != IntPtr.Zero)
        {
            return;
        }

        sharedArtworkBlock = ObjC.CreateGlobalBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, double, double, IntPtr>)&ArtworkRequestHandler);
    }

    // ---- Artwork image lifetime (shared with the request handler) ------

    private static void SetArtworkImage(IntPtr image)
    {
        IntPtr previous;
        lock (ArtworkGate)
        {
            previous = currentArtworkImage;
            currentArtworkImage = image;
        }

        if (previous != IntPtr.Zero)
        {
            ObjC.Send(previous, SelRelease);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr ArtworkRequestHandler(IntPtr block, double width, double height)
    {
        IntPtr image;
        lock (ArtworkGate)
        {
            image = currentArtworkImage;
            if (image != IntPtr.Zero)
            {
                ObjC.Send(image, SelRetain);
            }
        }

        // Return a balanced (+0, autoreleased) reference so releasing our retained copy after a
        // track change cannot free an image the framework is still reading.
        if (image != IntPtr.Zero)
        {
            ObjC.Send(image, SelAutorelease);
        }

        return image;
    }

    // ---- Remote command callbacks (main run loop) ----------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPlay(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        current?.RaisePlay();
        return CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPause(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        current?.RaisePause();
        return CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnTogglePlayPause(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        current?.RaiseToggle();
        return CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnNextTrack(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        current?.RaiseNext();
        return CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnPreviousTrack(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        current?.RaisePrevious();
        return CommandSuccess;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint OnChangePlaybackPosition(IntPtr self, IntPtr cmd, IntPtr commandEvent)
    {
        if (current is { } instance)
        {
            instance.RaiseSeek(ObjC.SendDouble(commandEvent, SelPositionTime));
        }

        return CommandSuccess;
    }

    private void RaisePlay()
    {
        lock (gate)
        {
            if (disposed || !hasItem || isPlaying)
            {
                return;
            }
        }

        // The controller's transport hook is a toggle; only fire it when actually paused so an
        // explicit Play never accidentally pauses.
        PlayPauseRequested?.Invoke();
    }

    private void RaisePause()
    {
        lock (gate)
        {
            if (disposed || !hasItem || !isPlaying)
            {
                return;
            }
        }

        PlayPauseRequested?.Invoke();
    }

    private void RaiseToggle()
    {
        lock (gate)
        {
            if (disposed || !hasItem)
            {
                return;
            }
        }

        PlayPauseRequested?.Invoke();
    }

    private void RaiseNext()
    {
        lock (gate)
        {
            if (disposed || !hasItem)
            {
                return;
            }
        }

        NextRequested?.Invoke();
    }

    private void RaisePrevious()
    {
        lock (gate)
        {
            if (disposed || !hasItem)
            {
                return;
            }
        }

        PreviousRequested?.Invoke();
    }

    private void RaiseSeek(double seconds)
    {
        lock (gate)
        {
            if (disposed || !hasItem)
            {
                return;
            }
        }

        if (seconds < 0)
        {
            seconds = 0;
        }

        SeekRequested?.Invoke(TimeSpan.FromSeconds(seconds));
    }
}
