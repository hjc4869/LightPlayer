using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace LightStudio.LightPlayer.Services;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? LatestVersionText = null,
    string? DownloadUrl = null,
    string? ErrorMessage = null);

public interface IUpdateCheckService
{
    bool IsSupported { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
}

public interface IBrowserUpdateService : INotifyPropertyChanged
{
    bool AutoCheckEnabled { get; set; }

    Task CheckForUpdatesAsync();
}