using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LightPlayer.Server;

internal static class PhotoIndexCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        AppLog.ConfigureConsole();
        try
        {
            var config = ServerConfiguration.Load();
            var host = ServerLibraryHost.GetOrCreate(config);
            ServerLibraryHost.ApplyFolders(host, config);
            var service = host.PhotoIndex ?? throw new NotSupportedException("Photo inference is unavailable on this host.");
            await service.InitializeAsync().ConfigureAwait(false);
            switch (args.Length > 1 ? args[1] : "status")
            {
                case "devices":
                    foreach (var device in service.Status.Devices)
                        Console.WriteLine($"{device.Id}\t{device.Name}");
                    break;
                case "import" when args.Length == 3:
                    await service.ImportModelAsync(args[2]).ConfigureAwait(false);
                    break;
                case "models":
                    var catalog = service.GetModelCatalog(CultureInfo.CurrentUICulture);
                    foreach (var model in catalog.Models)
                        Console.WriteLine($"{model.Id}\t{model.Name}\t{model.Description}\t{model.DownloadUri}");
                    if (!string.IsNullOrEmpty(catalog.Description))
                        Console.WriteLine(catalog.Description);
                    break;
                case "enable":
                    var deviceId = args.Skip(2).FirstOrDefault(value => value != "--remote") ?? service.Status.Options.DeviceId;
                    await service.ConfigureAsync(new PhotoIndexOptions(true, deviceId, args.Contains("--remote"))).ConfigureAwait(false);
                    if (service.GetModelCatalog(CultureInfo.CurrentUICulture).Description is { Length: > 0 } description)
                        Console.WriteLine(description);
                    break;
                case "disable":
                    await service.ConfigureAsync(service.Status.Options with { Enabled = false, AllowRemoteSearch = false }).ConfigureAwait(false);
                    break;
                case "scan":
                    if (!service.Status.IsAvailable)
                        throw new InvalidOperationException("Import a compatible photo model and enable photo indexing before scanning.");
                    await service.IndexAsync().ConfigureAwait(false);
                    break;
                case "status":
                    break;
                default:
                    Console.Error.WriteLine("photo-index [status | devices | models | import <model-path> | enable [device-id] [--remote] | disable | scan]");
                    return 1;
            }
            var status = service.Status;
            Console.WriteLine($"Available={status.IsAvailable}; model={status.ModelName}; device={status.ActiveDeviceId}; remote={status.Options.AllowRemoteSearch}; indexed={status.IndexedCount}; failed={status.FailedCount}.");
            if (status.Error is not null)
                throw new InvalidOperationException(status.Error);
            (service as IDisposable)?.Dispose();
            return 0;
        }
        catch (Exception exception)
        {
            AppLog.Error("PhotoIndex", "Photo indexing command failed.", exception);
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            AppLog.Shutdown();
        }
    }
}