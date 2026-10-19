using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;

namespace LightStudio.LightPlayer.Web;

/// <summary>
/// Backs the shell's navigation with the browser's own history: every shell location becomes a
/// history entry whose URL hash fragment holds it, so the browser's back, forward and reload drive
/// the app the way the system back gesture does on Android. Locations are already percent-escaped
/// by the shell, so the hash is passed through verbatim.
/// </summary>
[SupportedOSPlatform("browser")]
internal sealed partial class BrowserNavigationHistory : INavigationHistory
{
    private string? currentLocation;
    private int currentIndex;

    private BrowserNavigationHistory(string? currentLocation, int currentIndex)
    {
        this.currentLocation = currentLocation;
        this.currentIndex = currentIndex;
    }

    public event Action<string?, int>? LocationChanged;

    public string? CurrentLocation => Volatile.Read(ref currentLocation);

    public int CurrentIndex => Volatile.Read(ref currentIndex);

    public static async Task<BrowserNavigationHistory> CreateAsync()
    {
        var location = await GetLocationAsync();
        var index = (int)await GetIndexAsync();
        var history = new BrowserNavigationHistory(
            location is { Length: > 0 } ? location : null,
            index);
        _ = history.ListenAsync();
        return history;
    }

    public void Replace(string location, int index)
    {
        Volatile.Write(ref currentLocation, location);
        Volatile.Write(ref currentIndex, index);
        _ = ReplaceEntryAsync(location, index);
    }

    public void Push(string location, int index)
    {
        Volatile.Write(ref currentLocation, location);
        Volatile.Write(ref currentIndex, index);
        _ = PushEntryAsync(location, index);
    }

    public void Go(int delta) => _ = GoToAsync(delta);

    /// <summary>
    /// Reports each move the browser makes through the history. Moves are awaited as promises rather
    /// than pushed through a marshalled callback, which multithreaded WebAssembly refuses to invoke
    /// from a JS event handler. The awaits are not configured away from the JS synchronization
    /// context, so the loop stays on the thread that owns the history's JS module.
    /// </summary>
    private async Task ListenAsync()
    {
        while (true)
        {
            await WaitForNavigationAsync();

            // Always reports where the browser is now rather than where each move landed, so a burst
            // of moves collapses into the state the user ended on.
            var location = await GetLocationAsync();
            var index = (int)await GetIndexAsync();
            Volatile.Write(ref currentLocation, location is { Length: > 0 } ? location : null);
            Volatile.Write(ref currentIndex, index);
            LocationChanged?.Invoke(CurrentLocation, index);
        }
    }

    [JSImport("waitForNavigation", "BrowserNavigation")]
    private static partial Task WaitForNavigationAsync();

    [JSImport("getLocation", "BrowserNavigation")]
    private static partial Task<string?> GetLocationAsync();

    [JSImport("getIndex", "BrowserNavigation")]
    private static partial Task<double> GetIndexAsync();

    [JSImport("push", "BrowserNavigation")]
    private static partial Task PushEntryAsync(string location, int index);

    [JSImport("replace", "BrowserNavigation")]
    private static partial Task ReplaceEntryAsync(string location, int index);

    [JSImport("go", "BrowserNavigation")]
    private static partial Task GoToAsync(int delta);
}
