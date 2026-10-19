using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.MediaLibraryCore.IO;

namespace LightStudio.MediaLibraryCore.Library;

internal sealed class ScanWorkBudget(IMultiMediaPlatformIO platform, CancellationToken cancellationToken)
{
    private long started = Stopwatch.GetTimestamp();
    private int items;

    internal ValueTask YieldAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsBrowser() || ++items < 64 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 4)
            return ValueTask.CompletedTask;
        return NextTurnAsync();
    }

    private async ValueTask NextTurnAsync()
    {
        await platform.YieldScanAsync(cancellationToken).ConfigureAwait(false);
        items = 0;
        started = Stopwatch.GetTimestamp();
    }
}
