using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Desktop.Apple;

internal readonly record struct PhotoLibraryResource(IntPtr Resource, string FileName, long Size);

/// <summary>PhotoKit access for <see cref="MacMediaLibraryProvider"/>; every read leaves network access disabled.</summary>
internal static unsafe class MacPhotoLibrary
{
    public const string Scheme = "photos-library://asset/";

    private const string PhotosFramework = "/System/Library/Frameworks/Photos.framework/Photos";
    private const string AVFoundationFramework = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";
    private const nint ReadWriteAccess = 2;
    private const nint MediaTypeImage = 1;
    private const nint MediaTypeVideo = 2;
    private const nint SourceUserLibrary = 1;
    private const nint SourceITunesSynced = 4;
    private const nint ResourcePhoto = 1;
    private const nint ResourceVideo = 2;
    private const nint ResourceFullSizePhoto = 5;
    private const nint ResourceFullSizeVideo = 6;
    private const nint HighQualityDelivery = 1;
    private const nint FastResize = 1;
    private const nint BitmapJpeg = 3;

    private static readonly Lazy<IntPtr> Framework = new(() =>
    {
        ObjC.LoadLibrary(AVFoundationFramework);
        return ObjC.LoadLibrary(PhotosFramework);
    });

    private static readonly IntPtr LocallyAvailableKey = ObjC.CreateString("locallyAvailable");
    private static readonly IntPtr FileSizeKey = ObjC.CreateString("fileSize");
    private static Action? changed;
    private static IntPtr observer;

    public static nint AuthorizationStatus => ObjC.Send(PhotoLibraryClass, ObjC.Sel("authorizationStatusForAccessLevel:"), ReadWriteAccess);

    public static bool IsAuthorized => AuthorizationStatus is 3 or 4;

    public static bool IsUndetermined => AuthorizationStatus == 0;

    private static IntPtr PhotoLibraryClass
    {
        get
        {
            _ = Framework.Value;
            return ObjC.GetClass("PHPhotoLibrary");
        }
    }

    private static IntPtr ImageManager => ObjC.Send(ObjC.GetClass("PHImageManager"), ObjC.Sel("defaultManager"));

    public static Task<nint> RequestAuthorizationAsync()
    {
        var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        var block = ObjC.CreateContextBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, nint, void>)&OnAuthorization, GCHandle.ToIntPtr(handle));
        try
        {
            ObjC.Send(PhotoLibraryClass, ObjC.Sel("requestAuthorizationForAccessLevel:handler:"), ReadWriteAccess, block);
        }
        finally
        {
            ObjC.FreeContextBlock(block);
        }

        return completion.Task;
    }

    /// <summary>Calls <paramref name="callback"/> on photo library changes once access is granted.</summary>
    public static void Observe(Action callback)
    {
        changed = callback;
        if (observer != IntPtr.Zero || !IsAuthorized)
            return;
        var cls = ObjC.GetClass("LPPhotoLibraryObserver");
        if (cls == IntPtr.Zero)
        {
            cls = ObjC.AllocateClassPair(ObjC.GetClass("NSObject"), "LPPhotoLibraryObserver");
            ObjC.AddMethod(cls, ObjC.Sel("photoLibraryDidChange:"),
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnLibraryChanged, "v@:@");
            ObjC.RegisterClassPair(cls);
        }

        observer = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(ObjC.Send(PhotoLibraryClass, ObjC.Sel("sharedPhotoLibrary")), ObjC.Sel("registerChangeObserver:"), observer);
    }

    public static void StopObserving()
    {
        changed = null;
        if (observer == IntPtr.Zero)
            return;
        ObjC.Send(ObjC.Send(PhotoLibraryClass, ObjC.Sel("sharedPhotoLibrary")), ObjC.Sel("unregisterChangeObserver:"), observer);
        ObjC.Send(observer, ObjC.Sel("release"));
        observer = IntPtr.Zero;
    }

    /// <summary>Reads the locally stored photos and videos of the user library and synced albums.</summary>
    public static void ReadAssets(Action<IntPtr, PhotoLibraryResource> read, CancellationToken cancellationToken)
    {
        var pool = ObjC.PushAutoreleasePool();
        try
        {
            var options = ObjC.Send(ObjC.Send(ObjC.GetClass("PHFetchOptions"), ObjC.Sel("alloc")), ObjC.Sel("init"));
            ObjC.Send(options, ObjC.Sel("setIncludeAssetSourceTypes:"), SourceUserLibrary | SourceITunesSynced);
            var assets = ObjC.Send(ObjC.GetClass("PHAsset"), ObjC.Sel("fetchAssetsWithOptions:"), options);
            ObjC.Send(options, ObjC.Sel("release"));
            var count = (long)ObjC.Send(assets, ObjC.Sel("count"));
            var skipped = 0;
            for (long index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemPool = ObjC.PushAutoreleasePool();
                try
                {
                    var asset = ObjC.Send(assets, ObjC.Sel("objectAtIndex:"), (IntPtr)index);
                    if (MediaType(asset) is not (MediaTypeImage or MediaTypeVideo))
                        continue;
                    if (DescribeLocalResource(asset) is { } resource)
                        read(asset, resource);
                    else
                        skipped++;
                }
                finally
                {
                    ObjC.PopAutoreleasePool(itemPool);
                }
            }

            if (skipped > 0)
                AppLog.Information("macOS.MediaLibrary", $"Skipped {skipped} photo library items that are not stored on this Mac.");
        }
        finally
        {
            ObjC.PopAutoreleasePool(pool);
        }
    }

    public static string Path(IntPtr asset, PhotoLibraryResource resource) =>
        Scheme + Uri.EscapeDataString(LocalIdentifier(asset)) + "/" + Uri.EscapeDataString(resource.FileName);

    public static bool IsVideo(IntPtr asset) => MediaType(asset) == MediaTypeVideo;

    public static int PixelWidth(IntPtr asset) => (int)ObjC.Send(asset, ObjC.Sel("pixelWidth"));

    public static int PixelHeight(IntPtr asset) => (int)ObjC.Send(asset, ObjC.Sel("pixelHeight"));

    public static double Duration(IntPtr asset) => ObjC.SendDouble(asset, ObjC.Sel("duration"));

    public static DateTimeOffset? CreationDate(IntPtr asset) => Date(ObjC.Send(asset, ObjC.Sel("creationDate")));

    public static DateTimeOffset? ModificationDate(IntPtr asset) => Date(ObjC.Send(asset, ObjC.Sel("modificationDate")));

    /// <summary>The system-localized name of the all-items album.</summary>
    public static string? LibraryFolderName()
    {
        var pool = ObjC.PushAutoreleasePool();
        try
        {
            var collections = ObjC.Send(ObjC.GetClass("PHAssetCollection"),
                ObjC.Sel("fetchAssetCollectionsWithType:subtype:options:"), (IntPtr)2, (IntPtr)209, IntPtr.Zero);
            return ObjC.ReadString(ObjC.Send(ObjC.Send(collections, ObjC.Sel("firstObject")), ObjC.Sel("localizedTitle")));
        }
        finally
        {
            ObjC.PopAutoreleasePool(pool);
        }
    }

    /// <summary>Runs <paramref name="read"/> with the asset of a photo library identifier.</summary>
    public static T WithAsset<T>(string path, Func<IntPtr, T> read)
    {
        var start = Scheme.Length;
        var end = path.IndexOf('/', start);
        if (!path.StartsWith(Scheme, StringComparison.Ordinal) || end <= start)
            throw new FileNotFoundException("Invalid photo library identifier.", path);
        _ = Framework.Value;
        var pool = ObjC.PushAutoreleasePool();
        var identifier = ObjC.CreateString(Uri.UnescapeDataString(path[start..end]));
        try
        {
            var identifiers = ObjC.Send(ObjC.GetClass("NSArray"), ObjC.Sel("arrayWithObject:"), identifier);
            var assets = ObjC.Send(ObjC.GetClass("PHAsset"), ObjC.Sel("fetchAssetsWithLocalIdentifiers:options:"),
                identifiers, IntPtr.Zero);
            var asset = ObjC.Send(assets, ObjC.Sel("firstObject"));
            return asset == IntPtr.Zero
                ? throw new FileNotFoundException("The item is no longer in the photo library.", path)
                : read(asset);
        }
        finally
        {
            ObjC.CFRelease(identifier);
            ObjC.PopAutoreleasePool(pool);
        }
    }

    /// <summary>Selects the asset's current-version resource; null when its data is not stored on this Mac.</summary>
    public static PhotoLibraryResource? DescribeLocalResource(IntPtr asset)
    {
        var video = MediaType(asset) == MediaTypeVideo;
        var resources = ObjC.Send(ObjC.GetClass("PHAssetResource"), ObjC.Sel("assetResourcesForAsset:"), asset);
        var count = (long)ObjC.Send(resources, ObjC.Sel("count"));
        IntPtr original = IntPtr.Zero;
        IntPtr current = IntPtr.Zero;
        for (long index = 0; index < count; index++)
        {
            var resource = ObjC.Send(resources, ObjC.Sel("objectAtIndex:"), (IntPtr)index);
            var type = ObjC.Send(resource, ObjC.Sel("type"));
            if (original == IntPtr.Zero && type == (video ? ResourceVideo : ResourcePhoto))
                original = resource;
            else if (current == IntPtr.Zero && type == (video ? ResourceFullSizeVideo : ResourceFullSizePhoto))
                current = resource;
        }

        var primary = current != IntPtr.Zero ? current : original;
        if (primary == IntPtr.Zero || !IsLocallyAvailable(primary))
            return null;
        var name = FileName(original != IntPtr.Zero ? original : primary);
        if (current != IntPtr.Zero && System.IO.Path.GetExtension(FileName(current)) is { Length: > 0 } extension)
            name = System.IO.Path.ChangeExtension(name, extension);
        return new PhotoLibraryResource(primary, name, ReadNumber(primary, "fileSize", FileSizeKey)?.Int64 ?? 0);
    }

    /// <summary>Reads the current version of a photo into memory.</summary>
    public static Stream OpenPhoto(IntPtr asset, string path)
    {
        var pool = ObjC.PushAutoreleasePool();
        var options = ImageOptions(resize: false);
        var result = new ImageResult();
        var handle = GCHandle.Alloc(result);
        var block = ObjC.CreateContextBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, uint, IntPtr, void>)&OnImageData, GCHandle.ToIntPtr(handle));
        try
        {
            ObjC.Send(ImageManager, ObjC.Sel("requestImageDataAndOrientationForAsset:options:resultHandler:"), asset, options, block);
        }
        finally
        {
            ObjC.FreeContextBlock(block);
            handle.Free();
            ObjC.Send(options, ObjC.Sel("release"));
            ObjC.PopAutoreleasePool(pool);
        }

        return result.Object != IntPtr.Zero
            ? new DataStream(result.Object)
            : throw new IOException($"Could not read {path} from the photo library: {result.Error}");
    }

    /// <summary>A JPEG preview of an asset from the photo library's own derivatives.</summary>
    public static byte[]? Thumbnail(IntPtr asset)
    {
        var options = ImageOptions(resize: true);
        var result = new ImageResult();
        var handle = GCHandle.Alloc(result);
        var block = ObjC.CreateContextBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnImage, GCHandle.ToIntPtr(handle));
        try
        {
            ObjC.Send(ImageManager, ObjC.Sel("requestImageForAsset:targetSize:contentMode:options:resultHandler:"),
                asset, 512, 512, IntPtr.Zero, options, block);
        }
        finally
        {
            ObjC.FreeContextBlock(block);
            handle.Free();
            ObjC.Send(options, ObjC.Sel("release"));
        }

        if (result.Object == IntPtr.Zero)
        {
            AppLog.Warning("macOS.MediaLibrary", $"Could not read a photo library preview: {result.Error}");
            return null;
        }

        try
        {
            var tiff = ObjC.Send(result.Object, ObjC.Sel("TIFFRepresentation"));
            var bitmap = ObjC.Send(ObjC.GetClass("NSBitmapImageRep"), ObjC.Sel("imageRepWithData:"), tiff);
            var jpeg = ObjC.Send(bitmap, ObjC.Sel("representationUsingType:properties:"), BitmapJpeg,
                ObjC.Send(ObjC.GetClass("NSDictionary"), ObjC.Sel("dictionary")));
            return CopyData(jpeg);
        }
        finally
        {
            ObjC.Send(result.Object, ObjC.Sel("release"));
        }
    }

    /// <summary>Returns the library file of a video, or null when the requested version has no file of its own.</summary>
    public static Task<string?> RequestVideoPathAsync(IntPtr asset, bool original, string path)
    {
        var options = ObjC.Send(ObjC.Send(ObjC.GetClass("PHVideoRequestOptions"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(options, ObjC.Sel("setNetworkAccessAllowed:"), ObjC.No);
        ObjC.Send(options, ObjC.Sel("setVersion:"), (IntPtr)(original ? 1 : 0));
        ObjC.Send(options, ObjC.Sel("setDeliveryMode:"), HighQualityDelivery);
        var request = new VideoRequest(path);
        var handle = GCHandle.Alloc(request);
        var block = ObjC.CreateContextBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnVideo, GCHandle.ToIntPtr(handle));
        try
        {
            ObjC.Send(ImageManager, ObjC.Sel("requestAVAssetForVideo:options:resultHandler:"), asset, options, block);
        }
        catch
        {
            handle.Free();
            throw;
        }
        finally
        {
            ObjC.FreeContextBlock(block);
            ObjC.Send(options, ObjC.Sel("release"));
        }

        return request.Completion.Task;
    }

    /// <summary>Copies a locally stored asset resource to <paramref name="file"/>.</summary>
    public static Task ExportAsync(PhotoLibraryResource resource, string file)
    {
        var target = ObjC.CreateString(file);
        var options = ObjC.Send(ObjC.Send(ObjC.GetClass("PHAssetResourceRequestOptions"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(options, ObjC.Sel("setNetworkAccessAllowed:"), ObjC.No);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(completion);
        var block = ObjC.CreateContextBlock(
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&OnWritten, GCHandle.ToIntPtr(handle));
        try
        {
            var url = ObjC.Send(ObjC.GetClass("NSURL"), ObjC.Sel("fileURLWithPath:"), target);
            var manager = ObjC.Send(ObjC.GetClass("PHAssetResourceManager"), ObjC.Sel("defaultManager"));
            ObjC.Send(manager, ObjC.Sel("writeDataForAssetResource:toFile:options:completionHandler:"),
                resource.Resource, url, options, block);
        }
        catch
        {
            handle.Free();
            throw;
        }
        finally
        {
            ObjC.FreeContextBlock(block);
            ObjC.Send(options, ObjC.Sel("release"));
            ObjC.CFRelease(target);
        }

        return completion.Task;
    }

    private static nint MediaType(IntPtr asset) => ObjC.Send(asset, ObjC.Sel("mediaType"));

    private static string LocalIdentifier(IntPtr asset) => ObjC.ReadString(ObjC.Send(asset, ObjC.Sel("localIdentifier"))) ?? string.Empty;

    private static string FileName(IntPtr resource) =>
        ObjC.ReadString(ObjC.Send(resource, ObjC.Sel("originalFilename"))) ?? string.Empty;

    // Photo library resources report storage state through key-value properties; when absent the item is treated as local
    // and network access stays disabled on every read.
    private static bool IsLocallyAvailable(IntPtr resource) =>
        ReadNumber(resource, "locallyAvailable", LocallyAvailableKey)?.Bool != false;

    private static (bool Bool, long Int64)? ReadNumber(IntPtr target, string name, IntPtr key)
    {
        if (ObjC.SendBool(target, ObjC.Sel("respondsToSelector:"), ObjC.Sel(name)) == ObjC.No)
            return null;
        var number = ObjC.Send(target, ObjC.Sel("valueForKey:"), key);
        return number == IntPtr.Zero
            ? null
            : (ObjC.SendBool(number, ObjC.Sel("boolValue")) != ObjC.No, (long)ObjC.Send(number, ObjC.Sel("longLongValue")));
    }

    private static DateTimeOffset? Date(IntPtr date) => date == IntPtr.Zero
        ? null
        : DateTimeOffset.FromUnixTimeMilliseconds((long)(ObjC.SendDouble(date, ObjC.Sel("timeIntervalSince1970")) * 1000));

    private static IntPtr ImageOptions(bool resize)
    {
        var options = ObjC.Send(ObjC.Send(ObjC.GetClass("PHImageRequestOptions"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        ObjC.Send(options, ObjC.Sel("setNetworkAccessAllowed:"), ObjC.No);
        ObjC.Send(options, ObjC.Sel("setDeliveryMode:"), HighQualityDelivery);
        ObjC.Send(options, ObjC.Sel("setSynchronous:"), ObjC.Yes);
        if (resize)
            ObjC.Send(options, ObjC.Sel("setResizeMode:"), FastResize);
        return options;
    }

    private static byte[]? CopyData(IntPtr data)
    {
        var length = (long)ObjC.Send(data, ObjC.Sel("length"));
        if (data == IntPtr.Zero || length <= 0)
            return null;
        var bytes = new byte[length];
        Marshal.Copy(ObjC.Send(data, ObjC.Sel("bytes")), bytes, 0, bytes.Length);
        return bytes;
    }

    private static string Describe(IntPtr info)
    {
        if (info == IntPtr.Zero)
            return "no details";
        var inCloud = ObjC.Send(info, ObjC.Sel("objectForKey:"), ObjC.ReadStringConstant(Framework.Value, "PHImageResultIsInCloudKey"));
        if (inCloud != IntPtr.Zero && ObjC.SendBool(inCloud, ObjC.Sel("boolValue")) != ObjC.No)
            return "the item is stored in iCloud";
        var error = ObjC.Send(info, ObjC.Sel("objectForKey:"), ObjC.ReadStringConstant(Framework.Value, "PHImageErrorKey"));
        return ObjC.ReadString(ObjC.Send(error != IntPtr.Zero ? error : info, ObjC.Sel("description"))) ?? "no details";
    }

    private static string DescribeError(IntPtr error) =>
        ObjC.ReadString(ObjC.Send(error, ObjC.Sel("localizedDescription"))) ?? "unknown error";

    private static T Target<T>(IntPtr block) => (T)GCHandle.FromIntPtr(ObjC.GetBlockContext(block)).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAuthorization(IntPtr block, nint status)
    {
        var handle = GCHandle.FromIntPtr(ObjC.GetBlockContext(block));
        var completion = (TaskCompletionSource<nint>)handle.Target!;
        handle.Free();
        completion.TrySetResult(status);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnImageData(IntPtr block, IntPtr data, IntPtr uti, uint orientation, IntPtr info)
    {
        try
        {
            var result = Target<ImageResult>(block);
            result.Object = data != IntPtr.Zero ? ObjC.Send(data, ObjC.Sel("retain")) : IntPtr.Zero;
            result.Error = data == IntPtr.Zero ? Describe(info) : null;
        }
        catch (Exception exception)
        {
            AppLog.Error("macOS.MediaLibrary", "Could not receive photo library data.", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnImage(IntPtr block, IntPtr image, IntPtr info)
    {
        try
        {
            var result = Target<ImageResult>(block);
            result.Object = image != IntPtr.Zero ? ObjC.Send(image, ObjC.Sel("retain")) : IntPtr.Zero;
            result.Error = image == IntPtr.Zero ? Describe(info) : null;
        }
        catch (Exception exception)
        {
            AppLog.Error("macOS.MediaLibrary", "Could not receive a photo library preview.", exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnVideo(IntPtr block, IntPtr asset, IntPtr audioMix, IntPtr info)
    {
        var handle = GCHandle.FromIntPtr(ObjC.GetBlockContext(block));
        var request = (VideoRequest)handle.Target!;
        handle.Free();
        try
        {
            if (asset == IntPtr.Zero)
            {
                request.Completion.TrySetException(
                    new IOException($"Could not read {request.Path} from the photo library: {Describe(info)}"));
                return;
            }

            var url = ObjC.SendBool(asset, ObjC.Sel("isKindOfClass:"), ObjC.GetClass("AVURLAsset")) != ObjC.No
                ? ObjC.Send(asset, ObjC.Sel("URL"))
                : IntPtr.Zero;
            request.Completion.TrySetResult(url != IntPtr.Zero && ObjC.SendBool(url, ObjC.Sel("isFileURL")) != ObjC.No
                ? ObjC.ReadString(ObjC.Send(url, ObjC.Sel("path")))
                : null);
        }
        catch (Exception exception)
        {
            request.Completion.TrySetException(exception);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWritten(IntPtr block, IntPtr error)
    {
        var handle = GCHandle.FromIntPtr(ObjC.GetBlockContext(block));
        var completion = (TaskCompletionSource)handle.Target!;
        handle.Free();
        if (error == IntPtr.Zero)
            completion.TrySetResult();
        else
            completion.TrySetException(new IOException($"Could not copy from the photo library: {DescribeError(error)}"));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLibraryChanged(IntPtr self, IntPtr selector, IntPtr change)
    {
        try
        {
            changed?.Invoke();
        }
        catch (Exception exception)
        {
            AppLog.Error("macOS.MediaLibrary", "Could not handle a photo library change.", exception);
        }
    }

    private sealed class ImageResult
    {
        public IntPtr Object;
        public string? Error;
    }

    private sealed class VideoRequest(string path)
    {
        public string Path { get; } = path;
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A read-only view of a retained <c>NSData</c>, released on disposal.</summary>
    private sealed class DataStream : UnmanagedMemoryStream
    {
        private IntPtr data;

        public DataStream(IntPtr data)
            : base((byte*)ObjC.Send(data, ObjC.Sel("bytes")), (long)ObjC.Send(data, ObjC.Sel("length")))
        {
            this.data = data;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            var retained = Interlocked.Exchange(ref data, IntPtr.Zero);
            if (retained != IntPtr.Zero)
                ObjC.Send(retained, ObjC.Sel("release"));
        }
    }
}
